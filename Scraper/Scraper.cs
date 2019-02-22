using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using csv_prometheus_exporter.Parser;
using csv_prometheus_exporter.Prometheus;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Web;
using YamlDotNet.RepresentationModel;
using ILogger = NLog.ILogger;
using LogLevel = NLog.LogLevel;

namespace csv_prometheus_exporter.Scraper
{
    internal static class Scraper
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        private static void ReadCoreConfig(out IList<ColumnReader> readers, out string scrapeConfigScript,
            out int? configReloadInterval, out YamlMappingNode scrapeConfig)
        {
            var scrapeConfigFilename = Environment.GetEnvironmentVariable("SCRAPECONFIG") ?? "/etc/scrapeconfig.yml";
            var yaml = new YamlStream();
            yaml.Load(new StreamReader(scrapeConfigFilename));
            scrapeConfig = (YamlMappingNode) yaml.Documents[0].RootNode;

            LoadReadersConfig(scrapeConfig, out readers);
            configReloadInterval = scrapeConfig.Int("reload-interval");

            scrapeConfigScript = scrapeConfig.String("script");
        }

        private static void LoadFromScript(IDictionary<string, Thread> threads, string scrapeConfigScript,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var yaml = new YamlStream();
            var split = scrapeConfigScript.Split(' ');
            var startInfo = new ProcessStartInfo
            {
                FileName = split[0],
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in split.Skip(1)) startInfo.ArgumentList.Add(arg);

            var process = new Process {StartInfo = startInfo};
            process.Start();
            process.WaitForExit();
            yaml.Load(process.StandardOutput);
            var scrapeConfig = (YamlMappingNode) yaml.Documents[0].RootNode;
            LoadScrapersConfig(threads, scrapeConfig, readers, metrics);
        }

        private static HashSet<string> LoadSshScrapersConfig(IDictionary<string, Thread> threads,
            YamlMappingNode config,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var ids = new HashSet<string>();
            var defaultFile = config.String("file");
            var defaultUser = config.String("user");
            var defaultPassword = config.String("password");
            var defaultPkey = config.String("pkey");
            var defaultConnectTimeout = config.Int("connect-timeout");

            foreach (var (envName, yamlEnvConfig) in config.StringMap("environments"))
            {
                var envConfig = (YamlMappingNode) yamlEnvConfig;
                var hosts = envConfig.StringList("hosts").ToList();
                foreach (var host in hosts)
                {
                    var targetId = $"ssh://{host}/{envConfig.String("file") ?? defaultFile}";
                    ids.Add(targetId);
                    if (threads.ContainsKey(targetId))
                        continue;

                    var scraper = new SSHLogScraper(
                        envConfig.String("file") ?? defaultFile,
                        envName,
                        readers,
                        host,
                        envConfig.String("user") ?? defaultUser,
                        envConfig.String("password") ?? defaultPassword,
                        envConfig.String("pkey") ?? defaultPkey,
                        envConfig.Int("connect-timeout") ?? defaultConnectTimeout ?? 30,
                        metrics
                    );
                    var thread = new Thread(() => scraper.Run());
                    threads[targetId] = thread;
                    Startup.Scrapers[targetId] = scraper;
                    thread.IsBackground = true;
                    thread.Name = "scraper:" + targetId;
                    thread.Start();
                }
            }

            return ids;
        }

