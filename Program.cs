using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace csv_prometheus_exporter
{
    public class Startup
    {
        // This method gets called by the runtime. Use this method
        // to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
        }

        public static readonly Dictionary<string, SSHLogScraper> Scrapers = new Dictionary<string, SSHLogScraper>();

        // This method gets called by the runtime. Use this method
        // to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            var routeBuilder = new RouteBuilder(app);

            routeBuilder.MapGet("metrics", context =>
            {
                var aggregated = new Dictionary<string, MetricsMeta>();

                lock (Scrapers)
                {
                    foreach (var scraper in Scrapers.Values)
                    {
                        Dictionary<string, MetricsMeta> m;
                        lock (scraper.Metrics)
                        {
                            m = new Dictionary<string, MetricsMeta>(scraper.Metrics);
                        }

                        foreach (var (k, v) in m)
                        {
                            if (!aggregated.TryGetValue(k, out var existing))
                            {
                                aggregated[k] = v.TTLClone();
                            }
                            else
                            {
                                existing.AddAll(v.TTLClone());
                            }
                        }
                    }
                }

                context.Response.Headers["Content-type"] = "text/plain; version=0.0.4; charset=utf-8";

                var result = new StringBuilder {Capacity = 100 << 20}; // 100 MB
                foreach (var aggregatedMetric in aggregated.Values)
                {
                    result.Append(aggregatedMetric.Header).Append("\n");
                    foreach (var metric in aggregatedMetric.GetAllMetrics())
                    {
                        result.Append(metric.Expose()).Append("\n");
                    }
                }

                return context.Response.WriteAsync(result.ToString());
            });

            app.UseRouter(routeBuilder.Build());
        }
    }

    internal static class Program
    {
        private static void ReadCoreConfig(out IList<Reader> readers, out string scrapeConfigScript,
            out int? configReloadInterval, out YamlMappingNode scrapeConfig,
            out IDictionary<string, MetricsMeta> metrics)
        {
            var scrapeConfigFilename = Environment.GetEnvironmentVariable("SCRAPECONFIG") ?? "/etc/scrapeconfig.yml";
            var yaml = new YamlStream();
            yaml.Load(new StreamReader(scrapeConfigFilename));
            scrapeConfig = (YamlMappingNode) yaml.Documents[0].RootNode;

            LoadReadersConfig(scrapeConfig, out readers, out metrics);
            configReloadInterval = scrapeConfig.Int("reload-interval");

            scrapeConfigScript = scrapeConfig.String("script");
        }

        private static void LoadFromScript(IDictionary<string, Thread> threads, string scrapeConfigScript,
            IList<Reader> readers, IDictionary<string, MetricsMeta> metrics)
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
            foreach (var arg in split.Skip(1))
            {
                startInfo.ArgumentList.Add(arg);
            }

            var process = new Process {StartInfo = startInfo};
            process.Start();
            process.WaitForExit();
            yaml.Load(process.StandardOutput);
            var scrapeConfig = (YamlMappingNode) yaml.Documents[0].RootNode;
            LoadScrapersConfig(threads, scrapeConfig, readers, metrics);
        }

        private static HashSet<string> LoadSshScrapersConfig(IDictionary<string, Thread> threads,
            YamlMappingNode config,
            IList<Reader> readers, IDictionary<string, MetricsMeta> metrics)
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
                    lock (Startup.Scrapers)
                        Startup.Scrapers[targetId] = scraper;
                    thread.IsBackground = true;
                    thread.Start();
                }
            }

            return ids;
        }

        private static void LoadReadersConfig(YamlMappingNode scrapeConfig, out IList<Reader> readers,
            out IDictionary<string, MetricsMeta> metrics)
        {
            readers = new List<Reader>();
            var histogramBuckets = new Dictionary<string, double[]>();
            foreach (var (histogramName, yamlBuckets) in scrapeConfig.Map("global").StringMap("histograms"))
            {
                var buckets = (YamlSequenceNode) yamlBuckets;
                if (histogramBuckets.ContainsKey(histogramName))
                    throw new Exception($"Duplicate histogram definition of {histogramName}");

                if (buckets == null || buckets.Children.Count == 0)
                    histogramBuckets[histogramName] =
                        LocalHistogram.DefaultBuckets;
                else
                    histogramBuckets[histogramName] =
                        buckets.Cast<YamlScalarNode>().Select(x => double.Parse(x.Value)).ToArray();
            }

            metrics = new Dictionary<string, MetricsMeta>();
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
                var tp = ((YamlMappingNode) fmtEntry).Children.Values.First() == null
                    ? null
                    : ((YamlScalarNode) ((YamlMappingNode) fmtEntry).Children.Values.First()).Value;

                if (tp == null)
                {
                    readers.Add(null);
                    continue;
                }

                if (tp == "label" && name == "environment")
                {
                    throw new Exception("'environment' is a reserved label name");
                }

                if (tp != "label" && (name == "parser_errors" || name == "lines_parsed" || name == "in_bytes"))
                {
                    throw new Exception($"'{name}' is a reserved metric name");
                }

                if (tp.Contains('+'))
                {
                    var spl = tp.Split('+');
                    tp = spl[0].Trim();
                    if (tp == "label")
                        throw new Exception("Labels cannot be used as histograms");
                    var histogramType = spl[1].Trim();
                    if (!histogramBuckets.ContainsKey(histogramType))
                        throw new Exception($"Histogram type {histogramType} is not defined");
                    metrics[name] = new MetricsMeta(name, $"Histogram of {name}", Type.Histogram,
                        histogramBuckets[histogramType]);
                }
                else if (tp != "label")
                {
                    metrics[name] = new MetricsMeta(name, $"Sum of {name}", Type.Counter);
                }

                switch (tp)
                {
                    case "number":
                        readers.Add(Parser.NumberReader(name));
                        break;
                    case "clf_number":
                        readers.Add(Parser.ClfNumberReader(name));
                        break;
                    case "request_header":
                        readers.Add(Parser.RequestHeaderReader());
                        break;
                    case "label":
                        readers.Add(Parser.LabelReader(name));
                        break;
                }
            }

            MetricsMeta.GlobalPrefix = scrapeConfig.Map("global").String("prefix");
            MetricsMeta.TTL = scrapeConfig.Map("global").Int("ttl") ?? 60;
        }

        private static void LoadScrapersConfig(IDictionary<string, Thread> threads, YamlMappingNode scrapeConfig,
            IList<Reader> readers, IDictionary<string, MetricsMeta> metrics)
        {
            var loadedIds = LoadSshScrapersConfig(threads, scrapeConfig.Map("ssh") ?? new YamlMappingNode(), readers,
                metrics);
            foreach (var (threadId, thread) in threads)
            {
                if (!loadedIds.Contains(threadId))
                {
                    thread.Abort();
                }
            }
        }

        private static void Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = 5;

            ThreadPool.GetMaxThreads(out _, out var completionThreads);
            ThreadPool.SetMinThreads(512, completionThreads);

            ReadCoreConfig(out var readers, out var scrapeConfigScript, out var configReloadInterval,
                out var scrapeConfig, out var metrics);
            var threads = new Dictionary<string, Thread>();
            LoadScrapersConfig(threads, scrapeConfig, readers, metrics);

            var loaderThread = new Thread(() =>
            {
                while (true)
                {
                    if (scrapeConfigScript != null)
                    {
                        LoadFromScript(threads, scrapeConfigScript, readers, metrics);
                    }

                    Thread.Sleep(configReloadInterval.HasValue
                        ? TimeSpan.FromSeconds(configReloadInterval.Value)
                        : TimeSpan.FromDays(365));
                }

                // ReSharper disable once FunctionNeverReturns
            });

            loaderThread.Start();

            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseKestrel(options => { options.ListenAnyIP(5000); })
                .Build()
                .Run();
            // ReSharper disable once FunctionNeverReturns
        }
    }
}