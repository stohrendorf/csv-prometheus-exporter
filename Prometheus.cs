using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using csv_prometheus_exporter.MetricsImpl;
using JetBrains.Annotations;

namespace csv_prometheus_exporter
{
    public enum Type
    {
        Counter,
        Gauge,
        Histogram,
        Summary
    }

    public sealed class MetricsTTL
    {
        public DateTime LastUpdated;
        public readonly LocalMetrics Metrics;

        public MetricsTTL([NotNull] LocalMetrics metrics, DateTime? lastUpdated = null)
        {
            LastUpdated = lastUpdated ?? DateTime.Now;
            Metrics = metrics;
        }

        public MetricsTTL Clone()
        {
            return new MetricsTTL(Metrics.Clone(), LastUpdated);
        }
    }

    public sealed class MetricsMeta
    {
        public static string GlobalPrefix = null;
        public static int TTL = 60;

        [NotNull] internal string BaseName { get; }
        [NotNull] internal string Help { get; }
        public Type Type { get; }
        [CanBeNull] internal double[] Buckets { get; }

        [NotNull] private readonly IDictionary<Dictionary<string, string>, MetricsTTL> _metrics =
            new ConcurrentDictionary<Dictionary<string, string>, MetricsTTL>();

        private static bool IsValidMetricsBasename([NotNull] string name)
        {
            return new Regex("^[a-zA-Z0-9:_]+$").IsMatch(name)
                   && !new[] {"_sum", "_count", "_bucket", "_total"}.Any(name.EndsWith)
                   && !new[] {"process_", "scrape_"}.Any(name.StartsWith);
        }

        public MetricsMeta([NotNull] string baseName, [NotNull] string help, Type type,
            [CanBeNull] double[] buckets = null)
        {
            if (!string.IsNullOrEmpty(GlobalPrefix) && !IsValidMetricsBasename($"{GlobalPrefix}:{baseName}"))
                throw new ArgumentException("Invalid metrics name", nameof(baseName));
            if (string.IsNullOrEmpty(GlobalPrefix) && !IsValidMetricsBasename(baseName))
                throw new ArgumentException("Invalid metrics name", nameof(baseName));

            BaseName = baseName;
            Help = help;
            Type = type;
            if (buckets != null)
                Buckets = buckets.ToArray();

            if (type == Type.Histogram && buckets == null)
                throw new ArgumentException("Must provide buckets if type is histogram", nameof(buckets));
            if (type != Type.Histogram && buckets != null)
                throw new ArgumentException("Must not provide buckets if type is not histogram", nameof(buckets));
        }

        public string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{BaseName}" : BaseName;

        private string Header =>
            string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
                Help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

        public void ExposeTo([NotNull] StreamWriter stream)
        {
            stream.WriteLine(Header);
            var eol = DateTime.Now - TimeSpan.FromSeconds(TTL);
            foreach (var ttlM in _metrics.Values)
            {
                if (ttlM.LastUpdated < eol)
                    continue;
                ttlM.Metrics.ExposeTo(stream);
            }
        }

        private LocalMetrics CreateMetrics([NotNull] Dictionary<string, string> labels)
        {
            switch (Type)
            {
                case Type.Counter:
                    return new LocalCounter(this, labels);
                case Type.Gauge:
                    return new LocalGauge(this, labels);
                case Type.Histogram:
                    Debug.Assert(Buckets != null);
                    return new LocalHistogram(this, labels, Buckets);
                case Type.Summary:
                    return new LocalSummary(this, labels);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public LocalMetrics GetMetrics([NotNull] Dictionary<string, string> labels)
        {
            var m = !_metrics.TryGetValue(labels, out var ttlM) ? CreateMetrics(labels) : ttlM.Metrics;

            _metrics[labels] = new MetricsTTL(m);
            return m;
        }

        public MetricsMeta FullClone()
        {
            var result = new MetricsMeta(BaseName, Help, Type, Buckets);
            foreach (var (labels, ttlM) in _metrics)
            {
                result._metrics[labels] = ttlM.Clone();
            }

            return result;
        }

        public void Merge([NotNull] MetricsMeta other)
        {
            foreach (var (labels, ttlM) in other._metrics)
            {
                if (_metrics.TryGetValue(labels, out var existing))
                {
                    existing.Metrics.MergeAll(ttlM.Metrics);
                    if (existing.LastUpdated < ttlM.LastUpdated)
                        existing.LastUpdated = ttlM.LastUpdated;
                }
                else
                    _metrics[labels] = ttlM.Clone();
            }
        }
    }

    public abstract class LocalMetrics
    {
        protected Dictionary<string, string> Labels { get; }

        protected MetricsMeta Meta { get; }

        protected LocalMetrics([NotNull] MetricsMeta meta, [NotNull] Dictionary<string, string> labels)
        {
            Meta = meta;
            Labels = labels;
        }

        protected string QualifiedName([CanBeNull] Dictionary<string, string> otherLabels = null)
        {
            if (otherLabels == null)
                otherLabels = new Dictionary<string, string>();
            return $"{Meta.PrefixedName}{{{LabelStr(otherLabels)}}}";
        }

        private string LabelStr([NotNull] Dictionary<string, string> otherLabels)
        {
            return string.Join(",", Labels.AsEnumerable().Concat(otherLabels.AsEnumerable())
                .OrderBy(_ => _.Key)
                .Select(_ => $"{_.Key}={Quote(_.Value)}"));
        }

        public abstract void ExposeTo(StreamWriter stream);

        public abstract void Add(double value);

        public abstract void MergeAll([NotNull] LocalMetrics other);

        private static string Quote([NotNull] string s)
        {
            return $"\"{s.Replace(@"\", @"\\").Replace("\n", @"\n").Replace("\"", "\\\"")}\"";
        }

        public abstract LocalMetrics Clone();

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
    }
}
