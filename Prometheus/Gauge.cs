using System.Diagnostics;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class Gauge : LabeledMetric
    {
        private readonly string _name;
        private readonly Scalar _value = new Scalar();

        public Gauge([NotNull] MetricBase metricBase, [NotNull] LabelDict labels) : base(metricBase, labels)
        {
            Debug.Assert(metricBase.Type == MetricsType.Gauge);
            _name = QualifiedName();
        }

        public override void ExposeTo(StreamWriter stream)
        {
            stream.WriteLine("{0} {1}", _name, _value);
        }

        public override void Add(double value)
        {
            _value.Add(value);
        }

        public void Set(double value)
        {
            _value.Set(value);
        }
    }
}