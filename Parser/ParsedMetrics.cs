using System.Collections.Generic;
using csv_prometheus_exporter.Prometheus;

namespace csv_prometheus_exporter.Parser;

public sealed class ParsedMetrics
{
  internal readonly LabelDict Labels;
  internal readonly IDictionary<string, double> Metrics = new Dictionary<string, double>(31);

  internal ParsedMetrics(LabelDict labels)
  {
    Labels = new LabelDict(labels);
  }
}
