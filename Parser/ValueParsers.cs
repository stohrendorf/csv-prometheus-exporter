using System.Globalization;

namespace csv_prometheus_exporter.Parser
{
    public static class ValueParsers
    {
        public static ColumnReader LabelReader(string name)
        {
            return (metric, value) => metric.Labels.Set(name, value);
        }

        public static ColumnReader RequestHeaderReader()
        {
            return (metric, value) =>
            {
                var request = value.Split(' ');
                if (request.Length != 3)
                    throw new ParserError();
                metric.Labels.Set("request_method", request[0]);
                metric.Labels.Set("request_uri", request[1].Split('?')[0]);
                metric.Labels.Set("request_http_version", request[2]);
            };
        }

        public static ColumnReader ClfNumberReader(string name)
        {
            return (metric, value) =>
            {
                if (value == "-")
                {
                    metric.Metrics[name] = 0.0;
                    return;
                }

                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }

        public static ColumnReader NumberReader(string name)
        {
            return (metric, value) =>
            {
                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }
    }
}