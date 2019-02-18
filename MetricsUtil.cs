using System.Collections.Generic;
using System.Linq;
using Prometheus;

namespace csv_prometheus_exporter
{
    public static class MetricsUtil
    {
        public static string Prefix { private get; set; }

        private static void IncImpl(string fullName, string documentation,
            SortedDictionary<string, string> labels, double amount)
        {
            var counter = Metrics.CreateCounter(fullName, documentation, labels.Keys.ToArray());
            counter.WithLabels(labels.Values.ToArray()).Inc(amount);
        }

        private static void SetGaugeImpl(string fullName, string documentation,
            SortedDictionary<string, string> labels, double amount)
        {
            var gauge = Metrics.CreateGauge(fullName, documentation, labels.Keys.ToArray());
            gauge.WithLabels(labels.Values.ToArray()).Inc(amount);
        }

        private static void ObserveImpl(string fullName, string documentation,
            SortedDictionary<string, string> labels, double[] buckets, double amount)
        {
            var histogram = Metrics.CreateHistogram(fullName, documentation, new HistogramConfiguration
            {
                Buckets = buckets,
                LabelNames = labels.Keys.ToArray()
            });
            histogram.WithLabels(labels.Values.ToArray()).Observe(amount);
        }

        private static string ToFullName(string name)
        {
            return $"{Prefix}:{name}";
        }

        public static void IncCounter(string name, string documentation, SortedDictionary<string, string> labels,
            double amount = 1)
        {
            IncImpl(ToFullName(name), documentation, labels, amount);
        }

        public static void SetGauge(string name, string documentation, SortedDictionary<string, string> labels,
            double amount)
        {
            SetGaugeImpl(ToFullName(name), documentation, labels, amount);
        }

        public static void Observe(string name, string documentation, SortedDictionary<string, string> labels,
            double[] buckets, double amount)
        {
            ObserveImpl(ToFullName(name), documentation, labels, buckets, amount);
        }
    }
}
