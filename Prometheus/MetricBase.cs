using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace csv_prometheus_exporter.Prometheus;

internal enum Resilience
{
  Weak,
  LongTerm,
  Zombie,
}

/// <summary>
///   Basic metric definition and container for its instances.
/// </summary>
public sealed class MetricBase
{
  private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

  private static string? _globalPrefix;

  internal static int TTL = 60;
  internal static int BackgroundResilience = 1;
  internal static int LongTermResilience = 10;

  internal static readonly MetricBase ParserErrors = new("parser_errors",
    "Number of lines which could not be parsed", MetricsType.Counter, null, Resilience.LongTerm);

  internal static readonly MetricBase LinesParsed = new("lines_parsed",
    "Number of successfully parsed lines", MetricsType.Counter, null, Resilience.LongTerm);

  internal static readonly MetricBase ParserErrorsPerTarget = new("parser_errors_per_target",
    "Number of lines which could not be parsed", MetricsType.Counter, null, Resilience.LongTerm);

  internal static readonly MetricBase LinesParsedPerTarget = new("lines_parsed_per_target",
    "Number of successfully parsed lines", MetricsType.Counter, null, Resilience.LongTerm);

  internal static readonly MetricBase Connected = new("connected",
    "Whether this target is currently being scraped", MetricsType.Gauge, null, Resilience.Zombie);

  internal static readonly MetricBase SSHBytesIn = new("ssh_bytes_in",
    "Total bytes read over SSH", MetricsType.Counter, null, Resilience.LongTerm);

  private readonly string _baseName;
  private readonly double[]? _buckets;
  private readonly string _help;

  private readonly IDictionary<LabelDict, LabeledMetric> _metrics =
    new Dictionary<LabelDict, LabeledMetric>();

  private readonly object _metricsLock = new();

  internal readonly Resilience Resilience;

  internal MetricBase(string baseName, string help, MetricsType type,
    double[]? buckets = null, Resilience resilience = Resilience.Weak)
  {
    if (!IsValidMetricsBasename(baseName))
    {
      throw new ArgumentException("Invalid metrics name", nameof(baseName));
    }

    _baseName = baseName;
    _help = help;
    Type = type;
    Resilience = resilience;
    if (buckets != null)
    {
      _buckets = buckets.ToArray();
    }

    if (type == MetricsType.Histogram && buckets == null)
    {
      throw new ArgumentException("Must provide buckets if type is histogram", nameof(buckets));
    }

    if (type != MetricsType.Histogram && buckets != null)
    {
      throw new ArgumentException("Must not provide buckets if type is not histogram", nameof(buckets));
    }

    if (type == MetricsType.Counter && !_baseName.EndsWith("_total"))
    {
      _baseName += "_total";
    }

    KillDeadMetricsCycle();
  }

  internal static string? GlobalPrefix
  {
    private get => _globalPrefix;

    set
    {
      if (!string.IsNullOrEmpty(value) && !IsValidMetricsBasename(value))
      {
        throw new ArgumentException("Invalid prefix", nameof(value));
      }

      _globalPrefix = value;
    }
  }

  /// <summary>
  ///   Time To Death
  /// </summary>
  private static int BackgroundTime => (BackgroundResilience + 1) * TTL;

  private static int LongTermTime => (LongTermResilience + 1) * TTL;

  internal MetricsType Type { get; }

  internal string PrefixedName => !string.IsNullOrEmpty(GlobalPrefix) ? $"{GlobalPrefix}:{_baseName}" : _baseName;

  private string Header =>
    string.Format("# HELP {0} {1}\n# TYPE {0} {2}", PrefixedName,
      _help.Replace(@"\", @"\\").Replace("\n", @"\n"), Type.ToString().ToLower());

  private static bool IsValidMetricsBasename(string name)
  {
    return new Regex("^[a-zA-Z0-9:_]+$").IsMatch(name)
           && !new[] { "_sum", "_count", "_bucket", "_total" }.Any(name.EndsWith);
  }

  internal int ExposeTo(StreamWriter stream)
  {
    stream.WriteLine(Header);

    IList<LabeledMetric> metrics;
    lock (_metricsLock)
    {
      metrics = _metrics.Values.ToList(); // copy
    }

    var exposed = 0;
    var endOfLife = DateTime.Now - TimeSpan.FromSeconds(TTL);
    var endOfLongTermLife = DateTime.Now - TimeSpan.FromSeconds(LongTermTime);
    foreach (var metric in metrics)
    {
      switch (metric.Resilience)
      {
        case Resilience.Weak:
          if (metric.LastUpdated < endOfLife)
          {
            continue;
          }

          break;
        case Resilience.LongTerm:
          if (metric.LastUpdated < endOfLongTermLife)
          {
            continue;
          }

          break;
        case Resilience.Zombie:
          break;
        default:
          throw new ArgumentOutOfRangeException();
      }

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
      var endOfLife = DateTime.Now - TimeSpan.FromSeconds(BackgroundTime);
      var endOfLongTermLife = DateTime.Now - TimeSpan.FromSeconds(LongTermTime);

      foreach (var (labels, metric) in old)
      {
        switch (metric.Resilience)
        {
          case Resilience.Weak:
            if (metric.LastUpdated < endOfLife)
            {
              continue;
            }

            break;
          case Resilience.LongTerm:
            if (metric.LastUpdated < endOfLongTermLife)
            {
              continue;
            }

            break;
          case Resilience.Zombie:
            break;
          default:
            throw new ArgumentOutOfRangeException();
        }

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

  private LabeledMetric CreateMetrics(LabelDict labels)
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

  internal LabeledMetric WithLabels(LabelDict labels)
  {
    lock (_metricsLock)
    {
      if (!_metrics.TryGetValue(labels, out var metric))
      {
        return _metrics[labels] = CreateMetrics(labels);
      }

      metric.LastUpdated = DateTime.Now;
      return metric;
    }
  }

  internal void Drop(LabeledMetric metric)
  {
    lock (_metricsLock)
    {
      _metrics.Remove(metric.Labels);
    }
  }
}
