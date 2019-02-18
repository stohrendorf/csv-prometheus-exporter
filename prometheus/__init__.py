import logging
import re
import threading
import time
from collections import defaultdict
from threading import RLock
from typing import Union, Dict, Tuple, FrozenSet, List

from prometheus_client import Counter, Summary, Histogram, CollectorRegistry, REGISTRY
from prometheus_client.core import Gauge
from prometheus_client.metrics import MetricWrapperBase

_active_metrics = Gauge(name='scraper_active_metrics',
                        documentation='Number of non-stale (tracked) metrics')
_gc_duration = Summary('scraper_gc_duration_seconds',
                       documentation='Duration of stale metric removal')

ENVIRONMENT = 'environment'
PARSER_ERRORS = 'parser_errors'
LINES_PARSED = 'lines_parsed'


def _get_logger():
    return logging.getLogger(__name__)


_current_ttl = 1.0


class _MetricGC:
    def __init__(self, registry: CollectorRegistry):
        # (metric_name, labels) = (counter, last_usage)
        self._metrics_ttl = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[MetricWrapperBase, float] ]
        self._metrics = defaultdict(dict)  # type: Dict[str, MetricWrapperBase]
        self._lock = RLock()
        self._registry = registry

    def gc(self):
        with _gc_duration.time():
            now = time.time()
            with self._lock:
                cleaned = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[MetricWrapperBase, float] ]
                dropped = 0
                for key, metric_ttl in self._metrics_ttl.items():
                    metric = metric_ttl[0]
                    last_used = metric_ttl[1]
                    if last_used + _current_ttl < now:
                        labels = dict(key[1])
                        values = map(lambda lbl: labels[lbl], metric._labelnames)
                        metric.remove(*values)
                        dropped += 1
                    else:
                        cleaned[key] = metric_ttl
                self._metrics_ttl = cleaned

            _active_metrics.set(len(self._metrics_ttl))
            if dropped > 0:
                _get_logger().info('Dropped {} metric(s) due to exceeding TTL'.format(dropped))

    def inc(self, full_name: str, documentation: str, labels: Dict[str, str], amount: float):
        counter = self._metrics.get(full_name, None)
        if counter is None:
            counter = Counter(full_name, documentation, labels.keys(),
                              registry=self._registry)
            self._metrics[full_name] = counter

        key = (full_name, frozenset(labels.items()))
        with self._lock:
            if key not in self._metrics_ttl:
                _active_metrics.inc()
            self._metrics_ttl[key] = (counter, time.time())
            counter.labels(**labels).inc(amount)

    def set_gauge(self, full_name: str, documentation: str, labels: Dict[str, str], value: float):
        gauge = self._metrics.get(full_name, None)
        if gauge is None:
            gauge = Gauge(full_name, documentation, labels.keys(), registry=self._registry)
            self._metrics[full_name] = gauge

        key = (full_name, frozenset(labels.items()))
        with self._lock:
            if key not in self._metrics_ttl:
                _active_metrics.inc()
            self._metrics_ttl[key] = (gauge, time.time())
            gauge.labels(**labels).set(value)

    def observe(self, full_name: str, documentation: str, labels: Dict[str, str], value: float, buckets: List[float]):
        histogram = self._metrics.get(full_name, None)
        if histogram is None:
            histogram = Histogram(full_name, documentation, labels.keys(), buckets=buckets,
                                  registry=self._registry)
            self._metrics[full_name] = histogram

        key = (full_name, frozenset(labels.items()))
        if key not in self._metrics_ttl:
            _active_metrics.inc()
            with self._lock:
                self._metrics_ttl[key] = (histogram, time.time())
                histogram.labels(**labels).observe(value)

    @property
    def registry(self):
        return self._registry


# https://www.oreilly.com/library/view/python-cookbook/0596001673/ch06s04.html
class ReadWriteLock:
    """ A lock object that allows many simultaneous "read locks", but
    only one "write lock." """

    def __init__(self):
        self._read_ready = threading.Condition(threading.RLock())
        self._readers = 0

    def acquire_read(self):
        """ Acquire a read lock. Blocks only if a thread has
        acquired the write lock. """
        self._read_ready.acquire()
        try:
            self._readers += 1
        finally:
            self._read_ready.release()

    def release_read(self):
        """ Release a read lock. """
        self._read_ready.acquire()
        try:
            self._readers -= 1
            if not self._readers:
                self._read_ready.notifyAll()
        finally:
            self._read_ready.release()

    def acquire_write(self):
        """ Acquire a write lock. Blocks until there are no
        acquired read or write locks. """
        self._read_ready.acquire()
        while self._readers > 0:
            self._read_ready.wait()

    def release_write(self):
        """ Release a write lock. """
        self._read_ready.release()


_metrics_lock = ReadWriteLock()
_metrics = defaultdict(lambda: _MetricGC(CollectorRegistry(auto_describe=True)))


def _get_metrics(key: str):
    _metrics_lock.acquire_read()
    try:
        if key in _metrics:
            return _metrics[key]
    finally:
        _metrics_lock.release_read()

    _metrics_lock.acquire_write()
    try:
        return _metrics[key]
    finally:
        _metrics_lock.release_write()


_metrics_prefix = None


def gc():
    _metrics_lock.acquire_read()
    try:
        tmp = list(_metrics.values())
    finally:
        _metrics_lock.release_read()
    for r in tmp:
        r.gc()


def set_prefix(prefix: str):
    if re.fullmatch('[a-zA-Z_:][a-zA-Z0-9_:]*', prefix) is None:
        raise RuntimeError('Invalid metrics prefix')

    global _metrics_prefix
    _metrics_prefix = prefix


def set_ttl(ttl: float):
    assert ttl > 0

    global _current_ttl
    _current_ttl = ttl


def inc_counter(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float] = 1):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _get_metrics(labels[ENVIRONMENT]).inc(full_name, documentation, labels, amount)


def set_gauge(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float]):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _get_metrics(labels[ENVIRONMENT]).set_gauge(full_name, documentation, labels, amount)


def observe(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float], buckets: List[float]):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _get_metrics(labels[ENVIRONMENT]).observe(full_name, documentation, labels, amount, buckets)


def registries():
    _metrics_lock.acquire_read()
    try:
        m = list(_metrics.values())
    finally:
        _metrics_lock.release_read()
    return [r.registry for r in m] + [REGISTRY]
