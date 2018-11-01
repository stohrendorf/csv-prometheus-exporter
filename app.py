import _thread
import glob
import logging
import os
import socket
import threading
from os import stat
from os.path import exists
from time import sleep
from typing import IO, Dict, List, Callable

import paramiko
import yaml
from prometheus_client import start_http_server

from parser import LogParser, request_header_reader, label_reader, number_reader, clf_number_reader, Metric
from prometheus import MetricsCollection

scrape_config = yaml.load(open(os.environ.get('SCRAPECONFIG', 'scrapeconfig.yml')))


class Pygtail(IO):
    def __init__(self, filename: str):
        self.filename = filename
        self._logfile_inode = stat(self.filename).st_ino
        self._fh = None
        self._rotated_logfile = None

    def __del__(self):
        if self._filehandle():
            self._filehandle().close()

    def __iter__(self):
        return self

    def __next__(self):
        """
        Return the next line in the file, updating the offset.
        """
        try:
            return self._get_next_line()
        except StopIteration:
            # we've reached the end of the file; if we're processing the
            # rotated log file or the file has been renamed, we can continue with the actual file; otherwise
            # update the offset file
            if self._is_new_file():
                self._rotated_logfile = None
                self._fh.close()
                # open up current logfile and continue
                return self._get_next_line()
            else:
                raise

    def _is_closed(self):
        if not self._fh:
            return True

        return self._fh.closed

    def _filehandle(self):
        """
        Return a filehandle to the file being tailed, with the position set
        to the current offset.
        """
        if not self._fh or self._is_closed():
            filename = self._rotated_logfile or self.filename
            self._fh = open(filename, "r", buffering=1)
            self._fh.seek(0, os.SEEK_END)

        return self._fh

    def _determine_rotated_logfile(self):
        """
        We suspect the logfile has been rotated, so try to guess what the
        rotated filename is, and return it.
        """
        rotated_filename = self._check_rotated_filename_candidates()
        if rotated_filename and exists(rotated_filename):
            if stat(rotated_filename).st_ino == self._logfile_inode:
                return rotated_filename

            if stat(self.filename).st_ino == self._logfile_inode:
                return rotated_filename

        return None

    def _check_rotated_filename_candidates(self):
        """
        Check for various rotated logfile filename patterns and return the first
        match we find.
        """
        # savelog(8)
        candidate = "%s.0" % self.filename
        if (exists(candidate) and exists("%s.1.gz" % self.filename) and
                (stat(candidate).st_mtime > stat("%s.1.gz" % self.filename).st_mtime)):
            return candidate

        # logrotate(8)
        # with delaycompress
        candidate = "%s.1" % self.filename
        if exists(candidate):
            return candidate

        # without delaycompress
        candidate = "%s.1.gz" % self.filename
        if exists(candidate):
            return candidate

        rotated_filename_patterns = (
            # logrotate dateext rotation scheme - `dateformat -%Y%m%d` + with `delaycompress`
            "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]",
            # logrotate dateext rotation scheme - `dateformat -%Y%m%d` + without `delaycompress`
            "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9].gz",
            # logrotate dateext rotation scheme - `dateformat -%Y%m%d-%s` + with `delaycompress`
            "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]",
            # logrotate dateext rotation scheme - `dateformat -%Y%m%d-%s` + without `delaycompress`
            "-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9].gz",
            # for TimedRotatingFileHandler
            ".[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]",
        )
        for rotated_filename_pattern in rotated_filename_patterns:
            candidates = glob.glob(self.filename + rotated_filename_pattern)
            if candidates:
                candidates.sort()
                return candidates[-1]  # return most recent

        # no match
        return None

    def _is_new_file(self):
        # Processing rotated logfile or at the end of current file which has been renamed
        return self._rotated_logfile or \
               self._filehandle().tell() == os.fstat(self._filehandle().fileno()).st_size and \
               os.fstat(self._filehandle().fileno()).st_ino != stat(self.filename).st_ino

    def _get_next_line(self):
        line = self._filehandle().readline()
        if not line:
            raise StopIteration
        return line


def _parse_file(stdout: IO, metrics: MetricsCollection, environment: str, readers: List[Callable[[Metric, str], None]]):
    if not environment:
        environment = 'N/A'

    for entry in LogParser(stdout, readers, {'environment': environment}).read_all():
        if entry is None:
            metrics.inc_counter(name='parser_errors',
                                documentation='Number of lines which could not be parsed',
                                labels={'environment': environment})
            continue

        metrics.inc_counter(name='lines_parsed',
                            documentation='Number of successfully parsed lines',
                            labels=entry.labels)

        for name, amount in entry.metrics.items():
            metrics.inc_counter(
                name=name,
                documentation='Sum of "{}"'.format(name),
                labels=entry.labels,
                amount=amount
            )


