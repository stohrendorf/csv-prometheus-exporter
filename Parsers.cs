using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using csv_prometheus_exporter.MetricsImpl;
using CsvParser;
using JetBrains.Annotations;
using NLog;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace csv_prometheus_exporter
{
    public class ParsedMetrics
    {
        public readonly LabelDict Labels;
        public readonly IDictionary<string, double> Metrics = new Dictionary<string, double>();

        public ParsedMetrics([NotNull] LabelDict labels)
        {
            Labels = new LabelDict(labels);
        }
    }

    public delegate void Reader(ParsedMetrics parsedMetrics, string value);

    public class ParserError : Exception
    {
    }

    public static class ValueParsers
    {
        public static Reader LabelReader(string name)
        {
            return (metric, value) => metric.Labels.Set(name, value);
        }

        public static Reader RequestHeaderReader()
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

        public static Reader ClfNumberReader(string name)
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

        public static Reader NumberReader(string name)
        {
            return (metric, value) =>
            {
                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }
    }

    public class LogParser
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        private readonly LabelDict _labels;
        private readonly IList<Reader> _readers;
        private readonly Stream _stream;

        private LogParser(Stream stream, IList<Reader> readers, string environment)
        {
            _stream = stream;
            _readers = readers;
            _labels = new LabelDict(environment);
        }

        private ParsedMetrics ConvertCsvLine(ICsvReaderRow line, LabelDict labels)
        {
            if (_readers.Count != line.Count) throw new ParserError();

            var result = new ParsedMetrics(labels);
            foreach (var (reader, column) in _readers.Zip(line, (a, b) => new KeyValuePair<Reader, string>(a, b)))
                reader?.Invoke(result, column);

            return result;
        }

        private IEnumerable<ParsedMetrics> ReadAll()
        {
            using (var parser = new CsvReader(_stream, Encoding.UTF8,
                new CsvReader.Config() {Quotes = '"', ColumnSeparator = ' '}))
            {
                parser.Reset();
                while (_stream.CanRead)
                {
                    ParsedMetrics result = null;
                    try
                    {
                        if (parser.MoveNext())
                        {
                            result = ConvertCsvLine(parser.Current, _labels);
                        }
                        else
                        {
                            parser.Reset();
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    yield return result;
                }
            }

            Logger.Info("End of stream");
        }

        public static void ParseFile(Stream stdout, string environment, IList<Reader> readers,
            IDictionary<string, MetricsMeta> metrics)
        {
            if (string.IsNullOrEmpty(environment))
                environment = "N/A";

            var envDict = new LabelDict(environment);

            foreach (var entry in new LogParser(stdout, readers, environment).ReadAll())
            {
                if (entry == null)
                {
                    metrics["parser_errors"].GetMetrics(envDict).Add(1);
                    continue;
                }

                metrics["lines_parsed"].GetMetrics(entry.Labels).Add(1);

                foreach (var (name, amount) in entry.Metrics)
                    if (metrics.TryGetValue(name, out var metric))
                        metric.GetMetrics(entry.Labels).Add(amount);
            }
        }
    }

    public sealed class SSHLogScraper
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        private readonly string _environment;
        private readonly string _filename;
        private readonly string _host;
        private readonly string _password;
        private readonly string _pkey;
        private readonly IList<Reader> _readers;
        private readonly int _timeout;
        private readonly string _username;
        public readonly IDictionary<string, MetricsMeta> Metrics = new ConcurrentDictionary<string, MetricsMeta>();

        public SSHLogScraper(string filename, string environment, IList<Reader> readers, string host, string user,
            string password, string pkey, int connectTimeout, IDictionary<string, MetricsMeta> metrics)
        {
            _filename = filename;
            _host = host;
            _username = user;
            _password = password;
            _pkey = pkey;
            _environment = environment;
            _readers = readers;
            _timeout = connectTimeout;
            foreach (var (name, metric) in metrics)
                Metrics[name] = new MetricsMeta(metric.BaseName, metric.Help, metric.Type, metric.Buckets);
            Metrics["parser_errors"] =
                new MetricsMeta("parser_errors", "Number of lines which could not be parsed", Type.Counter);
            Metrics["lines_parsed"] =
                new MetricsMeta("lines_parsed", "Number of successfully parsed lines", Type.Counter);
            Metrics["connected"] =
                new MetricsMeta("connected", "Whether this target is currently being scraped", Type.Gauge);
        }

        private SshClient CreateClient()
        {
            ConnectionInfo connInfo;
            if (string.IsNullOrEmpty(_pkey))
                connInfo = new PasswordConnectionInfo(_host, 22, _username, _password ?? string.Empty);
            else if (!string.IsNullOrEmpty(_password))
                connInfo = new PrivateKeyConnectionInfo(_host, 22, _username,
                    new PrivateKeyFile(_pkey, _password));
            else
                connInfo = new PrivateKeyConnectionInfo(_host, 22, _username,
                    new PrivateKeyFile(_pkey));

            connInfo.Timeout = TimeSpan.FromSeconds(_timeout);
            return new SshClient(connInfo);
        }

        public void Run()
        {
            Logger.Info($"Scraper thread for {_filename} on {_host} became alive");
            var envHostDict = new LabelDict(_environment);
            envHostDict.Set("host", _host);
            var connected = Metrics["connected"].GetMetrics(envHostDict) as LocalGauge;
            Debug.Assert(connected != null);
            while (true)
            {
                connected.Set(0);
                try
                {
                    Logger.Info($"Trying to establish connection to {_host}");
                    using (var client = CreateClient())
                    {
                        client.Connect();
                        connected.Set(1);
                        Logger.Info($"Starting tailing {_filename} on {_host}");
                        var cmd = client.CreateCommand($"tail -n0 -F \"{_filename}\" 2>/dev/null");
                        var tmp = cmd.BeginExecute();
                        LogParser.ParseFile(cmd.OutputStream, _environment, _readers, Metrics);

                        cmd.EndExecute(tmp);
                        if (cmd.ExitStatus != 0)
                            Logger.Warn($"Tail command failed with exit code {cmd.ExitStatus} on {_host}");
                    }
                }
                catch (SshOperationTimeoutException ex)
                {
                    Logger.Error($"Timeout on {_host}: {ex.Message}");
                }
                catch (SshConnectionException ex)
                {
                    Logger.Error($"Failed to connect to {_host}: {ex.Message}");
                }
                catch (SshAuthenticationException ex)
                {
                    Logger.Error($"Failed to authenticate for {_host}: {ex.Message}");
                }
                catch (SocketException ex)
                {
                    Logger.Error($"Error on socket for {_host} (check firewall?): {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Fatal(ex, $"Unhandled exception on {_host}");
                }

                connected.Set(0);
                Logger.Info($"Will retry connecting to {_host} in 30 seconds");
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }

            // ReSharper disable once FunctionNeverReturns
        }
    }
}