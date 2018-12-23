import logging
import os
import subprocess
import threading
from time import sleep
from typing import Dict, List, Iterable, Callable
from wsgiref.simple_server import make_server

import yaml
from prometheus_client import make_wsgi_app, REGISTRY, Counter, Histogram, Gauge

import prometheus
from logscrape import StoppableThread, LocalLogThread, SSHLogThread
from parser import request_header_reader, label_reader, number_reader, clf_number_reader, Metric


def _get_logger():
    return logging.getLogger(__name__)


_active_targets = Gauge(name='scrape_targets_count', documentation='', labelnames=['type'])


class MetricsGCCaller(StoppableThread):
    def __init__(self, interval: float, threads: Dict[str, StoppableThread]):
        super().__init__()
        assert interval > 0
        self._interval = interval
        self._threads = threads

    def run(self):
        while not self.stop_me:
            sleep(self._interval)
            _get_logger().info('Doing metrics garbage collection')
            prometheus.gc()
            active = 0
            inactive = 0
            for thread in self._threads.values():
                if thread.is_alive():
                    active += 1
                else:
                    inactive += 1

            _active_targets.labels(type='active').set(active)
            _active_targets.labels(type='inactive').set(inactive)


def _read_core_config():
    scrape_config_filename = os.environ.get('SCRAPECONFIG', 'scrapeconfig.yml')
    scrape_config = yaml.load(open(scrape_config_filename))
    readers = _load_readers_config(scrape_config)

    config_reload_interval = scrape_config.get('reload-interval', None)
    if config_reload_interval is not None:
        config_reload_interval = float(config_reload_interval)

    scrape_config_script = scrape_config.get('script', None)

    return readers, scrape_config_script, config_reload_interval, scrape_config


_script_load_counter = Counter(name='script_load_events', documentation='', labelnames=['type'])
_script_load_timer = Histogram(name='script_execution_time', documentation='')


def _load_from_script(threads: Dict[str, StoppableThread], scrape_config_script: str, readers):
    try:
        with _script_load_timer.time():
            script_result = subprocess.check_output(scrape_config_script, shell=True, timeout=60).decode(
                'utf-8')  # type: str
        scrape_config = yaml.load(script_result)
        _script_load_counter.labels(type='success').inc()
    except:
        _script_load_counter.labels(type='error').inc()
        _get_logger().error('Failed to read configuration from script "{}"'.format(scrape_config_script),
                            exc_info=True)
        return

    _load_scrapers_config(threads, scrape_config, readers)


def _load_local_scrapers_config(threads: Dict[str, StoppableThread], config: Iterable[Dict],
                                readers: List[Callable[[Metric, str], None]]) -> List[str]:
    ids = []
    for entry in config:
        target_id = 'local://{}'.format(entry['path'])
        ids.append(target_id)
        if target_id in threads:
            _get_logger().warning('Ignoring duplicate scrape target "{}"'.format(target_id))
            continue
        threads[target_id] = LocalLogThread(filename=entry['path'],
                                            environment=entry.get('environment', None),
                                            readers=readers)
    return ids


def _load_ssh_scrapers_config(threads: Dict[str, StoppableThread],
                              config: Dict[str, Dict],
                              readers: List[Callable[[Metric, str], None]]) -> List[str]:
    ids = []
    default_file = config.get('file', None)
    default_user = config.get('user', None)
    default_password = config.get('password', None)
    default_connect_timeout = config.get('connect-timeout', None)
    for env_name, env_config in config['environments'].items():
        hosts = env_config['hosts']
        if not isinstance(hosts, list):
            hosts = [hosts]

        for host in hosts:
            target_id = 'ssh://{}/{}'.format(host, env_config.get('file', default_file))
            ids.append(target_id)
            if target_id in threads:
                _get_logger().warning('Ignoring duplicate scrape target "{}"'.format(target_id))
                continue
            threads[target_id] = SSHLogThread(
                filename=env_config.get('file', default_file),
                environment=env_name,
                readers=readers,
                host=host,
                user=env_config.get('user', default_user),
                password=env_config.get('password', default_password),
                connect_timeout=env_config.get('connect-timeout', default_connect_timeout)
            )
    return ids


def _load_readers_config(scrape_config: Dict) -> List[Callable[[Metric, str], None]]:
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

    prometheus.set_prefix(scrape_config['global']['prefix'])
    prometheus.set_ttl(scrape_config['global']['ttl'])

    return readers


def _load_scrapers_config(threads: Dict[str, StoppableThread], scrape_config: Dict,
                          readers: List[Callable[[Metric, str], None]]):
    loaded_ids = []
    if 'local' in scrape_config:
        loaded_ids.extend(_load_local_scrapers_config(threads, scrape_config['local'], readers))
    if 'ssh' in scrape_config:
        loaded_ids.extend(_load_ssh_scrapers_config(threads, scrape_config['ssh'], readers))

    stop_threads = []
    for thread_id, thread in threads.items():
        if thread_id not in loaded_ids:
            _get_logger().info('Scrape target "{}" will be removed'.format(thread_id))
            thread.stop_me = True
            stop_threads.append(thread)
        elif not thread.is_alive():
            _get_logger().info('New scrape target "{}" added'.format(thread_id))
            thread.start()

    for thread in stop_threads:
        thread.join()


logging.basicConfig(level=logging.INFO, format='%(asctime)-15s %(levelname)-8s [%(module)s] %(message)s')


def serve_me():
    app = make_wsgi_app(REGISTRY)
    httpd = make_server('', 5000, app)
    server_thread = threading.Thread(target=httpd.serve_forever)
    server_thread.start()

    readers, scrape_config_script, config_reload_interval, scrape_config = _read_core_config()

    threads = {}
    _load_scrapers_config(threads, scrape_config, readers)

    gc_thread = MetricsGCCaller(float(scrape_config['global']['ttl']), threads)
    gc_thread.start()

    while True:
        if scrape_config_script is not None:
            _load_from_script(threads, scrape_config_script, readers)
        if config_reload_interval is None:
            config_reload_interval = 86400 * 365  # sleep for a year... should be enough
        sleep(config_reload_interval)


serve_me()
