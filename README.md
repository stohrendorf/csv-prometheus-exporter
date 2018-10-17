# CSV Prometheus Exporter

A simple exporter for CSV-based files[*].  Basically runs "tail -f" on local files or remote files over SSH and aggregates
them into Prometheus compatible metrics. 

> [*] CSV in this case means "space-separated, double-quote delimited format"; this piece of software was primarily
> developed for parsing access logs, but if needed, it can be extended to parse any CSV-based format that Python's
> CSV parser can handle.

Metrics are exposed at `host:5000/metrics`.

## Configuration
Columns are defined as follows, with an example for apache access logs:
```yaml
format: # based on "%h %l %u %t \"%r\" %>s %b"
- remote_host: label
- ~ # ignore remote logname
- remote_user: label
- ~ # ignore timestamp
- request_header: request_header # special parser that emits the labels "request_http_version", "request_uri" and "request_method"
- status: label
- body_bytes_sent: clf_int  # maps a single dash to zero, otherwise behaves like "int"
```

* Names are either used as metric names or label names.
* All non-labels (using numeric parsers) are accumulated.  This means that the sampling frequency does not have any
  impact on the metrics.
* For metrics, use any of these parsers: `int`, `float` or `clf_int`.
* A special parser `request_header` is described above.

_Please note that there is a special, internal metric `request_count`
which counts the request per label-tuples; don't use this as one of your metric names!_

Place your `scrapeconfig.yml` in the folder you're starting `app.py` from;
[see here for an example](./scrapeconfig.example.yml), showing all of its features.

## A note about Proemtheus performance
Performance matters, and the exported metrics are not usable immediately in most cases.  The following
Prometheus rules have been tested in high-traffic situations, and sped up Prometheus queries immensely.

**Adjust as necessary, these are only examples.**

```yaml
groups:
- name: access_logs_precompute
  interval: 5s
  rules:
  - record: "prefix:request_count_per_second"
    expr: "irate(prefix:request_count[1m])"
  - record: "prefix:body_bytes_sent_per_second"
    expr: "irate(prefix:body_bytes_sent[1m])"
  - record: "prefix:request_length_per_second"
    expr: "irate(prefix:request_length[1m])"
  - record: "prefix:request_time_per_second"
    expr: "irate(prefix:request_time[1m])"
```