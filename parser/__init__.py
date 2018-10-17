import csv
from typing import Dict, Union, List, Iterable, Optional, Callable


class ParserError(Exception):
    pass


class Metric:
    """
    A collection of metrics extracted from a single log line.
    """

    def __init__(self, labels: Dict[str, str]):
        """
        :param labels: Additional metric labels; can be overwritten from the log line.
        """
        self.labels = labels  # type: Dict[str, str]
        self.metrics = dict()  # type: Dict[str, Union[int, float]]

    def get_label_str(self):
        return ','.join('{}="{}"'.format(k, v.replace('"', '\\"')) for k, v in self.labels.items())

    def get_metrics(self) -> Dict[str, Union[int, float]]:
        label_str = self.get_label_str()
        return {
            '{}{{{}}}'.format(k, label_str): v
            for k, v in self.metrics.items()
        }


def label_reader(name: str) -> Callable[[Metric, str], None]:
    def p(entry: Metric, value: str):
        entry.labels[name] = value

    return p


def request_header_reader() -> Callable[[Metric, str], None]:
    def p(entry: Metric, value: str):
        request = value.split()
        if len(request) != 3:
            raise ParserError()

        entry.labels['request_method'] = request[0]
        entry.labels['request_uri'] = request[1]
        entry.labels['request_http_version'] = request[2]

    return p


def int_reader(name: str) -> Callable[[Metric, str], None]:
    def p(entry: Metric, value: str):
        try:
            entry.metrics[name] = int(value)
        except:
            raise ParserError()

    return p


def clf_int_reader(name: str) -> Callable[[Metric, str], None]:
    def p(entry: Metric, value: str):
        if value == '-':
            entry.metrics[name] = 0
            return

        try:
            entry.metrics[name] = int(value)
        except:
            raise ParserError()

    return p


def float_reader(name: str) -> Callable[[Metric, str], None]:
    def p(entry: Metric, value: str):
        try:
            entry.metrics[name] = float(value)
        except:
            raise ParserError()

    return p


class LogParser:
    def convert_csv_line(self, line: List[str], labels: Dict[str, str]) -> Metric:
        if len(self._readers) != len(line):
            raise ParserError()

        result = Metric(labels)
        for reader, column in zip(self._readers, line):
            if reader is not None:
                reader(result, column)

        return result

    def __init__(self, file: Iterable[str], readers: List[Callable[[Metric, str], None]], labels: Dict[str, str]):
        self._reader = csv.reader(file, delimiter=' ', doublequote=False, strict=True)
        self._readers = readers
        self._labels = labels

    def read_all(self) -> Iterable[Optional[Metric]]:
        for line in self._reader:
            try:
                yield self.convert_csv_line(line, self._labels)
            except ParserError:
                yield None
