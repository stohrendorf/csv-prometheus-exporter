using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NLog;

namespace csv_prometheus_exporter.Prometheus
{
    /// <summary>
    /// Basic metric definition and container for its instances.
    /// </summary>
    public sealed class MetricBase
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private static string _globalPrefix = null;

        public static string GlobalPrefix
        {
            get => _globalPrefix;

            set
            {
                if (!string.IsNullOrEmpty(value) && !IsValidMetricsBasename(value))
                    throw new ArgumentException("Invalid prefix", nameof(value));
                _globalPrefix = value;
            }
        }

        public static int TTL = 60;

        /// <summary>
        /// Time To Death
        /// </summary>
        private static int TTD => 2 * TTL;

        public static readonly MetricBase ParserErrors = new MetricBase("parser_errors",
            "Number of lines which could not be parsed", MetricsType.Counter, null, true);

        public static readonly MetricBase LinesParsed = new MetricBase("lines_parsed",
            "Number of successfully parsed lines", MetricsType.Counter, null, true);

        public static readonly MetricBase Connected = new MetricBase("connected",
            "Whether this target is currently being scraped", MetricsType.Gauge, null, true);

        [NotNull] private readonly string _baseName;
        [CanBeNull] private readonly double[] _buckets;
        [NotNull] private readonly string _help;

        [NotNull] private readonly IDictionary<LabelDict, LabeledMetric> _metrics =
            new Dictionary<LabelDict, LabeledMetric>();

        private readonly object _metricsLock = new object();

        public readonly bool ExposeAlways;

        public MetricBase([NotNull] string baseName, [NotNull] string help, MetricsType type,
            [CanBeNull] double[] buckets = null, bool exposeAlways = false)
        {
            if (!IsValidMetricsBasename(baseName))
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
                _baseName += "_total";
            }

            KillDeadMetricsCycle();
        }

        public MetricsType Type { get; }

        public string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{_baseName}" : _baseName;

        private string Header =>
            string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
                _help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

        private static bool IsValidMetricsBasename([NotNull] string name)
        {
            return new Regex("^[a-zA-Z0-9:_]+$").IsMatch(name)
                   && !new[] {"_sum", "_count", "_bucket", "_total"}.Any(name.EndsWith);
        }

        public int ExposeTo([NotNull] StreamWriter stream)
        {
            stream.WriteLine(Header);

            IList<LabeledMetric> metrics;
            lock (_metricsLock)
            {
                metrics = _metrics.Values.ToList(); // copy
            }

            var exposed = 0;
            var endOfLife = DateTime.Now - TimeSpan.FromSeconds(TTL);
            foreach (var metric in metrics)
            {
                if (!metric.ExposeAlways && metric.LastUpdated < endOfLife)
                    continue;

                metric.ExposeTo(stream);
                ++exposed;
            }

            return exposed;
        }

        private void KillDeadMetrics()
        {
            lock (_metricsLock)
            {
                logger.Info($"Doing metric extinction for {_baseName}...");
                var stopWatch = new Stopwatch();
                stopWatch.Start();
                var old = new Dictionary<LabelDict, LabeledMetric>(_metrics);
                _metrics.Clear();
                var eol = DateTime.Now - TimeSpan.FromSeconds(TTD);

                foreach (var (labels, metric) in old)
                {
                    if (!metric.ExposeAlways && metric.LastUpdated < eol)
                        continue;

                    _metrics[labels] = metric;
                }

                stopWatch.Stop();
                logger.Info(
                    $"Metrics extinction for {_baseName} took {stopWatch.Elapsed}; of {old.Count} metrics, {_metrics.Count} were retained");
            }

            Thread.Yield();
            KillDeadMetricsCycle();
        }

        private void KillDeadMetricsCycle()
        {
            Task.Delay(TimeSpan.FromSeconds(TTL)).ContinueWith(_ => KillDeadMetrics());
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
                if (!_metrics.TryGetValue(labels, out var metric))
                    return _metrics[labels] = CreateMetrics(labels);

                metric.LastUpdated = DateTime.Now;
                return metric;
            }
        }

        public void Drop(LabeledMetric metric)
        {
            lock (_metricsLock)
            {
                _metrics.Remove(metric.Labels);
            }
        }
    }
}