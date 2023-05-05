using System.Diagnostics;
using System.IO;

namespace csv_prometheus_exporter.Prometheus;

internal sealed class Counter : LabeledMetric
{
  private readonly string _name;
  private readonly Scalar _value = new();

  internal Counter(MetricBase metricBase, LabelDict labels) : base(metricBase, labels)
  {
    Debug.Assert(metricBase.Type == MetricsType.Counter);
    _name = QualifiedName();
  }

  internal override void ExposeTo(StreamWriter stream)
  {
    stream.WriteLine("{0} {1}", _name, _value);
  }

  internal override void Add(double value)
  {
    Debug.Assert(value >= 0);
    _value.Add(value);
  }

  internal void Set(double value)
  {
    Debug.Assert(value >= _value.Get());
    _value.Set(value);
  }
}
