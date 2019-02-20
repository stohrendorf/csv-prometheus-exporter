using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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


    public class MetricsTTL
    {
        public DateTime LastUpdated = DateTime.Now;
        public readonly LocalMetrics Metrics;

        public MetricsTTL([NotNull] LocalMetrics metrics)
        {
            Metrics = metrics;
        }
    }

    public sealed class MetricsMeta
    {
        public static string GlobalPrefix = null;
        public static int TTL = 60;

        [NotNull] private string BaseName { get; }
        [NotNull] private string Help { get; }
        public Type Type { get; }
        [CanBeNull] private double[] Buckets { get; }

        [NotNull] private readonly IDictionary<SortedDictionary<string, string>, MetricsTTL> _metrics =
            new Dictionary<SortedDictionary<string, string>, MetricsTTL>();

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

        public MetricsMeta Clone()
        {
            return new MetricsMeta(BaseName, Help, Type, Buckets);
        }

        public string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{BaseName}" : BaseName;

        private string Header =>
            string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
                Help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

        public void ExposeTo([NotNull] StringBuilder stringBuilder)
        {
            stringBuilder.Append(Header).Append('\n');
            var now = DateTime.Now;
            foreach (var ttlM in _metrics.Values)
            {
                if (ttlM.LastUpdated + TimeSpan.FromSeconds(TTL) < now)
                    continue;
                ttlM.Metrics.ExposeTo(stringBuilder);
                stringBuilder.Append('\n');
            }
        }

        private LocalMetrics CreateMetrics([NotNull] SortedDictionary<string, string> labels)
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

        public LocalMetrics GetMetrics([NotNull] SortedDictionary<string, string> labels)
        {
            lock (_metrics)
            {
                var m = !_metrics.TryGetValue(labels, out var ttlM) ? CreateMetrics(labels) : ttlM.Metrics;

                _metrics[labels] = new MetricsTTL(m);
                return m;
            }
        }

        public MetricsMeta FullClone()
        {
            var result = Clone();
            lock (_metrics)
            {
                foreach (var (labels, ttlM) in _metrics)
                {
                    result._metrics[labels] = ttlM;
                }
            }

            return result;
        }

        public void Merge([NotNull] MetricsMeta other)
        {
            lock (other._metrics)
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
                        _metrics[labels] = ttlM;
                }
            }
        }
    }

    public abstract class LocalMetrics
    {
        private SortedDictionary<string, string> Labels { get; }

        private MetricsMeta Meta { get; }

        protected LocalMetrics([NotNull] MetricsMeta meta, [NotNull] SortedDictionary<string, string> labels)
        {
            Meta = meta;
            Labels = labels;
        }

        protected string QualifiedName([CanBeNull] SortedDictionary<string, string> otherLabels = null)
        {
            if (otherLabels == null)
                otherLabels = new SortedDictionary<string, string>();
            return $"{Meta.PrefixedName}{{{LabelStr(otherLabels)}}}";
        }

        private string LabelStr([NotNull] SortedDictionary<string, string> otherLabels)
        {
            return ToLabelStr(Labels, otherLabels);
        }

        private static string ToLabelStr([NotNull] params SortedDictionary<string, string>[] a)
        {
            return string.Join(",", a.AsEnumerable()
                .SelectMany(_ => _.AsEnumerable())
                .OrderBy(_ => _.Key)
                .Select(_ => $"{_.Key}={Quote(_.Value)}"));
        }

        public abstract void ExposeTo(StringBuilder stringBuilder);

        public abstract void Add(double value);

        public abstract void MergeAll([NotNull] LocalMetrics other);

        private static string Quote([NotNull] string s)
        {
            return $"\"{s.Replace(@"\", @"\\").Replace("\n", @"\n").Replace("\"", "\\\"")}\"";
        }

        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
        protected static string ToGoString(double d)
        {
            if (d == double.PositiveInfinity)
                return "+Inf";
            if (d == double.NegativeInfinity)
                return "-Inf";
            if (d == double.NaN)
                return "NaN";

            return d.ToString(CultureInfo.InvariantCulture);
        }

        protected static string ExtendBaseName(string name, string suffix)
        {
            return new Regex(@"\{").Replace(name, suffix + "{", 1);
        }
    }

    public sealed class LocalCounter : LocalMetrics
    {
        private double _value;
        private readonly string _name;

        public LocalCounter([NotNull] MetricsMeta meta, [NotNull] SortedDictionary<string, string> labels) : base(meta,
            labels)
        {
            Debug.Assert(meta.Type == Type.Counter);
            _name = QualifiedName();
        }

        public override void ExposeTo(StringBuilder stringBuilder)
        {
            stringBuilder.Append(_name).Append(' ').Append(_value);
        }

        public override void Add(double value)
        {
            Debug.Assert(value >= 0);
            _value += value;
        }

        public override void MergeAll(LocalMetrics other)
        {
            if (!(other is LocalCounter o))
                throw new ArgumentException("Incompatible type", nameof(other));

            _value += o._value;
        }
    }

    public sealed class LocalGauge : LocalMetrics
    {
        private double _value;
        private readonly string _name;

        public LocalGauge([NotNull] MetricsMeta meta, [NotNull] SortedDictionary<string, string> labels) : base(meta,
            labels)
        {
            Debug.Assert(meta.Type == Type.Gauge);
            _name = QualifiedName();
        }

        public override void ExposeTo(StringBuilder stringBuilder)
        {
            stringBuilder.Append(_name).Append(' ').Append(_value);
        }

        public override void Add(double value)
        {
            _value += value;
        }

        public override void MergeAll(LocalMetrics other)
        {
            if (!(other is LocalGauge o))
                throw new ArgumentException("Incompatible type", nameof(other));

            _value += o._value;
        }
    }

    public sealed class LocalHistogram : LocalMetrics
    {
        public static readonly double[] DefaultBuckets =
            {.005, .01, .025, .05, .075, .1, .25, .5, .75, 1.0, 2.5, 5.0, 7.5, 10.0, double.PositiveInfinity};

        private readonly double[] _buckets;
        private readonly ulong[] _counts;
        private ulong _observations;
        private double _sum;
        private readonly string _bucketName;
        private readonly string _sumName;
        private readonly string _countName;

        public LocalHistogram([NotNull] MetricsMeta meta, [NotNull] SortedDictionary<string, string> labels,
            [NotNull] double[] buckets) : base(meta, labels)
        {
            Debug.Assert(meta.Type == Type.Histogram);
            _buckets = buckets.OrderBy(_ => _).ToArray();
            if (_buckets.Length == 0)
                _buckets = DefaultBuckets;
            else if (!double.IsPositiveInfinity(_buckets.Last()))
                _buckets = _buckets.AsEnumerable().Concat(new[] {double.PositiveInfinity}).ToArray();

            if (_buckets.Length < 2)
                throw new ArgumentException("Must at least provide one bucket", nameof(buckets));

            _counts = Enumerable.Range(0, _buckets.Length).Select(_ => 0UL).ToArray();
            _bucketName = QualifiedName(new SortedDictionary<string, string> {["le"] = "$$$$$"});
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StringBuilder stringBuilder)
        {
            for (int i = 0; i < _buckets.Length; ++i)
            {
                stringBuilder.Append(_bucketName.Replace("$$$$$", ToGoString(_buckets[i])))
                    .Append(' ').Append(_counts[i]).Append('\n');
            }

            stringBuilder.Append(_countName).Append(' ').Append(_observations).Append('\n')
                .Append(_sumName).Append(' ').Append(_sum);
        }

        public override void Add(double value)
        {
            ++_observations;
            _sum += value;
            for (int i = 0; i < _buckets.Length; ++i)
            {
                if (value <= _buckets[i])
                {
                    ++_counts[i];
                    break;
                }
            }
        }

        public override void MergeAll(LocalMetrics other)
        {
            if (!(other is LocalHistogram o))
                throw new ArgumentException("Incompatible type", nameof(other));

            _observations += o._observations;
            _sum += o._sum;
            for (int i = 0; i < _counts.Length; ++i)
                _counts[i] += o._counts[i];
        }
    }

    public sealed class LocalSummary : LocalMetrics
    {
        private double _sum;
        private ulong _count;
        private readonly string _sumName;
        private readonly string _countName;

        public LocalSummary([NotNull] MetricsMeta meta, [NotNull] SortedDictionary<string, string> labels) : base(meta,
            labels)
        {
            Debug.Assert(meta.Type == Type.Gauge);
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StringBuilder stringBuilder)
        {
            stringBuilder.Append(_sumName).Append(' ').Append(_sum).Append('\n')
                .Append(_countName).Append(' ').Append(_count);
        }

        public override void Add(double value)
        {
            _sum += value;
            ++_count;
        }

        public override void MergeAll(LocalMetrics other)
        {
            if (!(other is LocalSummary o))
                throw new ArgumentException("Incompatible type", nameof(other));

            _sum += o._sum;
            _count += o._count;
        }
    }
}