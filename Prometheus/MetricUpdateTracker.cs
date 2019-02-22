using System;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class MetricUpdateTracker
    {
        public readonly LabeledMetric Metric;
        public DateTime LastUpdated;

        public MetricUpdateTracker([NotNull] LabeledMetric metric, DateTime? lastUpdated = null)
        {
            LastUpdated = lastUpdated ?? DateTime.Now;
            Metric = metric;
        }

        public LabeledMetric TouchAndGet()
        {
            LastUpdated = DateTime.Now;
            return Metric;
        }
    }
}