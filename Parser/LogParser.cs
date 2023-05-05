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

namespace csv_prometheus_exporter.Parser;

internal sealed class StreamStarvationException : Exception
{
}

internal sealed class LogParser
{
  private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

  private readonly LabelDict _envLabel;
  private readonly IList<ColumnReader> _readers;
  private readonly Stream _stream;

  private LogParser(Stream stream, IList<ColumnReader> readers, string environment)
  {
    _stream = stream;
    _readers = readers;
    _envLabel = new LabelDict(environment);
  }

  private ParsedMetrics ConvertCsvLine(ICsvReaderRow line, LabelDict labels)
  {
    if (_readers.Count != line.Count)
    {
      throw new ParserError();
    }

    var result = new ParsedMetrics(labels);
    foreach (var (reader, column) in _readers.Zip(line, static (a, b) => new KeyValuePair<ColumnReader?, string>(a, b)))
    {
      reader?.Invoke(result, column);
    }

    return result;
  }

  private IEnumerable<ParsedMetrics?> ReadAll(int msTimeout, CancellationToken cancellationToken,
    char quotes = '"', char columnSeparator = ' ')
  {
    using (var sshStream = new SSHStream(_stream))
    using (var parser = new CsvReader(sshStream, Encoding.UTF8,
             new CsvReader.Config
               { Quotes = quotes, ColumnSeparator = columnSeparator, WithQuotes = false, ReadinBufferSize = 64 }))
    {
      while (_stream.CanRead && !cancellationToken.IsCancellationRequested)
      {
        ParsedMetrics? result = null;
        try
        {
          var task = parser.MoveNextAsync(cancellationToken);
          if (!task.Wait(msTimeout, cancellationToken))
          {
            throw new StreamStarvationException();
          }

          if (task.Result)
          {
            result = ConvertCsvLine(parser.Current!, _envLabel);
          }
        }
        catch (StreamStarvationException)
        {
          throw;
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

        MetricBase.SSHBytesIn.WithLabels(_envLabel).Add(sshStream.TotalRead);
        sshStream.TotalRead = 0;
      }

      MetricBase.SSHBytesIn.WithLabels(_envLabel).Add(sshStream.TotalRead);
      sshStream.TotalRead = 0;
    }

    if (!_stream.CanRead)
    {
      logger.Info("End of stream");
    }
    else if (cancellationToken.IsCancellationRequested)
    {
      logger.Info("Thread termination requested");
    }
    else
    {
      logger.Warn("Unknown reason for parsing cancellation");
    }
  }

  internal static void ParseStream(Stream stream, string environment, string target, IList<ColumnReader> readers,
    IDictionary<string, MetricBase> metrics, int msTimeout, CancellationToken cancellationToken)
  {
    if (string.IsNullOrEmpty(environment))
    {
      throw new ArgumentException("Environment must not be empty", nameof(environment));
    }

    var envDict = new LabelDict(environment);
    var envTargetDict = new LabelDict(environment);
    envTargetDict.Set("target", target);

    foreach (var entry in new LogParser(stream, readers, environment).ReadAll(msTimeout, cancellationToken))
    {
      if (entry == null)
      {
        if (cancellationToken.IsCancellationRequested)
        {
          break;
        }

        MetricBase.ParserErrors.WithLabels(envDict).Add(1);
        MetricBase.ParserErrorsPerTarget.WithLabels(envTargetDict).Add(1);
        continue;
      }

      MetricBase.LinesParsed.WithLabels(entry.Labels).Add(1);
      var labels = new LabelDict(entry.Labels);
      labels.Set("target", target);
      MetricBase.LinesParsedPerTarget.WithLabels(labels).Add(1);

      foreach (var (name, amount) in entry.Metrics)
      {
        metrics[name].WithLabels(entry.Labels).Add(amount);
      }

      if (cancellationToken.IsCancellationRequested)
      {
        break;
      }
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
      ["parser_errors"] = new("parser_errors", "Number of lines which could not be parsed",
        MetricsType.Counter),
      ["lines_parsed"] = new("lines_parsed", "Number of successfully parsed lines",
        MetricsType.Counter),
      ["parser_errors_per_target"] = new("parser_errors",
        "Number of lines which could not be parsed",
        MetricsType.Counter),
      ["lines_parsed_per_target"] = new("lines_parsed", "Number of successfully parsed lines",
        MetricsType.Counter),
      ["connected"] = new("connected", "Whether this target is currently being scraped",
        MetricsType.Gauge, null,
        Resilience.Zombie),
    };

    return result;
  }

  [Test]
  public void TestReadTimeout()
  {
    var streamMock = new Mock<Stream>();
    streamMock.Setup(static stream => stream.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
      .Callback(static () => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
    streamMock.Setup(static stream => stream.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
        It.IsAny<CancellationToken>()))
      .Callback(static () => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
    streamMock.Setup(static stream => stream.CanRead)
      .Returns(true);

    var readers = new List<ColumnReader>();
    var sw = new Stopwatch();
    sw.Start();
    Assert.That(
      () => LogParser.ParseStream(streamMock.Object, "env", "tgt", readers, CreateMetricsDict(), 200,
        CancellationToken.None), Throws.TypeOf<StreamStarvationException>());
    sw.Stop();
    Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(ReadSleepSeconds));
  }

  [Test]
  public void TestCancellation()
  {
    var streamMock = new Mock<Stream>();
    streamMock.Setup(static stream => stream.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
      .Callback(static () => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
    streamMock.Setup(static stream => stream.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
        It.IsAny<CancellationToken>()))
      .Callback(static () => { Thread.Sleep(TimeSpan.FromSeconds(ReadSleepSeconds)); });
    streamMock.Setup(static stream => stream.CanRead)
      .Returns(true);

    var readers = new List<ColumnReader>();
    var tokenSource = new CancellationTokenSource();
    var task = Task.Run(
      () => LogParser.ParseStream(streamMock.Object, "env", "tgt", readers, CreateMetricsDict(), int.MaxValue,
        tokenSource.Token), tokenSource.Token);
    Thread.Sleep(stepDelay);
    Assert.That(task.Status, Is.EqualTo(TaskStatus.Running));
    tokenSource.Cancel();
    Thread.Sleep(stepDelay);
    Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
  }
}
