# CSV Prometheus Exporter

[![Build Status](https://api.cirrus-ci.com/github/stohrendorf/csv-prometheus-exporter.svg)](https://cirrus-ci.com/github/stohrendorf/csv-prometheus-exporter)

A simple exporter for CSV-based files[*].  Basically runs "tail -f" on remote files over SSH and aggregates
them into Prometheus compatible metrics. It is capable of processing at least 100 servers with thousands of
requests per second on a single core with a response time below 2 seconds.

> [*] CSV in this case means "space-separated, double-quote delimited format"; this piece of software was primarily
> developed for parsing access logs, but if needed, it can be extended to parse any CSV-based format that
> [FastCsvParser](https://github.com/bopohaa/CsvParser) can handle.

Metrics are exposed at `host:5000/metrics`.

## Configuration
The configuration format is defined as follows.
```yaml
global:
  ttl: 60 # The metrics' time-to-live in seconds.
  prefix: some_prefix  # If set, all metrics (including process metrics) will be exposed as "some_prefix:metric".
  histograms: # optional
    - response_time: ~  # Default bucket limits.
    - request_size: [10, 20, 50, ...]  # Upper bucket limits, "+Inf" is added automatically.
  format:
    - name: type
    - name: type+request_size  # Expose the metric as a histogram, using the histograms defined above.
    - ...

script: python3 some-inventory-script.py # Output must be the the same as the ssh section below, including the "ssh" key.
reload-interval: 30 # Optional; seconds between attempts to execute the script above.

ssh:
  connection: # Provide some default settings; these can be overriden per environment.
    file: /var/log/some-csv-file # tail -f on this
    user: log-reader # SSH user
    password: secure123
    pkey: /home/log-reader-id-rsa # private key file (optional)
    connect-timeout: 5 # (optional, defaults to 30 seconds)
    read-timeout-ms: 1000 # (optional, defaults to 60 seconds)
  environments:
    environmentA:
      hosts: [...]
    environmentB:
      hosts: [...]
      connection:
        file: /var/log/some-other-csv-file
        user: someotheruser
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

Place your `scrapeconfig.yml` either in the folder you're starting `app.py` from, or
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
collection, and doesn't receive an update for another period of the specified TTL, it will be fully evicted
from the processing.

Practice has shown that this two-phase metric garbage collection is strictly necessary to avoid excessive
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
