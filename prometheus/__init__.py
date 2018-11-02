import logging
import re
import time
from threading import RLock
from typing import Union, Dict, Tuple, FrozenSet
from prometheus_client import Counter, Summary
from prometheus_client.core import _LabelWrapper, Gauge


class _CounterGC:
    def __init__(self, ttl: float):
        assert ttl > 0
        self._ttl = ttl
        # (metric_name, labels) = (counter, last_usage)
        self._counters_ttl = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[_LabelWrapper, float] ]
        self._counters = {}  # type: Dict[ str, Tuple[_LabelWrapper, float] ]
        self._lock = RLock()
        self._active_metrics = Gauge(name='scraper_active_metrics',
                                     documentation='Number of non-stale (tracked) metrics')
        self._gc_duration = Summary('scraper_gc_duration_seconds',
                                    documentation='Duration of stale metric removal')

    def gc(self):
        with self._gc_duration.time():
            now = time.time()
            with self._lock:
                cleaned = {}  # type: Dict[ Tuple[str, FrozenSet[Tuple[str, str]]], Tuple[_LabelWrapper, float] ]
                dropped = 0
                for key, counter_ttl in self._counters_ttl.items():
                    counter = counter_ttl[0]
                    last_used = counter_ttl[1]
                    if last_used + self._ttl < now:
                        labels = dict(key[1])
                        values = [labels[lbl] for lbl in counter._labelnames]
                        counter.remove(*values)
                        counter.labels(**labels)
                        dropped += 1
                    else:
                        cleaned[key] = counter_ttl
                self._counters_ttl = cleaned

            self._active_metrics.set(len(self._counters_ttl))
            if dropped > 0:
                logging.getLogger().info('Dropped {} metric(s) due to exceeding TTL'.format(dropped))

    def inc(self, full_name: str, documentation: str, labels: Dict[str, str], amount: float):
        with self._lock:
            counter = self._counters.get(full_name, None)
            if counter is None:
                counter = Counter(full_name, documentation, labels.keys())  # type: _LabelWrapper
                self._counters[full_name] = counter

            key = (full_name, frozenset(labels.items()))
            if key not in self._counters_ttl:
                self._active_metrics.inc()
            self._counters_ttl[key] = (counter, time.time())
            counter.labels(**labels).inc(amount)


class MetricsCollection:
    def __init__(self, prefix: str, ttl: float):
        if re.fullmatch('[a-zA-Z_:][a-zA-Z0-9_:]*', prefix) is None:
            raise RuntimeError('Invalid metrics prefix')

        self._counters = _CounterGC(ttl)
        self._prefix = prefix

    def inc_counter(self, name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float] = 1):
        full_name = '{}:{}'.format(self._prefix, name)
        self._counters.inc(full_name, documentation, labels, amount)

    def gc(self):
        self._counters.gc()
