using System.Collections.Generic;
using csv_prometheus_exporter.Prometheus;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Parser
{
    public class ParsedMetrics
    {
        public readonly LabelDict Labels;
        public readonly IDictionary<string, double> Metrics = new Dictionary<string, double>(31);

        public ParsedMetrics([NotNull] LabelDict labels)
        {
            Labels = new LabelDict(labels);
        }
    }
}