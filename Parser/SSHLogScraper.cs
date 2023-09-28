using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using csv_prometheus_exporter.Prometheus;
using NLog;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace csv_prometheus_exporter.Parser;

internal sealed class SSHLogScraper
{
  private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
  private readonly int _connectTimeout;

  private readonly string _environment;
  private readonly string _filename;
  private readonly string _host;
  private readonly IDictionary<string, MetricBase> _metrics;
  private readonly string _password;
  private readonly string _pkey;
  private readonly IList<ColumnReader> _readers;
  private readonly int _readTimeoutMs;
  private readonly string _username;

  public readonly CancellationTokenSource CancellationTokenSource = new();

  public Thread? Thread = null;

  public SSHLogScraper(string filename, string environment, IList<ColumnReader> readers, string host, string user,
    string password, string pkey, int connectTimeout, int readTimeoutMs,
    IDictionary<string, MetricBase> metrics)
  {
    _filename = filename;
    _host = host;
    _username = user;
    _password = password;
    _pkey = pkey;
    _environment = environment;
    _readers = readers;
    _connectTimeout = connectTimeout;
    _readTimeoutMs = readTimeoutMs;
    _metrics = metrics;
  }

  private SshClient CreateClient()
  {
    ConnectionInfo connInfo;
    if (string.IsNullOrEmpty(_pkey))
    {
      connInfo = new PasswordConnectionInfo(_host, 22, _username, _password);
    }
    else if (!string.IsNullOrEmpty(_password))
    {
      connInfo = new PrivateKeyConnectionInfo(_host, 22, _username,
        new PrivateKeyFile(_pkey, _password));
    }
    else
    {
      connInfo = new PrivateKeyConnectionInfo(_host, 22, _username,
        new PrivateKeyFile(_pkey));
    }

    connInfo.Timeout = TimeSpan.FromSeconds(_connectTimeout);
    return new SshClient(connInfo);
  }

  public void Run()
  {
    logger.Info($"Scraper thread for {_filename} on {_host} became alive");
    var envHostDict = new LabelDict(_environment);
    envHostDict.Set("host", _host);
    var connected = MetricBase.Connected.WithLabels(envHostDict) as Gauge;
    Debug.Assert(connected != null);
    try
    {
      while (!CancellationTokenSource.IsCancellationRequested)
      {
        try
        {
          connected.Set(0);
          try
          {
            logger.Info($"Trying to establish connection to {_host}");
            using (var client = CreateClient())
            {
              client.Connect();
              connected.Set(1);
              logger.Info($"Starting tailing {_filename} on {_host}");
              var cmd = client.CreateCommand($"tail -n0 --follow=name \"{_filename}\" 2>/dev/null");
              var tmp = cmd.BeginExecute();
              ((PipeStream)cmd.OutputStream).BlockLastReadBuffer = true;
              try
              {
                LogParser.ParseStream(cmd.OutputStream, _environment, _host, _readers, _metrics,
                  _readTimeoutMs,
                  CancellationTokenSource.Token);

                cmd.EndExecute(tmp);

                if (cmd.ExitStatus != 0)
                {
                  logger.Warn($"Tail command failed with exit code {cmd.ExitStatus} on {_host}");
                }
                else
                {
                  logger.Info($"Tail command finished successfully on {_host}");
                }
              }
              catch (StreamStarvationException)
              {
                logger.Warn($"SSH stream starvation for {_filename} on {_host}");
                cmd.CancelAsync();
              }
            }
          }
          catch (SshOperationTimeoutException ex)
          {
            logger.Error($"Timeout on {_host}: {ex.Message}");
          }
          catch (SshConnectionException ex)
          {
            logger.Error($"Failed to connect to {_host}: {ex.Message}");
          }
          catch (SshAuthenticationException ex)
          {
            logger.Error($"Failed to authenticate for {_host}: {ex.Message}");
          }
          catch (SocketException ex)
          {
            logger.Error($"Error on socket for {_host} (check firewall?): {ex.Message}");
          }
          catch (Exception ex)
          {
            logger.Fatal(ex, $"Unhandled exception on {_host}: {ex.Message}");
          }

          connected.Set(0);
          logger.Info($"Will retry connecting to {_host} in 30 seconds");
          if (CancellationTokenSource.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
          {
            break;
          }
        }
        finally
        {
          connected.Set(0);
        }
      }
    }
    finally
    {
      connected.Drop();
    }
  }
}
