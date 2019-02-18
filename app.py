import logging
import os
import subprocess
import threading
from time import sleep
from typing import Dict, List, Iterable, Callable, Tuple
from urllib.parse import parse_qs
from wsgiref.simple_server import make_server

import yaml
from prometheus_client import Counter, Histogram, Gauge
from prometheus_client.exposition import choose_encoder
from prometheus_client.utils import INF

import prometheus
from logscrape import StoppableThread, LocalLogThread, SSHLogThread, LogThread
from parser import request_header_reader, label_reader, number_reader, clf_number_reader, Metric
from prometheus import set_gauge, ENVIRONMENT, registries, PARSER_ERRORS, LINES_PARSED


def _get_logger():
    return logging.getLogger(__name__)


_active_targets = Gauge(name='scrape_targets_count', documentation='', labelnames=['type'])


class MetricsGCCaller(StoppableThread):
    def __init__(self, interval: float, threads: Dict[str, LogThread]):
        super().__init__()
        assert interval > 0
        self._interval = interval
        self._threads = threads

    def run(self):
        while not self.stop_me:
            sleep(self._interval)
            _get_logger().info('Doing metrics garbage collection')
            prometheus.gc()
            connected = 0
            disconnected = 0
            for thread in self._threads.values():
                if thread.is_connected:
                    connected += 1
                else:
                    disconnected += 1
                    set_gauge("target_disconnected", "Marks a target as disconnected if not zero", {
                        'host': thread.host,
                        ENVIRONMENT: thread.environment
                    }, 1)

            _active_targets.labels(type='connected').set(connected)
            _active_targets.labels(type='disconnected').set(disconnected)


def _read_core_config():
    scrape_config_filename = os.environ.get('SCRAPECONFIG', 'scrapeconfig.yml')
    scrape_config = yaml.load(open(scrape_config_filename))
    readers, histograms = _load_readers_config(scrape_config)

    config_reload_interval = scrape_config.get('reload-interval', None)
    if config_reload_interval is not None:
        config_reload_interval = float(config_reload_interval)

    scrape_config_script = scrape_config.get('script', None)

    return readers, scrape_config_script, config_reload_interval, scrape_config, histograms


_script_load_counter = Counter(name='script_load_events', documentation='', labelnames=['type'])
_script_load_timer = Histogram(name='script_execution_time', documentation='')


def _load_from_script(threads: Dict[str, LogThread], scrape_config_script: str, readers, histograms):
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

    _load_scrapers_config(threads, scrape_config, readers, histograms)


def _load_local_scrapers_config(threads: Dict[str, LogThread], config: Iterable[Dict],
                                readers: List[Callable[[Metric, str], None]],
                                histograms) -> List[str]:
    ids = []
    for entry in config:
        target_id = 'local://{}'.format(entry['path'])
        ids.append(target_id)
        if target_id in threads:
            _get_logger().debug('Ignoring duplicate scrape target "{}"'.format(target_id))
            continue
        threads[target_id] = thread = LocalLogThread(filename=entry['path'],
                                                     environment=entry.get('environment', None),
                                                     readers=readers,
                                                     histograms=histograms)
        thread.start()
        _get_logger().info('New scrape target "{}" added'.format(target_id))
    return ids


def _load_ssh_scrapers_config(threads: Dict[str, LogThread],
                              config: Dict[str, Dict],
                              readers: List[Callable[[Metric, str], None]],
                              histograms: Dict[str, List[float]]) -> List[str]:
    ids = []
    default_file = config.get('file', None)
    default_user = config.get('user', None)
    default_password = config.get('password', None)
    default_pkey = config.get('pkey', None)
    default_connect_timeout = config.get('connect-timeout', None)

    for env_name, env_config in config['environments'].items():
        hosts = env_config['hosts']
        if not isinstance(hosts, list):
            hosts = [hosts]

        for host in hosts:
            target_id = 'ssh://{}/{}'.format(host, env_config.get('file', default_file))
            ids.append(target_id)
            if target_id in threads:
                _get_logger().debug('Ignoring duplicate scrape target "{}"'.format(target_id))
                continue
            threads[target_id] = thread = SSHLogThread(
                filename=env_config.get('file', default_file),
                environment=env_name,
                readers=readers,
                host=host,
                user=env_config.get('user', default_user),
                password=env_config.get('password', default_password),
                pkey=env_config.get('pkey', default_pkey),
                connect_timeout=env_config.get('connect-timeout', default_connect_timeout),
                histograms=histograms
            )
            thread.start()
            _get_logger().info('New scrape target "{}" added'.format(target_id))
    return ids


