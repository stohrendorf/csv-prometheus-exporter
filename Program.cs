using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using csv_prometheus_exporter.MetricsImpl;
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
        public static readonly IDictionary<string, SSHLogScraper> Scrapers =
            new ConcurrentDictionary<string, SSHLogScraper>();

        // This method gets called by the runtime. Use this method
        // to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
        }

        private static Task Collect(HttpContext context)
        {
            context.Response.Headers["Content-type"] = "text/plain; version=0.0.4; charset=utf-8";

            var aggregation = Task.Run(() =>
            {
                var aggregated = new Dictionary<string, MetricsMeta>();

                foreach (var scraper in Scrapers.Values)
                foreach (var (metricName, metricData) in scraper.Metrics)
                    if (!aggregated.TryGetValue(metricName, out var existing))
                        aggregated[metricName] = metricData.FullClone();
                    else
                        existing.Merge(metricData);

                return aggregated;
            });

            return aggregation.ContinueWith(
                _ =>
                {
                    using (var textStream = new StreamWriter(context.Response.Body, Encoding.UTF8, 8 << 20))
                    {
                        int total = 0, discarded = 0;
                        foreach (var aggregatedMetric in _.Result.Values)
                            aggregatedMetric.ExposeTo(textStream, ref total, ref discarded);
                        Console.WriteLine("Result: total {0}, discarded {1}", total, discarded);
                    }
                }
            );
        }

        // This method gets called by the runtime. Use this method
        // to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            var routeBuilder = new RouteBuilder(app);

            routeBuilder.MapGet("metrics", Collect);

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
                    Startup.Scrapers[targetId] = scraper;
                    thread.IsBackground = true;
                    thread.Name = "scraper:" + targetId;
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
                    throw new Exception("'environment' is a reserved label name");

                if (tp != "label" && (name == "parser_errors" || name == "lines_parsed" || name == "in_bytes"))
                    throw new Exception($"'{name}' is a reserved metric name");

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
                else if (tp != "label" && tp != "request_header")
                {
                    metrics[name] = new MetricsMeta(name, $"Sum of {name}", Type.Counter);
                }

                switch (tp)
                {
                    case "number":
                        readers.Add(ValueParsers.NumberReader(name));
                        break;
                    case "clf_number":
                        readers.Add(ValueParsers.ClfNumberReader(name));
                        break;
                    case "request_header":
                        readers.Add(ValueParsers.RequestHeaderReader());
                        break;
                    case "label":
                        readers.Add(ValueParsers.LabelReader(name));
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
                if (!loadedIds.Contains(threadId))
                    thread.Abort();
        }

        private static void Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = 5;

            ThreadPool.GetMinThreads(out var a, out var b);
            Console.WriteLine("Current min threads: {0}, {1}", a, b);
            if (!ThreadPool.SetMinThreads(1024, 128))
                throw new Exception("Failed to set minimum thread count");

            ReadCoreConfig(out var readers, out var scrapeConfigScript, out var configReloadInterval,
                out var scrapeConfig, out var metrics);
            var threads = new Dictionary<string, Thread>();
            LoadScrapersConfig(threads, scrapeConfig, readers, metrics);

            if (scrapeConfigScript != null)
            {
                var loaderThread = new Thread(() =>
                {
                    while (true)
                    {
                        LoadFromScript(threads, scrapeConfigScript, readers, metrics);

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
                .Build()
                .Run();
            // ReSharper disable once FunctionNeverReturns
        }
    }
}