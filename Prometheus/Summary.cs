using System.Diagnostics;
using System.IO;

namespace csv_prometheus_exporter.Prometheus;

internal sealed class Summary : LabeledMetric
{
  private readonly ULongScalar _count = new();
  private readonly string _countName;
  private readonly Scalar _sum = new();
  private readonly string _sumName;

  public Summary(MetricBase metricBase, LabelDict labels) : base(metricBase, labels)
  {
    Debug.Assert(metricBase.Type == MetricsType.Gauge);
    var name = QualifiedName();
    _sumName = ExtendBaseName(name, "_sum");
    _countName = ExtendBaseName(name, "_count");
  }

  internal override void ExposeTo(StreamWriter stream)
  {
    stream.WriteLine("{0} {1}", _sumName, _sum);
    stream.WriteLine("{0} {1}", _countName, _count);
  }

  internal override void Add(double value)
  {
    _sum.Add(value);
    _count.Add(1);
  }
}
