using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using csv_prometheus_exporter.Prometheus;
using NLog;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace csv_prometheus_exporter.Parser
{
    public sealed class SSHLogScraper
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        private readonly string _environment;
        private readonly string _filename;
        private readonly string _host;
        private readonly IDictionary<string, MetricBase> _metrics;
        private readonly string _password;
        private readonly string _pkey;
        private readonly IList<ColumnReader> _readers;
        private readonly int _timeout;
        private readonly string _username;

        public SSHLogScraper(string filename, string environment, IList<ColumnReader> readers, string host, string user,
            string password, string pkey, int connectTimeout, IDictionary<string, MetricBase> metrics)
        {
            _filename = filename;
            _host = host;
            _username = user;
            _password = password;
            _pkey = pkey;
            _environment = environment;
            _readers = readers;
            _timeout = connectTimeout;
            _metrics = metrics;
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
            var connected = _metrics["connected"].WithLabels(envHostDict) as Gauge;
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
                        LogParser.ParseFile(cmd.OutputStream, _environment, _readers, _metrics);

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