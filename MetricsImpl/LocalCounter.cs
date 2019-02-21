using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.MetricsImpl
{
    public sealed class LocalCounter : LocalMetrics
    {
        private double _value;
        private readonly string _name;

        public LocalCounter([NotNull] MetricsMeta meta, [NotNull] Dictionary<string, string> labels) : base(meta,
            labels)
        {
            Debug.Assert(meta.Type == Type.Counter);
            _name = QualifiedName();
        }

        private LocalCounter(LocalCounter self) : base(self.Meta, self.Labels)
        {
            _value = self._value;
            _name = self._name;
        }

        public override void ExposeTo(StreamWriter stream)
        {
            stream.WriteLine("{0} {1}", _name, _value.ToString(CultureInfo.InvariantCulture));
        }

        public override void Add(double value)
        {
            Debug.Assert(value >= 0);
            _value += value;
        }

        public override void MergeAll(LocalMetrics other)
        {
#if DEBUG
            if (!(other is LocalCounter o))
                throw new ArgumentException("Incompatible type", nameof(other));
#else
            var o = (LocalCounter) other;
#endif

            _value += o._value;
        }

        public override LocalMetrics Clone()
        {
            return new LocalCounter(this);
        }
    }
}