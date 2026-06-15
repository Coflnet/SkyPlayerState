using Coflnet.Security.OpenBao;
global using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;

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
                    logging.AddOpenTelemetry(options =>
                    {
                        options.IncludeFormattedMessage = true;
                        options.IncludeScopes = true;
                        var endpoint = context.Configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
                        if (!string.IsNullOrEmpty(endpoint))
                            options.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
                        else
                            options.AddOtlpExporter();
                    });
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
