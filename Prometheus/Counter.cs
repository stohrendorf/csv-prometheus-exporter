using System.Diagnostics;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class Counter : LabeledMetric
    {
        private readonly string _name;
        private readonly Scalar _value = new Scalar();

        public Counter([NotNull] MetricBase metricBase, [NotNull] LabelDict labels) : base(metricBase, labels)
        {
            Debug.Assert(metricBase.Type == MetricsType.Counter);
            _name = QualifiedName();
        }

        public override void ExposeTo(StreamWriter stream)
        {
            stream.WriteLine("{0} {1}", _name, _value);
        }

        public override void Add(double value)
        {
            Debug.Assert(value >= 0);
            _value.Add(value);
        }

        public void Set(double value)
        {
            Debug.Assert(value >= _value.Get());
            _value.Set(value);
        }
    }
}