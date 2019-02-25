using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using csv_prometheus_exporter.Prometheus;
using CsvParser;
using Moq;
using NLog;
using NUnit.Framework;

namespace csv_prometheus_exporter.Parser
{
    public class LogParser
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private readonly LabelDict _labels;
        private readonly IList<ColumnReader> _readers;
        private readonly Stream _stream;

        private LogParser(Stream stream, IList<ColumnReader> readers, string environment)
        {
            _stream = stream;
            _readers = readers;
            _labels = new LabelDict(environment);
        }

        private ParsedMetrics ConvertCsvLine(ICsvReaderRow line, LabelDict labels)
        {
            if (_readers.Count != line.Count) throw new ParserError();

            var result = new ParsedMetrics(labels);
            foreach (var (reader, column) in _readers.Zip(line, (a, b) => new KeyValuePair<ColumnReader, string>(a, b)))
                reader?.Invoke(result, column);

            return result;
        }

        private IEnumerable<ParsedMetrics> ReadAll(int msTimeout, CancellationToken cancellationToken,
            char quotes = '"', char columnSeparator = ' ')
        {
            using (var sshStream = new SSHStream(_stream))
            using (var parser = new CsvReader(sshStream, Encoding.UTF8,
                new CsvReader.Config
                    {Quotes = quotes, ColumnSeparator = columnSeparator, WithQuotes = false}))
            {
                while (_stream.CanRead && !cancellationToken.IsCancellationRequested)
                {
                    ParsedMetrics result = null;
                    try
                    {
                        var task = parser.MoveNextAsync(cancellationToken);
                        if (!task.Wait(msTimeout, cancellationToken))
                        {
                            logger.Warn("Source stream timed out");
                            yield break;
                        }

                        if (task.Result)
                            result = ConvertCsvLine(parser.Current, _labels);
                    }
                    catch (ParserError)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Info("Parser cancellation requested");
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.Fatal(ex, $"Unexpected exception: {ex.Message}");
                    }

                    yield return result;
                }
            }

            if (!_stream.CanRead)
                logger.Info("End of stream");
            else if (cancellationToken.IsCancellationRequested)
                logger.Info("Thread termination requested");
            else
                logger.Warn("Unknown reason for parsing cancellation");
        }

        public static void ParseFile(Stream stream, string environment, IList<ColumnReader> readers,
            IDictionary<string, MetricBase> metrics, int msTimeout, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(environment))
                throw new ArgumentException("Environment must not be empty", nameof(environment));

            var envDict = new LabelDict(environment);

            foreach (var entry in new LogParser(stream, readers, environment).ReadAll(msTimeout, cancellationToken))
            {
                if (entry == null)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    metrics["parser_errors"].WithLabels(envDict).Add(1);
                    continue;
                }

                metrics["lines_parsed"].WithLabels(entry.Labels).Add(1);

                foreach (var (name, amount) in entry.Metrics)
                    metrics[name].WithLabels(entry.Labels).Add(amount);

                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }
    }

    [TestFixture]
    public class LogParserTest
    {
        private const int ReadSleepSeconds = 10;
        private static readonly TimeSpan stepDelay = TimeSpan.FromSeconds(2);

        private static IDictionary<string, MetricBase> CreateMetricsDict()
        {
            var result = new Dictionary<string, MetricBase>
            {
                ["parser_errors"] = new MetricBase("parser_errors", "Number of lines which could not be parsed",
                    MetricsType.Counter),
                ["lines_parsed"] = new MetricBase("lines_parsed", "Number of successfully parsed lines",
                    MetricsType.Counter),
                ["connected"] = new MetricBase("connected", "Whether this target is currently being scraped",
                    MetricsType.Gauge, null,
                    true)
            };

            return result;
        }

        [Test]
        public void TestReadTimeout()
        {
            var streamMock = new Mock<Stream>();
            streamMock.Setup(stream => stream.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
                .Callback(() => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
            streamMock.Setup(stream => stream.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
            streamMock.Setup(stream => stream.CanRead)
                .Returns(true);

            var readers = new List<ColumnReader>();
            var sw = new Stopwatch();
            sw.Start();
            LogParser.ParseFile(streamMock.Object, "env", readers, CreateMetricsDict(), 200, CancellationToken.None);
            sw.Stop();
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(ReadSleepSeconds));
        }

        [Test]
        public void TestCancellation()
        {
            var streamMock = new Mock<Stream>();
            streamMock.Setup(stream => stream.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
                .Callback(() => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
            streamMock.Setup(stream => stream.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
            streamMock.Setup(stream => stream.CanRead)
                .Returns(true);

            var readers = new List<ColumnReader>();
            var tokenSource = new CancellationTokenSource();
            var task = Task.Run(
                () => LogParser.ParseFile(streamMock.Object, "env", readers, CreateMetricsDict(), int.MaxValue,
                    tokenSource.Token), tokenSource.Token);
            Thread.Sleep(stepDelay);
            Assert.That(task.Status, Is.EqualTo(TaskStatus.Running));
            tokenSource.Cancel();
            Thread.Sleep(stepDelay);
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        }
    }
}
