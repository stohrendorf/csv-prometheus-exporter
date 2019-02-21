using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.MetricsImpl
{
    public sealed class LocalGauge : LocalMetrics
    {
        private readonly string _name;
        private double _value;

        public LocalGauge([NotNull] MetricsMeta meta, [NotNull] LabelDict labels) : base(meta, labels)
        {
            Debug.Assert(meta.Type == Type.Gauge);
            _name = QualifiedName();
        }

        private LocalGauge(LocalGauge self) : base(self.Meta, self.Labels)
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
            _value += value;
        }

        public void Set(double value)
        {
            _value = value;
        }

        public override void Add(LocalMetrics other)
        {
#if DEBUG
            if (!(other is LocalGauge o))
                throw new ArgumentException("Incompatible type", nameof(other));
#else
            var o = (LocalGauge) other;
#endif

            _value += o._value;
        }

        public override LocalMetrics Clone()
        {
            return new LocalGauge(this);
        }
    }
}