import re
from typing import Union, Dict
from prometheus_client import Counter
from prometheus_client.core import _LabelWrapper


class MetricsCollection:
    def __init__(self, prefix: str):
        if re.fullmatch('[a-zA-Z_:][a-zA-Z0-9_:]*', prefix) is None:
            raise RuntimeError('Invalid metrics prefix')

        self._counters = dict()  # type: Dict[str, _LabelWrapper]
        self._prefix = prefix

    def inc_counter(self, name: str, documentation: str, labels: Dict[str, str], amount: Union[int, float] = 1):
        full_name = '{}:{}'.format(self._prefix, name)

        if full_name not in self._counters:
            self._counters[full_name] = Counter(full_name, documentation, labels.keys())
        self._counters[full_name].labels(**labels).inc(amount)
