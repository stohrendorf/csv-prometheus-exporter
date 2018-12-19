import _thread
import logging
import os
import socket
import subprocess
import threading
from time import sleep
from typing import IO, Dict, List, Callable, Tuple, Optional
from wsgiref.simple_server import make_server

import paramiko
import yaml
from prometheus_client import make_wsgi_app, REGISTRY, Counter
from prometheus_client.core import _LabelWrapper

from parser import LogParser, request_header_reader, label_reader, number_reader, clf_number_reader, Metric
import prometheus

_in_bytes = Counter('in_bytes', 'Amount of bytes read from remote', ['environment'])  # type: _LabelWrapper

_READ_TIMEOUT = 5


def _parse_file(stdout: IO, environment: str, readers: List[Callable[[Metric, str], None]]):
    if not environment:
        environment = 'N/A'

    prev_pos = stdout.tell()
    in_bytes_env = _in_bytes.labels(environment=environment)  # type: Counter
    for entry in LogParser(stdout, readers, {'environment': environment}).read_all():
        current_pos = stdout.tell()
        in_bytes_env.inc(current_pos - prev_pos)
        prev_pos = current_pos

        if entry is None:
            prometheus.inc_counter(name='parser_errors',
                                   documentation='Number of lines which could not be parsed',
                                   labels={'environment': environment})
            continue

        prometheus.inc_counter(name='lines_parsed',
                               documentation='Number of successfully parsed lines',
                               labels=entry.labels)

        for name, amount in entry.metrics.items():
            prometheus.inc_counter(
                name=name,
                documentation='Sum of "{}"'.format(name),
                labels=entry.labels,
                amount=amount
            )


class StoppableThread(threading.Thread):
    def __init__(self):
        super().__init__()
        self.stop_me = False


class MetricsGCCaller(StoppableThread):
    def __init__(self, interval: float):
        super().__init__()
        assert interval > 0
        self._interval = interval

    def run(self):
        while not self.stop_me:
            sleep(self._interval)
            logging.getLogger().info('Doing metrics garbage collection')
            prometheus.gc()


class LocalLogThread(StoppableThread):
    def __init__(self, filename: str, environment: str, readers: List[Callable[[Metric, str], None]]):
        super().__init__()
        self._filename = filename
        self._environment = environment
        self._readers = readers

    def run(self):
        while not self.stop_me:
            try:
                with subprocess.Popen(args=['tail', '-F', '-n0', self._filename],
                                      stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL,
                                      universal_newlines=True,
                                      timeout=_READ_TIMEOUT) as process:
                    _parse_file(process.stdout, self._environment, self._readers)
            except subprocess.TimeoutExpired:
                pass
            except Exception as e:
                logging.getLogger().warning('Failed to tail {}'.format(self._filename), exc_info=e)
                sleep(5)


class SSHLogThread(StoppableThread):
    def __init__(self,
                 filename: str,
                 environment: str,
                 readers: List[Callable[[Metric, str], None]],
                 host: str,
                 user: str,
                 password: str,
                 connect_timeout: float):
        super().__init__()
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
            while not self.stop_me:
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
                    try:
                        ssh_stdin, ssh_stdout, ssh_stderr = client.exec_command(
                            'tail -n0 -F "{}" 2>/dev/null'.format(self._filename),
                            bufsize=1, timeout=_READ_TIMEOUT)
                        _parse_file(ssh_stdout, self._environment, self._readers)
                    except socket.timeout:
                        continue
                    sleep(1)
        except Exception as e:
            logging.getLogger().warning("SSH failure", exc_info=e)
            _thread.interrupt_main()
            exit(1)


def _stop_all_threads(threads: List[StoppableThread]):
    for thread in threads:
        thread.stop_me = True
    for thread in threads:
        thread.join()


def read_and_load_config(threads: List[StoppableThread]) -> Tuple[List[StoppableThread], Optional[int]]:
    scrape_config_filename = os.environ.get('SCRAPECONFIG', 'scrapeconfig.yml')
    scrape_config = yaml.load(open(scrape_config_filename))
    config_reload_interval = scrape_config.get('reload-interval', None)
    if config_reload_interval is not None:
        config_reload_interval = int(config_reload_interval)

    scrape_config_script = scrape_config.get('script', None)
    if scrape_config_script is not None:
        logging.getLogger().info('Reading config from script {}...'.format(scrape_config_script))
        scrape_config_script = os.path.expandvars(scrape_config_script)
        script_result = subprocess.check_output(scrape_config_script, shell=True).decode(
            'utf-8')  # type: str

        _stop_all_threads(threads)
        return load_config(yaml.load(script_result)), config_reload_interval

    _stop_all_threads(threads)
    return load_config(scrape_config), config_reload_interval


def load_config(scrape_config: dict) -> List[StoppableThread]:
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
        elif tp != 'label' and name in ('parser_errors', 'lines_parsed', 'in_bytes'):
            raise ValueError("'{}' is a reserved metric name".format(name))

        reader = {
            'number': number_reader,
            'clf_number': clf_number_reader,
            'request_header': lambda _: request_header_reader(),
            'label': label_reader
        }[tp](name)
        readers.append(reader)

    ttl = scrape_config['global']['ttl']
    prometheus.set_prefix(prefix)
    prometheus.set_ttl(ttl)

    threads = []  # type: List[StoppableThread]

    if 'local' in scrape_config:
        for file in scrape_config['local']:
            threads.append(
                LocalLogThread(filename=file['path'], environment=file.get('environment', None), readers=readers))

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
                        environment=env_name,
                        readers=readers,
                        host=host,
                        user=env_config.get('user', default_user),
                        password=env_config.get('password', default_password),
                        connect_timeout=env_config.get('connect-timeout', default_connect_timeout))
                )

    for thread in threads:
        thread.start()

    return threads


logging.basicConfig(level=logging.INFO, format='%(asctime)-15s %(levelname)-8s [%(module)s] %(message)s')


def serve_me():
    app = make_wsgi_app(REGISTRY)
    httpd = make_server('', 5000, app)
    t = threading.Thread(target=httpd.serve_forever)
    t.start()

    while True:
        running_threads = []  # type: List[StoppableThread]
        running_threads, timeout = read_and_load_config(running_threads)

        if timeout is None:
            timeout = 86400 * 365  # sleep for a year... should be enough
        sleep(timeout)


serve_me()
