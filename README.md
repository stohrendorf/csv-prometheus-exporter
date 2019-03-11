# CSV Prometheus Exporter

[![Build Status](https://api.cirrus-ci.com/github/stohrendorf/csv-prometheus-exporter.svg)](https://cirrus-ci.com/github/stohrendorf/csv-prometheus-exporter)

A simple exporter for CSV-based files[*].  Basically runs "tail -f" on remote files over SSH and aggregates
them into Prometheus compatible metrics. It is capable of processing at least 100 servers with thousands of
requests per second on a small VM (peak usage 8 cores, average usage 2 cores, average RAM usage below 200MB,
average incoming SSH traffic below 400kB/s, not including resource requirements for Prometheus itself).

> [*] CSV in this case means "space-separated, double-quote delimited format"; this piece of software was primarily
> developed for parsing access logs, but if needed, it can be extended to parse any CSV-based format that
> [FastCsvParser](https://github.com/bopohaa/CsvParser) can handle.

Metrics are exposed at `host:5000/metrics`.

## Configuration
The configuration format is defined as follows. Identifiers prefixed with a `$` are names that can be
chosen freely. Stuff enclosed within `[...]` is optional, the text enclosed within `<...>` describes
the expected types. Please note that the tilde (`~`) is equivalent to `null`.
```
global:
  # The metrics' time-to-live.
  ttl: <seconds = 60>
  [background-resilience: <integer = 1>] # how many "ttl" time spans to keep the metrics in background after they
                                         # exceed their ttl
  [long-term-resilience: <integer = 10>] # how many "ttl" time spans to keep long-term metrics in background after they
                                         # exceed their ttl
  # If prefix is set, all metrics (including process metrics)
  # will be exposed as "prefix:metric-name".
  [prefix: <string>]
  [histograms: <buckets-list>]
  format: <metrics-list>

[script: <script-name>]
# If a script is given, but no reload-interval, it is executed only once at startup.
[reload-interval: <seconds>]

ssh:
  [<connection-settings>]
  environments: <list-of-environments>

<buckets-list> :=
  # Numbers do not need to be ordered, an implicit "+Inf" will be added.
  # If the values are not set, the default will be used, which is
  # [.005, .01, .025, .05, .075, .1, .25, .5, .75, 1, 2.5, 5, 7.5, 10]
  $bucket_name_1: <~ | list-of-numbers>
  $bucket_name_2: <~ | list-of-numbers>
  ...

<metrics-list> :=
  $metric_name_1: <type>
  $metric_name_2: <type>
  
<type> :=
  # Example: clf_number + request_bytes_sent 
  (number | clf_number | label | request_header) [+ $bucket_name]

<list-of-environments> :=
  $environment_name_1:
    hosts: <hostnames-or-ip-list>
    [connection: <connection-settings>]
  $environment_name_2:
    hosts: <hostnames-or-ip-list>
    [connection: <connection-settings>]
  ...

# Note that a few restrictions exist for the connection settings.
#   1. "file" and "user" are defined as required, but this means only
#      that they must be either set at the global "ssh" level, or at
#      the environment level.
#   2. At least one of "password" or "pkey" must be set.
#   3. If one of the settings is not set explicitly on environment level,
#      the value is inherited from the global "ssh" level.
<connection-settings> :=
  file: <string>
  user: <string>
  [password: <string>]
  [pkey: <string>]
  [connect-timeout: <seconds = 30>]
  [read-timeout-ms: <ms = 60000]
```

The supported `type` values are:
* `number` for floating point values, exposed as a `counter`, or as a `histogram`.
* `clf_number`, which behaves the same as `number`, except that a single `-` will be mapped to zero.
* `label` will use the column's value as a label.
* `request_header` expects a HTTP request header; a value of `POST /foo HTTP/1.1` will emit the labels
  `request_method="POST"`, `request_uri="/foo"` and `request_http_version="HTTP/1.1"`.
  
Each metric (except process metrics) will contain the label `environment`.
  
Scalar metrics will be accumulated; successfully processed lines will be counted in `lines_parsed`, with its labels
set to the CSV line's labels. If something bad happens, the erroneous lines will be counted in `parser_errors`,
but as the entry could not reliably parsed at this point, it will only contain the `environment` label.

For example, to parse access logs, you could use this.
```yaml
format: # based on "%h %l %u %t \"%r\" %>s %b"
- remote_host: label
- ~ # ignore remote logname
- remote_user: label
- ~ # ignore timestamp
- request_header: request_header # special parser that emits the labels "request_http_version", "request_uri" and "request_method"
- status: label
- body_bytes_sent: clf_number  # maps a single dash to zero, otherwise behaves like "number"
```

Place your `scrapeconfig.yml` either in `/etc`, or
provide the environment variable `SCRAPECONFIG` with a config file path;
[see here for a config file example](./scrapeconfig.example.yml), showing all of its features.

# Installation

A docker image, containing `python3` and `curl`, is available
[here](https://hub.docker.com/r/stohrendorf/csv-prometheus-exporter/).

# Technical & Practical Notes

## The TTL Thing
Metrics track when they were last updated. If a metric doesn't change within the TTL specified in the
config file (which defaults to 60 seconds), it will not be exposed via `/metrics` anymore; this is the
first phase of garbage collection to avoid excessive traffic. If a metric is in the first phase of garbage
collection, and doesn't receive an update for another `background-resilience` periods of the specified TTL,
it will be fully evicted from the processing.

A few metrics (namely, the `parser_errors` and `lines_parsed` metrics) are in "long-term mode"; they will be evicted
after `long-term-resilience` periods of the TTL; the `connected` metric will never be evicted.

Practice has shown that this multi-phase metric garbage collection is strictly necessary to avoid excessive
response sizes and to avoid the process to be clogged up by processing dead metrics. It doesn't have any known
serious impacts on the metrics' values, though.

## A note about Prometheus performance
Performance matters, and the exported metrics are not usable immediately in most cases.  The following
Prometheus rules have been tested in high-traffic situations, and sped up Prometheus queries immensely.

**Adjust as necessary, these are only examples.**

```yaml
groups:
- name: access_logs_precompute
  interval: 5s
  rules:
  - record: "prefix:lines_parsed_per_second"
    expr: "irate(prefix:lines_parsed_total[1m])"
  - record: "prefix:body_bytes_sent_per_second"
    expr: "irate(prefix:body_bytes_sent_total[1m])"
  - record: "prefix:request_length_per_second"
    expr: "irate(prefix:request_length_total[1m])"
  - record: "prefix:request_time_per_second"
    expr: "irate(prefix:request_time_total[1m])"
```
