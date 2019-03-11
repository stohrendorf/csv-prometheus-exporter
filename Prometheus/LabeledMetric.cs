using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public abstract class LabeledMetric
    {
        protected LabeledMetric([NotNull] MetricBase metricBase, [NotNull] LabelDict labels)
        {
            _metricBase = metricBase;
            Labels = labels;
        }

        public readonly LabelDict Labels;

        private readonly MetricBase _metricBase;
        public Resilience Resilience => _metricBase.Resilience;

        public DateTime LastUpdated = DateTime.Now;

        protected string QualifiedName([CanBeNull] string le = null)
        {
            return $"{_metricBase.PrefixedName}{{{Labels.ToString(le)}}}";
        }

        public abstract void ExposeTo(StreamWriter stream);

        public abstract void Add(double value);

        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
        protected static string ToGoString(double d)
        {
            switch (d)
            {
                case double.PositiveInfinity:
                    return "+Inf";
                case double.NegativeInfinity:
                    return "-Inf";
                case double.NaN:
                    return "NaN";
                default:
                    return d.ToString(CultureInfo.InvariantCulture);
            }
        }

        protected static string ExtendBaseName(string name, string suffix)
        {
            return new Regex(@"\{").Replace(name, suffix + "{", 1);
        }

        public void Drop()
        {
            _metricBase.Drop(this);
        }
    }
}