class LocalLogThread(threading.Thread):
    def __init__(self, metrics: MetricsCollection, filename: str, environment: str,
                 readers: List[Callable[[Metric, str], None]]):
        super().__init__()
        self._metrics = metrics
        self._filename = filename
        self._environment = environment
        self._readers = readers

    def run(self):
        while True:
            try:
                tail_file = Pygtail(self._filename)  # type: IO
                break
            except:
                sleep(5)
                continue

        while True:
            _parse_file(tail_file, self._metrics, self._environment, self._readers)
            sleep(1)


class SSHLogThread(threading.Thread):
    def __init__(self, metrics: MetricsCollection, filename: str, environment: str,
                 readers: List[Callable[[Metric, str], None]], host: str, user: str, password: str,
                 connect_timeout: float):
        super().__init__()
        self._metrics = metrics
        self._filename = filename
        self._host = host
        self._user = user
        self._password = password
        self._environment = environment
        self._readers = readers
        if connect_timeout is None:
            self._connect_timeout = None
        elif isinstance(connect_timeout, float):
            self._connect_timeout = connect_timeout
        else:
            self._connect_timeout = float(connect_timeout)

    def run(self):
        try:
            while True:
                with paramiko.SSHClient() as client:
                    client.load_system_host_keys()
                    client.set_missing_host_key_policy(paramiko.WarningPolicy())
                    try:
                        client.connect(hostname=self._host, port=22,
                                       username=self._user, password=self._password,
                                       timeout=self._connect_timeout)
                    except socket.timeout as e:
                        logging.getLogger().warning("Connect attempt to {} timed out, retrying".format(self._host))
                        continue
                    except (socket.error, paramiko.SSHException) as e:
                        logging.getLogger().warning("Connect attempt to {} failed, not trying again".format(self._host),
                                                    exc_info=e)
                        break
                    ssh_stdin, ssh_stdout, ssh_stderr = client.exec_command(
                        'tail -n0 -F "{}" 2>/dev/null'.format(self._filename),
                        bufsize=1)

                    _parse_file(ssh_stdout, self._metrics, self._environment, self._readers)
                    sleep(1)
        except Exception as e:
            logging.getLogger().warning("SSH failure", exc_info=e)
            _thread.interrupt_main()
            exit(1)


def parse_config():
    prefix = scrape_config['global']['prefix']
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
        elif tp != 'label' and name in ('parser_errors', 'lines_parsed'):
            raise ValueError("'{}' is a reserved metric name".format(name))

        reader = {
            'number': number_reader,
            'clf_number': clf_number_reader,
            'request_header': lambda _: request_header_reader(),
            'label': label_reader
        }[tp](name)
        readers.append(reader)

    metrics = MetricsCollection(prefix)

    threads = []

    if 'local' in scrape_config:
        for file in scrape_config['local']:
            threads.append(
                LocalLogThread(filename=file['path'], metrics=metrics, environment=file.get('environment', None),
                               readers=readers))

    if 'ssh' in scrape_config:
        default_file = scrape_config['ssh'].get('file', None)
        default_user = scrape_config['ssh'].get('user', None)
        default_password = scrape_config['ssh'].get('password', None)
        default_connect_timeout = scrape_config['ssh'].get('connect-timeout', None)
        for env_name, env_config in scrape_config['ssh']['environments'].items():
            hosts = env_config['hosts']
            if not isinstance(hosts, list):
                hosts = [hosts]

            for host in hosts:
                threads.append(
                    SSHLogThread(
                        filename=env_config.get('file', default_file),
                        metrics=metrics,
                        environment=env_name,
                        readers=readers,
                        host=host,
                        user=env_config.get('user', default_user),
                        password=env_config.get('password', default_password),
                        connect_timeout=env_config.get('connect-timeout', default_connect_timeout))
                )

    for thread in threads:
        thread.daemon = True
        thread.start()

    return threads, metrics


_threads, _metrics = parse_config()

start_http_server(5000)

while True:
    logging.getLogger().debug('Sleep...')
    sleep(1)  # dumb loop because all threads are daemonized
