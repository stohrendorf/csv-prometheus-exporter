using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using CsvHelper;
using JetBrains.Annotations;
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
        private readonly LabelDict _labels;
        private readonly IList<Reader> _readers;
        private readonly StreamReader _stream;

        private LogParser(StreamReader stream, IList<Reader> readers, string environment)
        {
            _stream = stream;
            _readers = readers;
            _labels = new LabelDict(environment);
        }

        private ParsedMetrics ConvertCsvLine(ICollection<string> line, LabelDict labels)
        {
            if (_readers.Count != line.Count) throw new ParserError();

            var result = new ParsedMetrics(labels);
            foreach (var (reader, column) in _readers.Zip(line, (a, b) => new KeyValuePair<Reader, string>(a, b)))
                reader?.Invoke(result, column);

            return result;
        }

        private IEnumerable<ParsedMetrics> ReadAll()
        {
            var parser = new CsvParser(_stream);
            parser.Configuration.BadDataFound =
                context => Console.WriteLine("Failed to parse CSV: {0}", context.RawRecord);
            parser.Configuration.Delimiter = " ";
            parser.Configuration.Quote = '"';
            parser.Configuration.IgnoreBlankLines = true;
            while (_stream.BaseStream.CanRead)
            {
                ParsedMetrics result = null;
                try
                {
                    result = ConvertCsvLine(parser.Read(), _labels);
                }
                catch
                {
                    // ignored
                }

                yield return result;
            }

            Console.WriteLine("End of stream");
        }

        public static void ParseFile(StreamReader stdout, string environment, IList<Reader> readers,
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
            Console.WriteLine("Scraper thread for {0} on {1} became alive", _filename, _host);
            while (true)
            {
                try
                {
                    Console.WriteLine("Trying to establish connection to {0}", _host);
                    using (var client = CreateClient())
                    {
                        client.Connect();
                        Console.WriteLine("Starting tailing {0} on {1}", _filename, _host);
                        var cmd = client.CreateCommand($"tail -n0 -F \"{_filename}\" 2>/dev/null");
                        var tmp = cmd.BeginExecute();
                        using (var reader = new StreamReader(cmd.OutputStream))
                        {
                            LogParser.ParseFile(reader, _environment, _readers, Metrics);
                        }

                        cmd.EndExecute(tmp);
                        if (cmd.ExitStatus != 0)
                            Console.WriteLine("Tail command failed with exit code {0} on {1}", cmd.ExitStatus, _host);
                    }
                }
                catch (SshOperationTimeoutException ex)
                {
                    Console.WriteLine("Timeout on {0}: {1}", _host, ex.Message);
                }
                catch (SshConnectionException ex)
                {
                    Console.WriteLine("Failed to connect to {0}: {1}", _host, ex.Message);
                }
                catch (SshAuthenticationException ex)
                {
                    Console.WriteLine("Failed to authenticate for {0}: {1}", _host, ex.Message);
                }
                catch (SocketException ex)
                {
                    Console.WriteLine("Error on socket for {0} (check firewall?): {1}", _host, ex.Message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unhandled exception on {0}: {1}", _host, ex);
                }

                Console.WriteLine("Will retry connecting to {0} in 30 seconds", _host);
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }

            // ReSharper disable once FunctionNeverReturns
        }
    }
}