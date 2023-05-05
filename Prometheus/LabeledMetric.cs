using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace csv_prometheus_exporter.Prometheus;

public abstract class LabeledMetric
{
  private readonly MetricBase _metricBase;

  internal readonly LabelDict Labels;

  internal DateTime LastUpdated = DateTime.Now;

  protected LabeledMetric(MetricBase metricBase, LabelDict labels)
  {
    _metricBase = metricBase;
    Labels = labels;
  }

  internal Resilience Resilience => _metricBase.Resilience;

  protected string QualifiedName(string? le = null)
  {
    return $"{_metricBase.PrefixedName}{{{Labels.ToString(le)}}}";
  }

  internal abstract void ExposeTo(StreamWriter stream);

  internal abstract void Add(double value);

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

  internal void Drop()
  {
    _metricBase.Drop(this);
  }
}
