using System.Diagnostics;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class Summary : LabeledMetric
    {
        private readonly ULongScalar _count = new ULongScalar();
        private readonly string _countName;
        private readonly Scalar _sum = new Scalar();
        private readonly string _sumName;

        public Summary([NotNull] MetricBase metricBase, [NotNull] LabelDict labels) : base(metricBase, labels)
        {
            Debug.Assert(metricBase.Type == MetricsType.Gauge);
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StreamWriter stream)
        {
            stream.WriteLine("{0} {1}", _sumName, _sum);
            stream.WriteLine("{0} {1}", _countName, _count);
        }

        public override void Add(double value)
        {
            _sum.Add(value);
            _count.Add(1);
        }
    }
}
