import _thread
import logging
import os
import socket
import subprocess
import threading
from time import sleep
from typing import IO, Dict, List, Callable
from wsgiref.simple_server import make_server

import paramiko
import yaml
from prometheus_client import make_wsgi_app, REGISTRY

from parser import LogParser, request_header_reader, label_reader, number_reader, clf_number_reader, Metric
from prometheus import MetricsCollection

scrape_config = yaml.load(open(os.environ.get('SCRAPECONFIG', 'scrapeconfig.yml')))


def _parse_file(stdout: IO, metrics: MetricsCollection, environment: str, readers: List[Callable[[Metric, str], None]]):
    if not environment:
        environment = 'N/A'

    for entry in LogParser(stdout, readers, {'environment': environment}).read_all():
        if entry is None:
            metrics.inc_counter(name='parser_errors',
                                documentation='Number of lines which could not be parsed',
                                labels={'environment': environment})
            continue

        metrics.inc_counter(name='lines_parsed',
                            documentation='Number of successfully parsed lines',
                            labels=entry.labels)

        for name, amount in entry.metrics.items():
            metrics.inc_counter(
                name=name,
                documentation='Sum of "{}"'.format(name),
                labels=entry.labels,
                amount=amount
            )


class MetricsGCCaller(threading.Thread):
    def __init__(self, metrics: MetricsCollection, interval: float):
        super().__init__()
        assert interval > 0
        self._metrics = metrics
        self._interval = interval

    def run(self):
        while True:
            sleep(self._interval)
            logging.getLogger().info('Doing metrics garbage collection')
            self._metrics.gc()


class LocalLogThread(threading.Thread):
    def __init__(self, metrics: MetricsCollection, filename: str, environment: str,
                 readers: List[Callable[[Metric, str], None]]):
        super().__init__()
        self._metrics = metrics
        self._filename = filename
        self._environment = environment
        self._readers = readers

    def run(self):
        while True:
            try:
                with subprocess.Popen(args=['tail', '-F', '-n0', self._filename],
                                      stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL,
                                      universal_newlines=True) as process:
                    _parse_file(process.stdout, self._metrics, self._environment, self._readers)
            except Exception as e:
                logging.getLogger().warning('Failed to tail {}'.format(self._filename), exc_info=e)
                sleep(5)


class SSHLogThread(threading.Thread):
    def __init__(self, metrics: MetricsCollection, filename: str, environment: str,
                 readers: List[Callable[[Metric, str], None]], host: str, user: str, password: str,
                 connect_timeout: float):
        super().__init__()
        self._metrics = metrics
        self._filename = filename
        self._host = host
        self._user = user
        self._password = password
        self._environment = environment
        self._readers = readers
        if connect_timeout is None:
            self._connect_timeout = None
        elif isinstance(connect_timeout, float):
            self._connect_timeout = connect_timeout
        else:
            self._connect_timeout = float(connect_timeout)

    def run(self):
        try:
            while True:
                with paramiko.SSHClient() as client:
                    client.load_system_host_keys()
                    client.set_missing_host_key_policy(paramiko.WarningPolicy())
                    try:
                        client.connect(hostname=self._host, port=22,
                                       username=self._user, password=self._password,
                                       timeout=self._connect_timeout)
                    except socket.timeout:
                        logging.getLogger().warning("Connect attempt to {} timed out, retrying".format(self._host))
                        continue
                    except (socket.error, paramiko.SSHException) as e:
                        logging.getLogger().warning("Connect attempt to {} failed, not trying again".format(self._host),
                                                    exc_info=e)
                        break
                    ssh_stdin, ssh_stdout, ssh_stderr = client.exec_command(
                        'tail -n0 -F "{}" 2>/dev/null'.format(self._filename),
                        bufsize=1)

                    _parse_file(ssh_stdout, self._metrics, self._environment, self._readers)
                    sleep(1)
        except Exception as e:
            logging.getLogger().warning("SSH failure", exc_info=e)
            _thread.interrupt_main()
            exit(1)


def parse_config():
    prefix = scrape_config['global']['prefix']
    readers = []
    for fmt_entry in scrape_config['global']['format']:
        if fmt_entry is None:
            readers.append(None)
            continue

        assert isinstance(fmt_entry, Dict)
        assert len(fmt_entry) == 1
        name, tp = next(iter(fmt_entry.items()))
        if tp is None:
            readers.append(None)
            continue

        if tp == 'label' and name == 'environment':
            raise ValueError("'environment' is a reserved label name")
        elif tp != 'label' and name in ('parser_errors', 'lines_parsed'):
            raise ValueError("'{}' is a reserved metric name".format(name))

        reader = {
            'number': number_reader,
            'clf_number': clf_number_reader,
            'request_header': lambda _: request_header_reader(),
            'label': label_reader
        }[tp](name)
        readers.append(reader)

    ttl = scrape_config['global']['ttl']
    metrics = MetricsCollection(prefix, ttl)

    threads = [MetricsGCCaller(metrics, ttl)]

    if 'local' in scrape_config:
        for file in scrape_config['local']:
            threads.append(
                LocalLogThread(filename=file['path'], metrics=metrics, environment=file.get('environment', None),
                               readers=readers))

    if 'ssh' in scrape_config:
        default_file = scrape_config['ssh'].get('file', None)
        default_user = scrape_config['ssh'].get('user', None)
        default_password = scrape_config['ssh'].get('password', None)
        default_connect_timeout = scrape_config['ssh'].get('connect-timeout', None)
        for env_name, env_config in scrape_config['ssh']['environments'].items():
            hosts = env_config['hosts']
            if not isinstance(hosts, list):
                hosts = [hosts]

            for host in hosts:
                threads.append(
                    SSHLogThread(
                        filename=env_config.get('file', default_file),
                        metrics=metrics,
                        environment=env_name,
                        readers=readers,
                        host=host,
                        user=env_config.get('user', default_user),
                        password=env_config.get('password', default_password),
                        connect_timeout=env_config.get('connect-timeout', default_connect_timeout))
                )

    for thread in threads:
        thread.daemon = True
        thread.start()


logging.basicConfig(level=logging.INFO, format='%(asctime)-15s %(levelname)-8s [%(module)s] %(message)s')
parse_config()


def serve_me():
    app = make_wsgi_app(REGISTRY)
    httpd = make_server('', 5000, app)
    t = threading.Thread(target=httpd.serve_forever)
    t.start()


serve_me()
