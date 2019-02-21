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

    public sealed class MetricUpdateTracker
    {
        public readonly LocalMetrics Metric;
        public DateTime LastUpdated;

        public MetricUpdateTracker([NotNull] LocalMetrics metric, DateTime? lastUpdated = null)
        {
            LastUpdated = lastUpdated ?? DateTime.Now;
            Metric = metric;
        }

        public MetricUpdateTracker DeepClone()
        {
            return new MetricUpdateTracker(Metric.Clone(), LastUpdated);
        }
    }

    public sealed class MetricsMeta
    {
        public static string GlobalPrefix = null;
        public static int TTL = 60;

        [NotNull] private readonly IDictionary<LabelDict, MetricUpdateTracker> _metrics =
            new ConcurrentDictionary<LabelDict, MetricUpdateTracker>();

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

        [NotNull] internal string BaseName { get; }
        [NotNull] internal string Help { get; }
        public Type Type { get; }
        [CanBeNull] internal double[] Buckets { get; }

        public string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{BaseName}" : BaseName;

        private string Header =>
            string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
                Help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

        private static bool IsValidMetricsBasename([NotNull] string name)
        {
            return new Regex("^[a-zA-Z0-9:_]+$").IsMatch(name)
                   && !new[] {"_sum", "_count", "_bucket", "_total"}.Any(name.EndsWith)
                   && !new[] {"process_", "scrape_"}.Any(name.StartsWith);
        }

        public void ExposeTo([NotNull] StreamWriter stream)
        {
            stream.WriteLine(Header);
            foreach (var ttlM in _metrics.Values)
            {
                ttlM.Metric.ExposeTo(stream);
            }
        }

        public int Count => _metrics.Count;

        private LocalMetrics CreateMetrics([NotNull] LabelDict labels)
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

        public LocalMetrics GetMetrics([NotNull] LabelDict labels)
        {
            var m = !_metrics.TryGetValue(labels, out var ttlM) ? CreateMetrics(labels) : ttlM.Metric;

            _metrics[labels] = new MetricUpdateTracker(m);
            return m;
        }

        public MetricsMeta DeepClone(ISet<LabelDict> filter)
        {
            var result = new MetricsMeta(BaseName, Help, Type, Buckets);
            foreach (var (labels, ttlM) in _metrics)
                if (filter.Contains(labels))
                    result._metrics[labels] = ttlM.DeepClone();

            return result;
        }

        public void GetLatestTTL(IDictionary<LabelDict, DateTime> ttls)
        {
            foreach (var (labels, ttlM) in _metrics)
                if (!ttls.TryGetValue(labels, out var existing) || existing < ttlM.LastUpdated)
                {
                    ttls[labels] = ttlM.LastUpdated;
                }
        }

        public void Add([NotNull] MetricsMeta other, ISet<LabelDict> filter)
        {
            foreach (var (labels, ttlM) in other._metrics)
            {
                if (!filter.Contains(labels))
                    continue;

                if (_metrics.TryGetValue(labels, out var existing))
                {
                    existing.Metric.Add(ttlM.Metric);
                }
                else
                {
                    _metrics[labels] = ttlM.DeepClone();
                }
            }
        }
    }

    public abstract class LocalMetrics
    {
        protected LocalMetrics([NotNull] MetricsMeta meta, [NotNull] LabelDict labels)
        {
            Meta = meta;
            Labels = labels;
        }

        protected LabelDict Labels { get; }

        protected MetricsMeta Meta { get; }

        protected string QualifiedName([CanBeNull] string le = null)
        {
            return $"{Meta.PrefixedName}{{{Labels.ToString(le)}}}";
        }

        public abstract void ExposeTo(StreamWriter stream);

        public abstract void Add(double value);

        public abstract void Add([NotNull] LocalMetrics other);

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