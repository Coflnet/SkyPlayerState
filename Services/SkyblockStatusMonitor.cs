using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Polls the public Hypixel status page and exports whether SkyBlock is unavailable (maintenance or major outage).
/// While it is, no client can send state updates, so the consume rate drops without any fault here; alert rules on
/// the consume rate can use the gauge to stay quiet. A failed poll reports available so alerting is never muted blindly.
/// </summary>
public class SkyblockStatusMonitor : BackgroundService
{
    public const string DefaultStatusUrl = "https://status.hypixel.net/api/v2/summary.json";
    private const string ComponentName = "SkyBlock";
    private static readonly string[] UnavailableStatuses = { "under_maintenance", "major_outage" };

    private static readonly Prometheus.Gauge unavailableGauge = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_skyblock_unavailable",
        "1 while the Hypixel status page reports SkyBlock under maintenance or in a major outage, else 0");

    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for the status page.</summary>
    public const string HttpClientName = "skyblock-status";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<SkyblockStatusMonitor> logger;
    private readonly string statusUrl;
    private readonly TimeSpan interval;

    public SkyblockStatusMonitor(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<SkyblockStatusMonitor> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        statusUrl = config["SKYBLOCK_STATUS_URL"] ?? DefaultStatusUrl;
        interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("SKYBLOCK_STATUS_INTERVAL_SECONDS", 60)));
    }

    /// <summary>
    /// True when the SkyBlock component of a Statuspage summary is under maintenance or in a major outage.
    /// A summary without that component counts as available.
    /// </summary>
    public static bool IsUnavailable(string summaryJson)
    {
        var components = JObject.Parse(summaryJson)["components"] as JArray;
        var skyblock = components?.FirstOrDefault(c => (string?)c["name"] == ComponentName);
        return UnavailableStatuses.Contains((string?)skyblock?["status"]);
    }

    /// <summary>Fetches the status once and updates the gauge.</summary>
    public async Task PollOnce(CancellationToken ct)
    {
        var unavailable = false;
        try
        {
            unavailable = IsUnavailable(await httpClientFactory.CreateClient(HttpClientName).GetStringAsync(statusUrl, ct));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not read the SkyBlock status from {url}", statusUrl);
        }
        unavailableGauge.Set(unavailable ? 1 : 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnce(stoppingToken);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
