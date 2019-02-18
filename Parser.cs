using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using CsvHelper;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace csv_prometheus_exporter
{
    public class Metric
    {
        public readonly SortedDictionary<string, string> Labels;
        public readonly IDictionary<string, double> Metrics = new Dictionary<string, double>();


        public Metric(SortedDictionary<string, string> labels)
        {
            Labels = labels;
        }
    }

    public delegate void Reader(Metric metric, string value);

    public class ParserError : Exception
    {
    }

    public static class Parser
    {
        public static Reader LabelReader(string name)
        {
            return (metric, value) => metric.Labels[name] = value;
        }

        public static Reader RequestHeaderReader()
        {
            return (metric, value) =>
            {
                var request = value.Split(' ');
                if (request.Length != 3)
                    throw new ParserError();
                metric.Labels["request_method"] = request[0];
                metric.Labels["request_uri"] = request[1].Split('?')[0];
                metric.Labels["request_http_version"] = request[2];
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

                if (!double.TryParse(value, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }

        public static Reader NumberReader(string name)
        {
            return (metric, value) =>
            {
                if (!double.TryParse(value, out var dbl))
                    throw new ParserError();

                metric.Metrics[name] = dbl;
            };
        }
    }

    public class LogParser
    {
        private readonly StreamReader _stream;
        private readonly IList<Reader> _readers;
        private readonly SortedDictionary<string, string> _labels;

        private LogParser(StreamReader stream, IList<Reader> readers, SortedDictionary<string, string> labels)
        {
            _stream = stream;
            _readers = readers;
            _labels = labels;
        }

        private Metric ConvertCsvLine(ICollection<string> line, SortedDictionary<string, string> labels)
        {
            if (_readers.Count != line.Count)
            {
                throw new ParserError();
            }

            var result = new Metric(labels);
            foreach (var (reader, column) in _readers.Zip(line, (a, b) => new KeyValuePair<Reader, string>(a, b)))
            {
                reader?.Invoke(result, column);
            }

            return result;
        }

        private IEnumerable<Metric> ReadAll()
        {
            var parser = new CsvParser(_stream);
            parser.Configuration.BadDataFound =
                context => Console.WriteLine("Failed to parse CSV: {0}", context.RawRecord);
            parser.Configuration.Delimiter = " ";
            parser.Configuration.Quote = '"';
            parser.Configuration.IgnoreBlankLines = true;
            while (!_stream.EndOfStream)
            {
                Metric result = null;
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
        }

        public static void ParseFile(StreamReader stdout, string environment, IList<Reader> readers,
            IDictionary<string, double[]> histograms)
        {
            if (string.IsNullOrEmpty(environment))
                environment = "N/A";

            var envDict = new SortedDictionary<string, string> {["environment"] = environment};

            foreach (var entry in new LogParser(stdout, readers, envDict).ReadAll())
            {
                if (entry == null)
                {
                    MetricsUtil.IncCounter("parser_errors", "Number of lines which could not be parsed", envDict);
                    continue;
                }

                MetricsUtil.IncCounter("lines_parsed", "Number of successfully parsed lines", entry.Labels);

                foreach (var (name, amount) in entry.Metrics)
                {
                    if (histograms.TryGetValue(name, out var histogram))
                    {
                        MetricsUtil.Observe(name, $"Histogram of \"{name}\"", entry.Labels, histogram, amount);
                    }
                    else
                    {
                        MetricsUtil.IncCounter(name, $"Sum of \"{name}\"", entry.Labels, amount);
                    }
                }
            }
        }
    }

    class SSHLogScraper
    {
        private readonly string _host;
        private readonly string _username;
        private readonly string _password;
        private readonly string _pkey;
        private readonly string _filename;
        private readonly string _environment;
        private readonly int _timeout;
        private readonly IList<Reader> _readers;
        private readonly IDictionary<string, double[]> _histograms;

        public SSHLogScraper(string filename, string environment, IList<Reader> readers, string host, string user,
            string password, string pkey, int connectTimeout, IDictionary<string, double[]> histograms)
        {
            _filename = filename;
            _host = host;
            _username = user;
            _password = password;
            _pkey = pkey;
            _environment = environment;
            _readers = readers;
            _histograms = histograms;
            _timeout = connectTimeout;
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
                            LogParser.ParseFile(reader, _environment, _readers, _histograms);
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
                    Console.WriteLine("Unhandled exception on {0}: {1}", _host, ex.Message);
                }

                Console.WriteLine("Will retry connecting to {0} in 30 seconds", _host);
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }

            // ReSharper disable once FunctionNeverReturns
        }
    }
}