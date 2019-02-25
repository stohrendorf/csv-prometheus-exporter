using System;
using System.Globalization;
using csv_prometheus_exporter.Prometheus;
using NUnit.Framework;

namespace csv_prometheus_exporter.Parser
{
    public delegate void ColumnReader(ParsedMetrics parsedMetrics, string value);

    /// <summary>
    /// Collection of available CSV column parsers.
    /// </summary>
    public static class ColumnReaders
    {
        internal const string RequestUri = "request_uri";
        internal const string RequestHttpVersion = "request_http_version";
        internal const string RequestMethod = "request_method";

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

        internal static ColumnReader LabelReader(string name)
        {
            return (metric, value) => metric.Labels.Set(name, value);
        }

        internal static ColumnReader RequestHeaderReader()
        {
            return (metric, value) =>
            {
                var request = value.Split(' ');
                if (request.Length != 3)
                    throw new ParserError();
                metric.Labels.Set(RequestMethod, request[0]);
                metric.Labels.Set(RequestUri, request[1].Split('?')[0]);
                metric.Labels.Set(RequestHttpVersion, request[2]);
            };
        }

        internal static ColumnReader ClfNumberReader(string name)
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

        internal static ColumnReader NumberReader(string name)
        {
            return (metric, value) =>
            {
                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }
    }

    [TestFixture]
    public class ColumnReadersTest
    {
        [Test]
        public void TestNumberReader()
        {
            var reader = ColumnReaders.NumberReader("foo");
            var m = new ParsedMetrics(new LabelDict("env"));

            reader(m, "1");
            Assert.That(m.Metrics, Has.Exactly(1).Items);
            Assert.That(m.Metrics, Contains.Key("foo"));
            Assert.That(m.Metrics, Contains.Value(1.0));
            Assert.That(m.Labels.Count, Is.EqualTo(0));

            reader(m, "2.0");
            Assert.That(m.Metrics, Has.Exactly(1).Items);
            Assert.That(m.Metrics, Contains.Key("foo"));
            Assert.That(m.Metrics, Contains.Value(2.0));
            Assert.That(m.Labels.Count, Is.EqualTo(0));

            reader(m, "2.0e1");
            Assert.That(m.Metrics, Has.Exactly(1).Items);
            Assert.That(m.Metrics, Contains.Key("foo"));
            Assert.That(m.Metrics, Contains.Value(20.0));
            Assert.That(m.Labels.Count, Is.EqualTo(0));

            Assert.That(() => reader(m, ""), Throws.TypeOf<ParserError>());
            Assert.That(() => reader(m, "nope"), Throws.TypeOf<ParserError>());
        }

        [Test]
        public void TestClfNumberReader()
        {
            var reader = ColumnReaders.ClfNumberReader("foo");
            var m = new ParsedMetrics(new LabelDict("env"));

            reader(m, "-");
            Assert.That(m.Metrics, Has.Exactly(1).Items);
            Assert.That(m.Metrics, Contains.Key("foo"));
            Assert.That(m.Metrics, Contains.Value(0));
            Assert.That(m.Labels.Count, Is.EqualTo(0));

            Assert.That(() => reader(m, ""), Throws.TypeOf<ParserError>());
            Assert.That(() => reader(m, "nope"), Throws.TypeOf<ParserError>());
        }

        [Test]
        public void TestRequestHeaderReader()
        {
            var reader = ColumnReaders.RequestHeaderReader();
            var m = new ParsedMetrics(new LabelDict("env"));

            reader(m, "POST /uri HTTP/1.1");
            Assert.That(m.Metrics, Is.Empty);
            Assert.That(m.Labels.Count, Is.EqualTo(3));
            Assert.That(m.Labels.Get(ColumnReaders.RequestMethod), Is.EqualTo("POST"));
            Assert.That(m.Labels.Get(ColumnReaders.RequestUri), Is.EqualTo("/uri"));
            Assert.That(m.Labels.Get(ColumnReaders.RequestHttpVersion), Is.EqualTo("HTTP/1.1"));

            reader(m, "GET /res?foo=bar HTTP/1.0");
            Assert.That(m.Metrics, Is.Empty);
            Assert.That(m.Labels.Count, Is.EqualTo(3));
            Assert.That(m.Labels.Get(ColumnReaders.RequestMethod), Is.EqualTo("GET"));
            Assert.That(m.Labels.Get(ColumnReaders.RequestUri), Is.EqualTo("/res"));
            Assert.That(m.Labels.Get(ColumnReaders.RequestHttpVersion), Is.EqualTo("HTTP/1.0"));

            Assert.That(() => reader(m, "a"), Throws.TypeOf<ParserError>());
            Assert.That(() => reader(m, "a b"), Throws.TypeOf<ParserError>());
            Assert.That(() => reader(m, "a b c"), Throws.Nothing);
            Assert.That(() => reader(m, "a b c d"), Throws.TypeOf<ParserError>());
        }

        [Test]
        public void TestLabelReader()
        {
            var reader = ColumnReaders.LabelReader("foo");
            var m = new ParsedMetrics(new LabelDict("env"));

            reader(m, "bar");
            Assert.That(m.Metrics, Is.Empty);
            Assert.That(m.Labels.Count, Is.EqualTo(1));
            Assert.That(m.Labels.Get("foo"), Is.EqualTo("bar"));
        }

        [Test]
        public void TestCreate()
        {
            Assert.That(() => ColumnReaders.Create("number", "bla"), Throws.Nothing);
            Assert.That(() => ColumnReaders.Create("clf_number", "bla"), Throws.Nothing);
            Assert.That(() => ColumnReaders.Create("request_header", "bla"), Throws.Nothing);
            Assert.That(() => ColumnReaders.Create("label", "bla"), Throws.Nothing);
            Assert.That(() => ColumnReaders.Create("xxx", "bla"), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
