using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Goes through the app's own registrations (Startup) and the Prometheus export only: while the status page
/// reports SkyBlock in maintenance no updates can arrive, and consume-rate alerts need a gauge to tell that apart.
/// </summary>
public class SkyblockStatusMonitorTests
{
    private const string StatusUrl = "http://status.synthetic/summary.json";
    private const string Metric = "sky_playerstate_skyblock_unavailable";

    private static string Summary(string skyblockStatus) => $$"""
        {"components":[
          {"name":"Minecraft Server Authentication","status":"operational"},
          {"name":"SkyBlock","status":"{{skyblockStatus}}"}
        ]}
        """;

    [Test]
    public async Task RegisteredStatusServiceExportsMaintenanceAndFailsOpen()
    {
        var handler = new StubHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SKYBLOCK_STATUS_URL"] = StatusUrl,
            ["SKYBLOCK_STATUS_INTERVAL_SECONDS"] = "1",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://127.0.0.1:4318"
        }).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(config);
        new Startup(config).ConfigureServices(collection);
        collection.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = handler));
        using var services = collection.BuildServiceProvider();

        var statusType = collection.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType).FirstOrDefault(t => t?.Name == "SkyblockStatusMonitor");
        Assert.That(statusType, Is.Not.Null, "Startup registers no hosted service exporting the SkyBlock status");
        var monitor = (IHostedService)ActivatorUtilities.CreateInstance(services, statusType!);

        handler.Respond(HttpStatusCode.OK, Summary("under_maintenance"));
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await ExpectGauge(1);
            handler.Respond(HttpStatusCode.ServiceUnavailable, "");
            await ExpectGauge(0);
            handler.Respond(HttpStatusCode.OK, Summary("major_outage"));
            await ExpectGauge(1);
            handler.Respond(HttpStatusCode.OK, Summary("operational"));
            await ExpectGauge(0);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
        Assert.That(handler.RequestedUris, Is.All.EqualTo(StatusUrl));
    }

    private static async Task ExpectGauge(int expected)
    {
        var pattern = new Regex($"^{Metric} {expected}$", RegexOptions.Multiline);
        var text = "";
        for (var i = 0; i < 100; i++)
        {
            text = await ExportMetrics();
            if (pattern.IsMatch(text))
                return;
            await Task.Delay(100);
        }
        Assert.Fail($"{Metric} never became {expected}; exported: {text.Split('\n').FirstOrDefault(l => l.StartsWith(Metric + " "))}");
    }

    private static async Task<string> ExportMetrics()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private class StubHandler : HttpMessageHandler
    {
        private record Reply(HttpStatusCode Code, string Body);
        private volatile Reply response = new(HttpStatusCode.OK, "");
        public readonly List<string?> RequestedUris = new();

        public void Respond(HttpStatusCode code, string body) => response = new(code, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestedUris)
                RequestedUris.Add(request.RequestUri?.ToString());
            var (code, body) = response;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
}
