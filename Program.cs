global using System;
using System.IO;
using System.Text;
using System.Threading;
using Coflnet.Core;
using Coflnet.Security.OpenBao;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Coflnet.Sky.PlayerState
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BufferConsoleOutput();
            Console.WriteLine("uuid: " + new Guid("9b5f43a35815412f837f99944af4faf8"));
            // ILogger output is shipped to Loki via OTLP (see AddOpenTelemetryLogging), not stdout.
            // `kubectl logs` only shows this boot banner; query application logs in Loki, e.g.:
            //   {service_name="sky-player-state"} | detected_level="Error"
            Console.WriteLine("[logs] application logs go to Loki, not stdout. Query: {service_name=\"sky-player-state\"} (Grafana > Explore > Loki)");
            CreateHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// The default <see cref="Console.Out"/> flushes to stdout on every write, i.e. a
        /// syscall taken under a process-global lock. With hundreds of hot-path log lines
        /// per second across concurrently processed messages that lock serialises
        /// processing. Wrap stdout in a buffered writer flushed periodically (and on exit)
        /// so logging stops being a throughput bottleneck while still reaching the log
        /// collector within a fraction of a second.
        /// </summary>
        private static void BufferConsoleOutput()
        {
            var bufferedOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
            {
                AutoFlush = false
            };
            var syncedOut = TextWriter.Synchronized(bufferedOut);
            Console.SetOut(syncedOut);

            var flushInterval = TimeSpan.FromMilliseconds(500);
            var flushTimer = new Timer(_ =>
            {
                try { syncedOut.Flush(); } catch { /* stdout closed during shutdown */ }
            }, null, flushInterval, flushInterval);
            GC.KeepAlive(flushTimer);

            void Flush(object sender, EventArgs e)
            {
                try { syncedOut.Flush(); } catch { /* best effort on shutdown/crash */ }
            }
            AppDomain.CurrentDomain.ProcessExit += Flush;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Flush(s, e);
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) => config.AddOpenBaoFromEnvironment())
                .ConfigureLogging((context, logging) =>
                {
                    // Shared OTel logging configuration from Coflnet.Core.
                    // Bridges ILogger -> OTLP (HttpProtobuf) with trace-log correlation,
                    // k8s pod attributes, and DEV_LOGGING console fallback.
                    logging.AddOpenTelemetryLogging(
                        context.Configuration,
                        context.Configuration["JAEGER_SERVICE_NAME"] ?? "sky-player-state");
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
