# Sample Grafana Dashboards

*Feel free to submit your own dashboard. Make sure that your metrics prefix and the job name are configurable
before doing so. Also, please provide a screenshot if possible.*

## Access Logs

[Grafana Dashboard File for Import](./access-logs.json)

![access logs screenshot](./access-logs.jpg)

Based on the format definition
```yaml
format:
- remote_addr: label
- ~
- remote_user: label
- ~
- request_header: request_header
- status: label
- sent_bytes: clf_number
- received_bytes: clf_number
- request_time_seconds: clf_number
- processing_time_seconds: clf_number
- ~
- user_agent: label
```
and the rules (change `PREFIX` as needed)
```yaml
groups:
- name: access_logs_precompute
  interval: 5s
  rules:
  - record: "PREFIX:lines_parsed_per_second"
    expr: "irate(PREFIX:lines_parsed_total[1m])"
  - record: "PREFIX:sent_bytes_per_second"
    expr: "irate(PREFIX:sent_bytes_total[1m])"
  - record: "PREFIX:received_bytes_per_second"
    expr: "irate(PREFIX:received_bytes_total[1m])"
  - record: "PREFIX:request_time_seconds_per_second"
    expr: "irate(PREFIX:request_time_seconds_total[1m])"
```