        private static void LoadReadersConfig(YamlMappingNode scrapeConfig, out IList<ColumnReader> readers)
        {
            readers = new List<ColumnReader>();
            var histogramBuckets = new Dictionary<string, double[]>();
            foreach (var (histogramName, yamlBuckets) in scrapeConfig.Map("global").StringMap("histograms"))
            {
                var buckets = (YamlSequenceNode) yamlBuckets;
                if (histogramBuckets.ContainsKey(histogramName))
                    throw new Exception($"Duplicate histogram definition of {histogramName}");

                if (buckets == null || buckets.Children.Count == 0)
                    histogramBuckets[histogramName] =
                        Histogram.DefaultBuckets;
                else
                    histogramBuckets[histogramName] =
                        buckets.Cast<YamlScalarNode>().Select(x => double.Parse(x.Value)).ToArray();
            }

            Startup.Metrics.Clear();
            foreach (var fmtEntry in scrapeConfig.Map("global").List("format"))
            {
                if (fmtEntry is YamlScalarNode scalar && scalar.Value == "~")
                {
                    readers.Add(null);
                    continue;
                }

                Debug.Assert(fmtEntry is YamlMappingNode);
                Debug.Assert(((YamlMappingNode) fmtEntry).Children.Count == 1);
                var name = ((YamlScalarNode) ((YamlMappingNode) fmtEntry).Children.Keys.First()).Value;
                var type = ((YamlMappingNode) fmtEntry).Children.Values.First() == null
                    ? null
                    : ((YamlScalarNode) ((YamlMappingNode) fmtEntry).Children.Values.First()).Value;

                if (type == null || type == "~")
                {
                    readers.Add(null);
                    continue;
                }

                if (type == "label" && name == "environment")
                    throw new Exception("'environment' is a reserved label name");

                if (type != "label" && (name == "parser_errors" || name == "lines_parsed" || name == "in_bytes"))
                    throw new Exception($"'{name}' is a reserved metric name");

                if (type.Contains('+'))
                {
                    var spl = type.Split('+');
                    type = spl[0].Trim();
                    if (type == "label")
                        throw new Exception("Labels cannot be used as histograms");
                    var histogramType = spl[1].Trim();
                    if (!histogramBuckets.ContainsKey(histogramType))
                        throw new Exception($"Histogram type {histogramType} is not defined");
                    Startup.Metrics[name] = new MetricBase(name, $"Histogram of {name}", MetricsType.Histogram,
                        histogramBuckets[histogramType]);
                }
                else if (type != "label" && type != "request_header")
                {
                    Startup.Metrics[name] = new MetricBase(name, $"Sum of {name}", MetricsType.Counter);
                }

                readers.Add(ColumnReaders.Create(type, name));
            }

            MetricBase.GlobalPrefix = scrapeConfig.Map("global").String("prefix");
            MetricBase.TTL = scrapeConfig.Map("global").Int("ttl") ?? 60;

            Startup.Metrics["parser_errors"] =
                new MetricBase("parser_errors", "Number of lines which could not be parsed", MetricsType.Counter);
            Startup.Metrics["lines_parsed"] =
                new MetricBase("lines_parsed", "Number of successfully parsed lines", MetricsType.Counter);
            Startup.Metrics["connected"] =
                new MetricBase("connected", "Whether this target is currently being scraped", MetricsType.Gauge, null,
                    true);
        }

        private static void LoadScrapersConfig(IDictionary<string, Thread> threads, YamlMappingNode scrapeConfig,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var loadedIds = LoadSshScrapersConfig(threads, scrapeConfig.Map("ssh") ?? new YamlMappingNode(), readers,
                metrics);
            foreach (var (threadId, thread) in threads)
                if (!loadedIds.Contains(threadId))
                    thread.Abort();
        }

        private static void InitLogging()
        {
            var config = new LoggingConfiguration();
            var console = new ColoredConsoleTarget("console");
            config.AddTarget(console);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, console);
            LogManager.Configuration = config;
        }

        private static void Main(string[] args)
        {
            InitLogging();
            ServicePointManager.DefaultConnectionLimit = 1;

            ThreadPool.GetMinThreads(out var a, out var b);
            Logger.Debug($"Current min threads: {a}, {b}");
            if (!ThreadPool.SetMinThreads(1024, 128))
                throw new Exception("Failed to set minimum thread count");

            ReadCoreConfig(out var readers, out var scrapeConfigScript, out var configReloadInterval,
                out var scrapeConfig);
            var threads = new Dictionary<string, Thread>();
            LoadScrapersConfig(threads, scrapeConfig, readers, Startup.Metrics);

            if (scrapeConfigScript != null)
            {
                var loaderThread = new Thread(() =>
                {
                    while (true)
                    {
                        LoadFromScript(threads, scrapeConfigScript, readers, Startup.Metrics);

                        if (configReloadInterval.HasValue)
                            Thread.Sleep(TimeSpan.FromSeconds(configReloadInterval.Value));
                        else
                            Thread.Sleep(-1);
                    }

                    // ReSharper disable once FunctionNeverReturns
                }) {Name = "inventory-loader-thread"};

                loaderThread.Start();
            }

            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseKestrel(options => { options.ListenAnyIP(5000); })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                })
                .UseNLog()
                .Build()
                .Run();
            // ReSharper disable once FunctionNeverReturns
        }
    }
}
