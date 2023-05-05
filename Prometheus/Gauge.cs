using System.Diagnostics;
using System.IO;

namespace csv_prometheus_exporter.Prometheus;

internal sealed class Gauge : LabeledMetric
{
  private readonly string _name;
  private readonly Scalar _value = new();

  internal Gauge(MetricBase metricBase, LabelDict labels) : base(metricBase, labels)
  {
    Debug.Assert(metricBase.Type == MetricsType.Gauge);
    _name = QualifiedName();
  }

  internal override void ExposeTo(StreamWriter stream)
  {
    stream.WriteLine("{0} {1}", _name, _value);
  }

  internal override void Add(double value)
  {
    _value.Add(value);
  }

  internal void Set(double value)
  {
    _value.Set(value);
  }
}
