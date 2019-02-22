using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NLog;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class MetricBase
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        public static string GlobalPrefix = null;
        public static int TTL = 60;

        [NotNull] private readonly string _baseName;
        [CanBeNull] private readonly double[] _buckets;
        [NotNull] private readonly string _help;

        [NotNull] private readonly IDictionary<LabelDict, MetricUpdateTracker> _metrics =
            new Dictionary<LabelDict, MetricUpdateTracker>();

        private readonly object _metricsLock = new object();

        public readonly bool ExposeAlways;

        public MetricBase([NotNull] string baseName, [NotNull] string help, MetricsType type,
            [CanBeNull] double[] buckets = null, bool exposeAlways = false)
        {
            if (!string.IsNullOrEmpty(GlobalPrefix) && !IsValidMetricsBasename($"{GlobalPrefix}:{baseName}"))
                throw new ArgumentException("Invalid metrics name", nameof(baseName));
            if (string.IsNullOrEmpty(GlobalPrefix) && !IsValidMetricsBasename(baseName))
                throw new ArgumentException("Invalid metrics name", nameof(baseName));

            _baseName = baseName;
            _help = help;
            Type = type;
            ExposeAlways = exposeAlways;
            if (buckets != null)
                _buckets = buckets.ToArray();

            if (type == MetricsType.Histogram && buckets == null)
                throw new ArgumentException("Must provide buckets if type is histogram", nameof(buckets));
            if (type != MetricsType.Histogram && buckets != null)
                throw new ArgumentException("Must not provide buckets if type is not histogram", nameof(buckets));

            if (type == MetricsType.Counter && !_baseName.EndsWith("_total"))
            {
                Logger.Warn($"Counter metric \"{_baseName}\" will be adjusted to have the \"..._total\" suffix");
                _baseName += "_total";
            }
        }

        public MetricsType Type { get; }

        public string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{_baseName}" : _baseName;

        private string Header =>
            string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
                _help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

        private static bool IsValidMetricsBasename([NotNull] string name)
        {
            return new Regex("^[a-zA-Z0-9:_]+$").IsMatch(name)
                   && !new[] {"_sum", "_count", "_bucket", "_total"}.Any(name.EndsWith)
                   && !new[] {"process_", "scrape_"}.Any(name.StartsWith);
        }

        public int ExposeTo([NotNull] StreamWriter stream)
        {
            stream.WriteLine(Header);

            IList<MetricUpdateTracker> metrics;
            lock (_metricsLock)
            {
                metrics = _metrics.Values.ToList(); // copy
            }

            var exposed = 0;
            var eol = DateTime.Now - TimeSpan.FromSeconds(TTL);
            foreach (var ttlM in metrics)
            {
                if (!ttlM.Metric.ExposeAlways && ttlM.LastUpdated < eol)
                    continue;

                ttlM.Metric.ExposeTo(stream);
                ++exposed;
            }

            Task.Run(() => KillDeadMetrics());

            return exposed;
        }

        private void KillDeadMetrics()
        {
            Logger.Info($"Doing metric extinction for {_baseName}...");
            var stopWatch = new Stopwatch();
            lock (_metricsLock)
            {
                var old = _metrics;
                _metrics.Clear();
                var eol = DateTime.Now - TimeSpan.FromSeconds(TTL * 5);

                foreach (var (labels, ttlM) in old)
                {
                    if (!ttlM.Metric.ExposeAlways && ttlM.LastUpdated < eol)
                        continue;

                    _metrics[labels] = ttlM;
                }
            }

            stopWatch.Stop();
            Logger.Info($"Metrics extinction for {_baseName} took {stopWatch.Elapsed}");
        }

        private LabeledMetric CreateMetrics([NotNull] LabelDict labels)
        {
            switch (Type)
            {
                case MetricsType.Counter:
                    return new Counter(this, labels);
                case MetricsType.Gauge:
                    return new Gauge(this, labels);
                case MetricsType.Histogram:
                    Debug.Assert(_buckets != null);
                    return new Histogram(this, labels, _buckets);
                case MetricsType.Summary:
                    return new Summary(this, labels);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public LabeledMetric WithLabels([NotNull] LabelDict labels)
        {
            lock (_metricsLock)
            {
                if (_metrics.TryGetValue(labels, out var ttlM))
                    return ttlM.TouchAndGet();

                return (_metrics[labels] = new MetricUpdateTracker(CreateMetrics(labels))).Metric;
            }
        }
    }
}