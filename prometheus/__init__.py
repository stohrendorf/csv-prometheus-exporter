import re
from typing import Union, Dict


class MetricsCollection:
    def __init__(self, prefix: str):
        if re.fullmatch('[a-z][a-z0-9_]*', prefix) is None:
            raise RuntimeError('Invalid metrics prefix')

        self._metrics = dict()  # type: Dict[str, Union[int, float]]
        self._prefix = prefix

    def add(self, name: str, value: Union[float, str]):
        if name not in self._metrics:
            self._metrics[name] = value
        else:
            self._metrics[name] += value

    def to_text(self) -> str:
        return '\n'.join(['{}:{} {}'.format(self._prefix, a, b) for a, b in self._metrics.items()])
