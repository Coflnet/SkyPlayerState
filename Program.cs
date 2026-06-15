global using System;
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
            Console.WriteLine("uuid: " + new Guid("9b5f43a35815412f837f99944af4faf8"));
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) => config.AddOpenBaoFromEnvironment())
                .ConfigureLogging((context, logging) =>
                {
                    // Shared OTel logging configuration from Coflnet.Sky.Core.
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
