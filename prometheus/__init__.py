import logging
import re
import time
from threading import RLock
from typing import Union, Dict, Tuple, FrozenSet
from prometheus_client import Counter, Summary, REGISTRY
from prometheus_client.core import Gauge
from prometheus_client.metrics import MetricWrapperBase

_active_metrics = Gauge(name='scraper_active_metrics',
                        documentation='Number of non-stale (tracked) metrics')
_gc_duration = Summary('scraper_gc_duration_seconds',
                       documentation='Duration of stale metric removal')


def _get_logger():
    return logging.getLogger(__name__)


class _MetricGC:
    def __init__(self, ttl: float):
        assert ttl > 0
        self._ttl = ttl
        # (metric_name, labels) = (counter, last_usage)
        self._metrics_ttl = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[MetricWrapperBase, float] ]
        self._metrics = {}  # type: Dict[ str, MetricWrapperBase ]
        self._lock = RLock()

    def gc(self):
        with _gc_duration.time():
            now = time.time()
            with self._lock:
                cleaned = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[MetricWrapperBase, float] ]
                dropped = 0
                for key, metric_ttl in self._metrics_ttl.items():
                    metric = metric_ttl[0]
                    last_used = metric_ttl[1]
                    if last_used + self._ttl < now:
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
        with self._lock:
            counter = self._metrics.get(full_name, None)
            if counter is None:
                counter = Counter(full_name, documentation, labels.keys())
                self._metrics[full_name] = counter

            key = (full_name, frozenset(labels.items()))
            if key not in self._metrics_ttl:
                _active_metrics.inc()
            self._metrics_ttl[key] = (counter, time.time())
            counter.labels(**labels).inc(amount)

    def set_gauge(self, full_name: str, documentation: str, labels: Dict[str, str], value: float):
        with self._lock:
            gauge = self._metrics.get(full_name, None)
            if gauge is None:
                gauge = Gauge(full_name, documentation, labels.keys())
                self._metrics[full_name] = gauge

            key = (full_name, frozenset(labels.items()))
            if key not in self._metrics_ttl:
                _active_metrics.inc()
            self._metrics_ttl[key] = (gauge, time.time())
            gauge.labels(**labels).set(value)

    def clear(self):
        with self._lock:
            for metric in self._metrics.values():
                REGISTRY.unregister(metric)
            self._metrics = {}

    def set_ttl(self, ttl):
        assert ttl > 0
        self._ttl = ttl


_metrics = _MetricGC(1)

_metrics_prefix = None


def gc():
    _metrics.gc()


def set_prefix(prefix: str):
    if re.fullmatch('[a-zA-Z_:][a-zA-Z0-9_:]*', prefix) is None:
        raise RuntimeError('Invalid metrics prefix')

    global _metrics_prefix
    _metrics_prefix = prefix


def set_ttl(ttl: float):
    _metrics.set_ttl(ttl)


def inc_counter(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float] = 1):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _metrics.inc(full_name, documentation, labels, amount)


def set_gauge(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float]):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _metrics.set_gauge(full_name, documentation, labels, amount)