def _load_readers_config(scrape_config: Dict) -> Tuple[List[Callable[[Metric, str], None]], Dict[str, List[float]]]:
    readers = []
    histogram_types = {}  # type: Dict[str, List[float]]
    for histogram_name, buckets in scrape_config['global'].get('histograms', {}).items():
        if histogram_name in histogram_types:
            raise RuntimeError('Duplicate histogram definition of {}'.format(histogram_name))
        if buckets is None or len(buckets) == 0:
            histogram_types[histogram_name] = Histogram.DEFAULT_BUCKETS
        else:
            histogram_types[histogram_name] = [float(x) for x in buckets]
        if histogram_types[histogram_name][-1] != INF:
            histogram_types[histogram_name].append(INF)

    histograms = {}  # type: Dict[str, List[float]]
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

        if tp == 'label' and name == ENVIRONMENT:
            raise ValueError("'{}' is a reserved label name".format(ENVIRONMENT))
        elif tp != 'label' and name in (PARSER_ERRORS, LINES_PARSED):
            raise ValueError("'{}' is a reserved metric name".format(name))

        if '+' in tp:
            tp_split = tp.split('+')
            tp = tp_split[0].strip()
            histogram_type = tp_split[1].strip()
            if histogram_type not in histogram_types:
                raise RuntimeError('Histogram type {} is not defined'.format(histogram_type))
            histograms[name] = histogram_types[histogram_type]

        reader = {
            'number': number_reader,
            'clf_number': clf_number_reader,
            'request_header': lambda _: request_header_reader(),
            'label': label_reader
        }[tp](name)
        readers.append(reader)

    prometheus.set_prefix(scrape_config['global']['prefix'])
    prometheus.set_ttl(scrape_config['global']['ttl'])

    return readers, histograms


def _load_scrapers_config(threads: Dict[str, LogThread], scrape_config: Dict,
                          readers: List[Callable[[Metric, str], None]],
                          histograms: Dict[str, List[float]]):
    loaded_ids = []
    if 'local' in scrape_config:
        loaded_ids.extend(_load_local_scrapers_config(threads, scrape_config['local'], readers, histograms))
    if 'ssh' in scrape_config:
        loaded_ids.extend(_load_ssh_scrapers_config(threads, scrape_config['ssh'], readers, histograms))

    stop_threads = []
    for thread_id, thread in threads.items():
        if thread_id not in loaded_ids:
            _get_logger().info('Scrape target "{}" will be removed'.format(thread_id))
            thread.stop_me = True
            stop_threads.append(thread)

    for thread in stop_threads:
        thread.join()


logging.basicConfig(format='%(asctime)-15s %(levelname)-8s [%(module)s] %(message)s')


def prometheus_app(environ, start_response):
    params = parse_qs(environ.get('QUERY_STRING', ''))
    output = []
    encoder, content_type = choose_encoder(environ.get('HTTP_ACCEPT'))
    for r in registries():
        if 'name[]' in params:
            r = r.restricted_registry(params['name[]'])
        output.append(encoder(r))

    status = str('200 OK')
    headers = [(str('Content-type'), content_type)]
    start_response(status, headers)
    return output


def serve_me():
    httpd = make_server('', 5000, prometheus_app)
    server_thread = threading.Thread(target=httpd.serve_forever)
    server_thread.start()

    readers, scrape_config_script, config_reload_interval, scrape_config, histograms = _read_core_config()

    threads = {}  # type: Dict[str, LogThread]
    _load_scrapers_config(threads, scrape_config, readers, histograms)

    gc_thread = MetricsGCCaller(float(scrape_config['global']['ttl']), threads)
    gc_thread.start()

    while True:
        if scrape_config_script is not None:
            _load_from_script(threads, scrape_config_script, readers, histograms)
        if config_reload_interval is None:
            config_reload_interval = 86400 * 365  # sleep for a year... should be enough
        sleep(config_reload_interval)


serve_me()
