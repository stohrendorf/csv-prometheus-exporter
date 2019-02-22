using System;
using System.Globalization;

namespace csv_prometheus_exporter.Parser
{
    public delegate void ColumnReader(ParsedMetrics parsedMetrics, string value);

    /// <summary>
    /// Collection of available CSV column parsers.
    /// </summary>
    public static class ColumnReaders
    {
        public static ColumnReader Create(string type, string name)
        {
            switch (type)
            {
                case "number":
                    return NumberReader(name);
                case "clf_number":
                    return ClfNumberReader(name);
                case "request_header":
                    return RequestHeaderReader();
                case "label":
                    return LabelReader(name);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Invalid data type specified");
            }
        }

        private static ColumnReader LabelReader(string name)
        {
            return (metric, value) => metric.Labels.Set(name, value);
        }

        private static ColumnReader RequestHeaderReader()
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

        private static ColumnReader ClfNumberReader(string name)
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

        private static ColumnReader NumberReader(string name)
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