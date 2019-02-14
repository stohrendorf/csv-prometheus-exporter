import _thread
import logging
import socket
import subprocess
import threading
from abc import ABC, abstractmethod
from time import sleep
from typing import List, Callable, IO, Dict

import paramiko
from prometheus_client import Counter

import prometheus
from parser import Metric, LogParser

_READ_TIMEOUT = 30
_in_bytes = Counter('in_bytes', 'Amount of bytes read from remote', ['environment'])  # type: Counter


def _get_logger():
    return logging.getLogger(__name__)


def _parse_file(stdout: IO,
                environment: str,
                readers: List[Callable[[Metric, str], None]],
                histograms: Dict[str, List[float]]):
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
            if name not in histograms:
                prometheus.inc_counter(
                    name=name,
                    documentation='Sum of "{}"'.format(name),
                    labels=entry.labels,
                    amount=amount
                )
            else:
                prometheus.observe(
                    name=name,
                    documentation='Histogram of "{}"'.format(name),
                    labels=entry.labels,
                    amount=amount,
                    buckets=histograms[name]
                )


class StoppableThread(threading.Thread):
    def __init__(self):
        super().__init__()
        self.stop_me = False


class LogThread(StoppableThread, ABC):
    def __init__(self):
        super().__init__()
        self._connected = False

    @property
    def is_connected(self) -> bool:
        return self._connected

    @property
    @abstractmethod
    def host(self) -> str:
        raise NotImplementedError()

    @property
    @abstractmethod
    def environment(self) -> str:
        raise NotImplementedError()


class LocalLogThread(LogThread):
    def __init__(self, filename: str, environment: str, readers: List[Callable[[Metric, str], None]],
                 histograms: Dict[str, List[float]]):
        super().__init__()
        self._filename = filename
        self._environment = environment
        self._readers = readers
        self._histograms = histograms

    @property
    def host(self):
        return self._filename

    @property
    def environment(self):
        return self._environment

    def run(self):
        while not self.stop_me:
            self._connected = False
            try:
                with subprocess.Popen(args=['tail', '-F', '-n0', self._filename],
                                      stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL,
                                      universal_newlines=True,
                                      timeout=_READ_TIMEOUT) as process:
                    self._connected = True
                    _parse_file(process.stdout, self._environment, self._readers, self._histograms)
            except subprocess.TimeoutExpired:
                pass
            except:
                self._connected = False
                _get_logger().warning('Failed to tail {}'.format(self._filename), exc_info=True)
                sleep(5)


class SSHLogThread(LogThread):
    def __init__(self,
                 filename: str,
                 environment: str,
                 readers: List[Callable[[Metric, str], None]],
                 host: str,
                 user: str,
                 password: str,
                 pkey: str,
                 connect_timeout: float,
                 histograms: Dict[str, List[float]]):
        super().__init__()
        self._filename = filename
        self._host = host
        self._user = user
        self._password = password
        self._pkey = pkey
        self._environment = environment
        self._readers = readers
        if connect_timeout is None:
            self._connect_timeout = None
        elif isinstance(connect_timeout, float):
            self._connect_timeout = connect_timeout
        else:
            self._connect_timeout = float(connect_timeout)
        self._histograms = histograms

    @property
    def host(self):
        return self._host

    @property
    def environment(self):
        return self._environment

    def run(self):
        try:
            while not self.stop_me:
                self._connected = False
                with paramiko.SSHClient() as client:
                    client.load_system_host_keys()
                    client.set_missing_host_key_policy(paramiko.WarningPolicy())
                    try:
                        client.connect(hostname=self._host, port=22,
                                       username=self._user, password=self._password,
                                       key_filename=self._pkey,
                                       timeout=self._connect_timeout)
                    except socket.timeout:
                        _get_logger().warning("Connect attempt to {} timed out, retrying".format(self._host))
                        continue
                    except (socket.error, paramiko.SSHException):
                        _get_logger().warning("Connect attempt to {} failed, not trying again".format(self._host),
                                              exc_info=True)
                        break

                    self._connected = True
                    try:
                        ssh_stdin, ssh_stdout, ssh_stderr = client.exec_command(
                            'tail -n0 -F "{}" 2>/dev/null'.format(self._filename),
                            bufsize=1, timeout=_READ_TIMEOUT)
                        _parse_file(ssh_stdout, self._environment, self._readers, self._histograms)
                    except socket.timeout:
                        continue
                    self._connected = False
                    sleep(1)
        except:
            _get_logger().warning("SSH failure", exc_info=True)
            _thread.interrupt_main()
            exit(1)
