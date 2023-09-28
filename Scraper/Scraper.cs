using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using csv_prometheus_exporter.Parser;
using csv_prometheus_exporter.Prometheus;
using csv_prometheus_exporter.Scraper.Config;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Web;
using YamlDotNet.Serialization;
using Environment = System.Environment;
using ILogger = NLog.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace csv_prometheus_exporter.Scraper;

file static class Scraper
{
  private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

  private static ScraperConfig ReadCoreConfig(out IList<ColumnReader> readers)
  {
    var scrapeConfigFilename = Environment.GetEnvironmentVariable("SCRAPECONFIG") ?? "/etc/scrapeconfig.yml";

    var config = new DeserializerBuilder()
      .Build()
      .Deserialize<ScraperConfig>(new StreamReader(scrapeConfigFilename));

    LoadReadersConfig(config.Global, out readers);

    return config;
  }

  private static void LoadFromScript(IDictionary<string, SSHLogScraper> scrapers, string scrapeConfigScript,
    IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
  {
    var split = scrapeConfigScript.Split(' ');
    var startInfo = new ProcessStartInfo
    {
      FileName = split[0],
      RedirectStandardOutput = true,
      RedirectStandardInput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var arg in split.Skip(1))
    {
      startInfo.ArgumentList.Add(arg);
    }

    var process = new Process { StartInfo = startInfo };
    process.Start();
    process.WaitForExit();
    var stdout = process.StandardOutput.ReadToEnd();
    var config = new DeserializerBuilder()
      .Build()
      .Deserialize<ScraperConfig>(stdout);
    if (config == null)
    {
      logger.Error("Failed to parse inventory script output:");
      logger.Error(stdout);
      return;
    }

    LoadScrapersConfig(scrapers, config, readers, metrics);
  }

  private static HashSet<string> LoadSshScrapersConfig(IDictionary<string, SSHLogScraper> scrapers,
    SSH? config, IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
  {
    var ids = new HashSet<string>();
    if (config?.Environments == null)
    {
      return ids;
    }

    foreach (var (envName, envConfig) in config.Environments)
    {
      foreach (var host in envConfig.Hosts)
      {
        var targetId =
          $"ssh://{host}/{envConfig.ConnectionSettings?.File ?? config.ConnectionSettings.File}";
        ids.Add(targetId);
        if (scrapers.ContainsKey(targetId))
        {
          continue;
        }

        var scraper = new SSHLogScraper(
          (envConfig.ConnectionSettings?.File ?? config.ConnectionSettings.File) ??
          throw new InvalidOperationException(),
          envName,
          readers,
          host,
          (envConfig.ConnectionSettings?.User ?? config.ConnectionSettings.User) ??
          throw new InvalidOperationException(),
          (envConfig.ConnectionSettings?.Password ?? config.ConnectionSettings.Password) ??
          throw new InvalidOperationException(),
          (envConfig.ConnectionSettings?.PKey ?? config.ConnectionSettings.PKey) ??
          throw new InvalidOperationException(),
          envConfig.ConnectionSettings?.ConnectTimeout ?? config.ConnectionSettings.ConnectTimeout ?? 30,
          envConfig.ConnectionSettings?.ReadTimeoutMs ??
          config.ConnectionSettings.ReadTimeoutMs ?? 60 * 1000,
          metrics
        );
        scraper.Thread = new Thread(() => scraper.Run());
        scrapers[targetId] = scraper;
        Startup.Scrapers[targetId] = scraper;
        scraper.Thread.IsBackground = true;
        scraper.Thread.Name = "scraper:" + targetId;
        scraper.Thread.Start();
      }
    }

    return ids;
  }

  private static void LoadReadersConfig(Global globalConfig, out IList<ColumnReader> readers)
  {
    readers = new List<ColumnReader>();
    var histogramBuckets = new Dictionary<string, double[]>();
    if (globalConfig.Histograms != null)
    {
      foreach (var (histogramName, buckets) in globalConfig.Histograms)
      {
        if (buckets == null || buckets.Count == 0)
        {
          histogramBuckets[histogramName] = Histogram.DefaultBuckets;
        }
        else
        {
          histogramBuckets[histogramName] = buckets.ToArray();
        }
      }
    }

    Startup.Metrics.Clear();
    foreach (var dict in globalConfig.Format)
    {
      if (dict == null)
      {
        throw new Exception("Invalid format specification");
      }

      if (dict.Count != 1)
      {
        throw new Exception();
      }

      var (name, typeDef) = dict.First();
      switch (typeDef)
      {
        case null:
          throw new Exception("Missing label name");
        case "label" when name == "environment":
          throw new Exception("'environment' is a reserved label name");
      }

      if (typeDef != "label" && (name == "parser_errors" || name == "lines_parsed" ||
                                 name == "parser_errors_per_target" || name == "lines_parsed_per_target"))
      {
        throw new Exception($"'{name}' is a reserved metric name");
      }

      var type = typeDef;
      if (typeDef.Contains('+'))
      {
        var typeAndHistogram = typeDef.Split('+');
        type = typeAndHistogram[0].Trim();
        if (type == "label")
        {
          throw new Exception("Labels cannot be used as histograms");
        }

        var histogramType = typeAndHistogram[1].Trim();
        if (!histogramBuckets.ContainsKey(histogramType))
        {
          throw new Exception($"Histogram type {histogramType} is not defined");
        }

        Startup.Metrics[name] = new MetricBase(name, $"Histogram of {name}", MetricsType.Histogram,
          histogramBuckets[histogramType]);
      }
      else if (type != "label" && type != "request_header")
      {
        Startup.Metrics[name] = new MetricBase(name, $"Sum of {name}", MetricsType.Counter);
      }

      readers.Add(ColumnReaders.Create(type, name));
    }

    MetricBase.GlobalPrefix = globalConfig.Prefix;
    MetricBase.TTL = globalConfig.TTL;
    MetricBase.BackgroundResilience = globalConfig.BackgroundResilience;
    MetricBase.LongTermResilience = globalConfig.LongTermResilience;
  }

  private static void LoadScrapersConfig(IDictionary<string, SSHLogScraper> scrapers, ScraperConfig scrapeConfig,
    IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
  {
    var loadedIds = LoadSshScrapersConfig(scrapers, scrapeConfig.SSH, readers, metrics);
    foreach (var (scraperId, scraper) in scrapers)
    {
      if (!loadedIds.Contains(scraperId))
      {
        scraper.CancellationTokenSource.Cancel();
      }
    }
  }

  private static void Main(string[] args)
  {
    LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config", false);
    ServicePointManager.DefaultConnectionLimit = 1;

    ThreadPool.GetMinThreads(out var a, out var b);
    logger.Info($"Current threadpool min threads: worker={a}, completionPort={b}");
    if (Environment.ProcessorCount > 0)
    {
      if (!ThreadPool.SetMinThreads(64 * Environment.ProcessorCount, 32 * Environment.ProcessorCount))
      {
        throw new Exception("Failed to set minimum thread count");
      }

      ThreadPool.GetMinThreads(out a, out b);
      logger.Info($"New threadpool min threads: worker={a}, completionPort={b}");
    }
    else
    {
      logger.Warn("Failed to determine logical processeor count, threadpool threads unchanged");
    }

    var scrapeConfig = ReadCoreConfig(out var readers);
    var scrapers = new Dictionary<string, SSHLogScraper>();
    LoadScrapersConfig(scrapers, scrapeConfig, readers, Startup.Metrics);

    if (scrapeConfig.Script != null)
    {
      var loaderThread = new Thread(() =>
      {
        while (true)
        {
          LoadFromScript(scrapers, scrapeConfig.Script, readers, Startup.Metrics);

          if (scrapeConfig.ReloadInterval.HasValue)
          {
            Thread.Sleep(TimeSpan.FromSeconds(scrapeConfig.ReloadInterval.Value));
          }
          else
          {
            Thread.Sleep(-1);
          }
        }

        // ReSharper disable once FunctionNeverReturns
      }) { Name = "inventory-loader-thread" };

      loaderThread.Start();
    }

    WebHost.CreateDefaultBuilder(args)
      .UseStartup<Startup>()
      .UseKestrel(static options => { options.ListenAnyIP(5000); })
      .ConfigureLogging(static logging =>
      {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Information);
      })
      .UseNLog()
      .Build()
      .Run();
    // ReSharper disable once FunctionNeverReturns
  }
}
