namespace csv_prometheus_exporter.Parser
{
    public delegate void ColumnReader(ParsedMetrics parsedMetrics, string value);
}