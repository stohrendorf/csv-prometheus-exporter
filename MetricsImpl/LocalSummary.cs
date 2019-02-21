using System.Diagnostics;
using System.Globalization;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.MetricsImpl
{
    public sealed class LocalSummary : LocalMetrics
    {
        private readonly string _countName;
        private readonly string _sumName;
        private ulong _count;
        private double _sum;

        public LocalSummary([NotNull] MetricsMeta meta, [NotNull] LabelDict labels) : base(meta, labels)
        {
            Debug.Assert(meta.Type == Type.Gauge);
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        private LocalSummary(LocalSummary self) : base(self.Meta, self.Labels)
        {
            _sum = self._sum;
            _count = self._count;
            _sumName = self._sumName;
            _countName = self._countName;
        }

        public override void ExposeTo(StreamWriter stream)
        {
            stream.WriteLine("{0} {1}", _sumName, _sum.ToString(CultureInfo.InvariantCulture));
            stream.WriteLine("{0} {1}", _countName, _count);
        }

        public override void Add(double value)
        {
            _sum += value;
            ++_count;
        }

        public override void MergeAll(LocalMetrics other)
        {
#if DEBUG
            if (!(other is LocalSummary o))
                throw new ArgumentException("Incompatible type", nameof(other));
#else
            var o = (LocalSummary) other;
#endif

            _sum += o._sum;
            _count += o._count;
        }

        public override LocalMetrics Clone()
        {
            return new LocalSummary(this);
        }
    }
}