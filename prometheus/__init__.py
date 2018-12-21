import logging
import re
import time
from threading import RLock
from typing import Union, Dict, Tuple, FrozenSet
from prometheus_client import Counter, Summary, REGISTRY
from prometheus_client.core import Gauge

_active_metrics = Gauge(name='scraper_active_metrics',
                        documentation='Number of non-stale (tracked) metrics')
_gc_duration = Summary('scraper_gc_duration_seconds',
                       documentation='Duration of stale metric removal')


class _CounterGC:
    def __init__(self, ttl: float):
        assert ttl > 0
        self._ttl = ttl
        # (metric_name, labels) = (counter, last_usage)
        self._counters_ttl = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[Counter, float] ]
        self._counters = {}  # type: Dict[ str, Counter ]
        self._lock = RLock()

    def gc(self):
        with _gc_duration.time():
            now = time.time()
            with self._lock:
                cleaned = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[Counter, float] ]
                dropped = 0
                for key, counter_ttl in self._counters_ttl.items():
                    counter = counter_ttl[0]
                    last_used = counter_ttl[1]
                    if last_used + self._ttl < now:
                        labels = dict(key[1])
                        values = map(lambda lbl: labels[lbl], counter._labelnames)
                        counter.remove(*values)
                        dropped += 1
                    else:
                        cleaned[key] = counter_ttl
                self._counters_ttl = cleaned

            _active_metrics.set(len(self._counters_ttl))
            if dropped > 0:
                logging.getLogger().info('Dropped {} metric(s) due to exceeding TTL'.format(dropped))

    def inc(self, full_name: str, documentation: str, labels: Dict[str, str], amount: float):
        with self._lock:
            counter = self._counters.get(full_name, None)
            if counter is None:
                counter = Counter(full_name, documentation, labels.keys())  # type: Counter
                self._counters[full_name] = counter

            key = (full_name, frozenset(labels.items()))
            if key not in self._counters_ttl:
                _active_metrics.inc()
            self._counters_ttl[key] = (counter, time.time())
            counter.labels(**labels).inc(amount)

    def clear(self):
        with self._lock:
            for counter in self._counters.values():
                REGISTRY.unregister(counter)
            self._counters = {}

    def set_ttl(self, ttl):
        assert ttl > 0
        self._ttl = ttl


_counters = _CounterGC(1)

_metrics_prefix = None


def gc():
    _counters.gc()


def set_prefix(prefix: str):
    if re.fullmatch('[a-zA-Z_:][a-zA-Z0-9_:]*', prefix) is None:
        raise RuntimeError('Invalid metrics prefix')

    global _metrics_prefix
    _metrics_prefix = prefix


def set_ttl(ttl: float):
    _counters.set_ttl(ttl)


def inc_counter(name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float] = 1):
    full_name = '{}:{}'.format(_metrics_prefix, name)
    _counters.inc(full_name, documentation, labels, amount)
