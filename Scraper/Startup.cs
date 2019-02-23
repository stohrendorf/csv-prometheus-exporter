using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using csv_prometheus_exporter.Parser;
using csv_prometheus_exporter.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace csv_prometheus_exporter.Scraper
{
    public class Startup
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        public static readonly IDictionary<string, SSHLogScraper> Scrapers =
            new ConcurrentDictionary<string, SSHLogScraper>();

        public static readonly IDictionary<string, MetricBase> Metrics = new Dictionary<string, MetricBase>();

        // This method gets called by the runtime. Use this method
        // to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
        }

        private static Task Collect(HttpContext context)
        {
            return Task.Run(() => ExposeData(context));
        }

        private static void ExposeData(HttpContext context)
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            // write the results
            context.Response.Headers["Content-type"] = "text/plain; version=0.0.4; charset=utf-8";
            using (var textStream = new StreamWriter(context.Response.Body))
            {
                var totalExposed = 0;
                foreach (var aggregatedMetric in Metrics.Values)
                    totalExposed += aggregatedMetric.ExposeTo(textStream);

                var process = Process.GetCurrentProcess();

                var meta = new MetricBase("process_cpu_seconds", "Process CPU seconds", MetricsType.Counter);
                var metric = meta.WithLabels(new LabelDict(Environment.MachineName));
                metric.Add(process.TotalProcessorTime.TotalSeconds);
                meta.ExposeTo(textStream);

                meta = new MetricBase("process_resident_memory_bytes", "Process RSS", MetricsType.Gauge);
                metric = meta.WithLabels(new LabelDict(Environment.MachineName));
                metric.Add(process.WorkingSet64);
                meta.ExposeTo(textStream);

                meta = new MetricBase("process_start_time_seconds", "Process Start Time (Unix epoch)",
                    MetricsType.Counter);
                metric = meta.WithLabels(new LabelDict(Environment.MachineName));
                metric.Add(((DateTimeOffset) process.StartTime).ToUnixTimeSeconds());
                meta.ExposeTo(textStream);

                meta = new MetricBase("scraper_active_metrics", "Currently exposed (active) metrics",
                    MetricsType.Gauge);
                metric = meta.WithLabels(new LabelDict(Environment.MachineName));
                metric.Add(totalExposed);
                metric.ExposeTo(textStream);
            }

            stopWatch.Stop();
            logger.Info($"Write active metrics to response stream: {stopWatch.Elapsed}");
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
}