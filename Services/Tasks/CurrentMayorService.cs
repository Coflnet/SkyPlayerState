using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Provides the current mayor name (lowercase) and perks for mayor gated task accessibility,
/// refreshed at most once per hour from the mayor service. Once a mayor is known, an outdated one is
/// served while the refresh runs in the background, so a slow mayor service never delays a caller.
/// </summary>
public class CurrentMayorService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private readonly Mayor.Client.Api.IMayorApi mayorApi;
    private readonly ILogger<CurrentMayorService> logger;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private MayorSnapshot currentMayor;
    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;

    public CurrentMayorService(Mayor.Client.Api.IMayorApi mayorApi, ILogger<CurrentMayorService> logger, TimeProvider time = null)
    {
        this.mayorApi = mayorApi;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Current mayor (lowercase name) with their perks, or null if it could not be loaded yet.
    /// </summary>
    public async Task<MayorSnapshot> GetCurrentMayorSnapshot()
    {
        if (!IsRefreshDue())
            return currentMayor;
        if (currentMayor != null)
        {
            // stale while revalidate: the refresh may hang until the api client times out
            _ = RefreshUnlessRunning();
            return currentMayor;
        }
        await refreshLock.WaitAsync();
        try
        {
            if (IsRefreshDue())
                await Refresh();
        }
        finally
        {
            refreshLock.Release();
        }
        return currentMayor;
    }

    private bool IsRefreshDue() => time.GetUtcNow() >= nextRefresh;

    private async Task RefreshUnlessRunning()
    {
        if (!await refreshLock.WaitAsync(0))
            return;
        try
        {
            if (IsRefreshDue())
                await Refresh();
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>Loads the mayor; on failure keeps the known one and retries in 5 minutes instead of hammering a failing service.</summary>
    private async Task Refresh()
    {
        try
        {
            var mayor = await mayorApi.MayorCurrentGetAsync();
            currentMayor = ToSnapshot(mayor) ?? currentMayor;
            nextRefresh = time.GetUtcNow() + RefreshInterval;
        }
        catch (Exception e)
        {
            nextRefresh = time.GetUtcNow() + RetryAfterFailure;
            logger.LogError(e, "could not load current mayor, keeping {mayor}", currentMayor?.Name);
        }
    }

    private static MayorSnapshot ToSnapshot(Mayor.Client.Model.CoflnetSkyMayorModelsModelCandidate mayor)
    {
        if (mayor?.Name == null)
            return null;
        var perks = mayor.Perks?.Select(p => new MayorPerk(p.Name, p.Description)).ToList();
        return new MayorSnapshot(mayor.Name.ToLowerInvariant(), perks);
    }
}
