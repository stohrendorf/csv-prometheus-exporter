using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Prometheus;
using YamlDotNet.RepresentationModel;

namespace csv_prometheus_exporter
{
    internal static class Program
    {
        private static void ReadCoreConfig(out IList<Reader> readers, out string scrapeConfigScript,
            out int? configReloadInterval, out YamlMappingNode scrapeConfig,
            out IDictionary<string, double[]> histograms)
        {
            var scrapeConfigFilename = Environment.GetEnvironmentVariable("SCRAPECONFIG") ?? "/etc/scrapeconfig.yml";
            var yaml = new YamlStream();
            yaml.Load(new StreamReader(scrapeConfigFilename));
            scrapeConfig = (YamlMappingNode) yaml.Documents[0].RootNode;

            LoadReadersConfig(scrapeConfig, out readers, out histograms);
            configReloadInterval = scrapeConfig.Int("reload-interval");

            scrapeConfigScript = scrapeConfig.String("script");
        }

        private static void LoadFromScript(IDictionary<string, Thread> threads, string scrapeConfigScript,
            IList<Reader> readers, IDictionary<string, double[]> histograms)
        {
            var yaml = new YamlStream();
            var split = scrapeConfigScript.Split(' ');
            var startInfo = new ProcessStartInfo
            {
                FileName = split[0],
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow=true
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
            LoadScrapersConfig(threads, scrapeConfig, readers, histograms);
        }

        private static HashSet<string> LoadSshScrapersConfig(IDictionary<string, Thread> threads, YamlMappingNode config,
            IList<Reader> readers, IDictionary<string, double[]> histograms)
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
                        (envConfig.Int("connect-timeout") ?? defaultConnectTimeout).Value,
                        histograms
                    );
                    var thread = new Thread(() => scraper.Run());
                    threads[targetId] = thread;
                    thread.Start();
                }
            }

            return ids;
        }

        private static void LoadReadersConfig(YamlMappingNode scrapeConfig, out IList<Reader> readers,
            out IDictionary<string, double[]> histograms)
        {
            readers = new List<Reader>();
            var histogramTypes = new Dictionary<string, double[]>();
            foreach (var (histogramName, yamlBuckets) in scrapeConfig.Map("global").StringMap("histograms"))
            {
                var buckets = (YamlSequenceNode) yamlBuckets;
                if (histogramTypes.ContainsKey(histogramName))
                    throw new Exception($"Duplicate histogram definition of {histogramName}");

                if (buckets == null || buckets.Children.Count == 0)
                    histogramTypes[histogramName] = null;
                else
                    histogramTypes[histogramName] =
                        buckets.Cast<YamlScalarNode>().Select(x => double.Parse(x.Value)).ToArray();
            }

            histograms = new Dictionary<string, double[]>();
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
                    var histogramType = spl[1].Trim();
                    if (!histogramTypes.ContainsKey(histogramType))
                        throw new Exception($"Histogram type {histogramType} is not defined");
                    histograms[name] = histogramTypes[histogramType];
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

            MetricsUtil.Prefix = scrapeConfig.Map("global").String("prefix");
        }

        private static void LoadScrapersConfig(IDictionary<string, Thread> threads, YamlMappingNode scrapeConfig,
            IList<Reader> readers, IDictionary<string, double[]> histograms)
        {
            var loadedIds = LoadSshScrapersConfig(threads, scrapeConfig.Map("ssh") ?? new YamlMappingNode(), readers,
                histograms);
            foreach (var (threadId, thread) in threads)
            {
                if (!loadedIds.Contains(threadId))
                {
                    thread.Abort();
                }
            }
        }

        private static void Main()
        {
            ReadCoreConfig(out var readers, out var scrapeConfigScript, out var configReloadInterval,
                out var scrapeConfig, out var histograms);
            var threads = new Dictionary<string, Thread>();
            LoadScrapersConfig(threads, scrapeConfig, readers, histograms);

            var metricServer = new KestrelMetricServer(5000);
            metricServer.Start();

            while (true)
            {
                if (scrapeConfigScript != null)
                {
                    LoadFromScript(threads, scrapeConfigScript, readers, histograms);
                }

                Thread.Sleep(configReloadInterval.HasValue
                    ? TimeSpan.FromSeconds(configReloadInterval.Value)
                    : TimeSpan.FromDays(365));
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }
}
