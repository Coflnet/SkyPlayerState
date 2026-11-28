using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Provides the current mayor name (lowercase), perks and minister for mayor gated task accessibility,
/// refreshed at most once per hour from the mayor service. Once a mayor is known, an outdated one is
/// served while the refresh runs in the background, so a slow mayor service never delays a caller.
/// The minister comes from the election periods (<c>/mayor/current</c> is declared as a candidate and its
/// client model drops the minister); failing to load it only leaves the minister unknown.
/// </summary>
public class CurrentMayorService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinisterTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How far back the election periods are read: more than one term (5 days 4 hours), so the current one is in.</summary>
    private static readonly TimeSpan MinisterLookback = TimeSpan.FromDays(7);
    private readonly Mayor.Client.Api.IMayorApi mayorApi;
    private readonly Mayor.Client.Api.IElectionPeriodsApi electionApi;
    private readonly ILogger<CurrentMayorService> logger;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private MayorSnapshot currentMayor;
    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;

    public CurrentMayorService(Mayor.Client.Api.IMayorApi mayorApi, ILogger<CurrentMayorService> logger, TimeProvider time = null,
        Mayor.Client.Api.IElectionPeriodsApi electionApi = null)
    {
        this.mayorApi = mayorApi;
        this.electionApi = electionApi;
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
            var (minister, loaded) = await LoadMinister(mayor?.Name);
            if (!loaded)
                minister = PreviousMinisterOf(mayor?.Name);
            currentMayor = ToSnapshot(mayor, minister) ?? currentMayor;
            // a failed minister lookup is retried soon, like a failed mayor lookup
            nextRefresh = time.GetUtcNow() + (loaded ? RefreshInterval : RetryAfterFailure);
        }
        catch (Exception e)
        {
            nextRefresh = time.GetUtcNow() + RetryAfterFailure;
            logger.LogError(e, "could not load current mayor, keeping {mayor}", currentMayor?.Name);
        }
    }

    /// <summary>
    /// The minister of <paramref name="mayorName"/>'s current term: the latest election period's, if its winner is that mayor.
    /// Loaded is false when the lookup failed (timeout, error); a minister may then still be known from before. Never throws.
    /// </summary>
    private async Task<(MinisterSnapshot Minister, bool Loaded)> LoadMinister(string mayorName)
    {
        if (electionApi == null || mayorName == null)
            return (null, true);
        try
        {
            var now = time.GetUtcNow();
            using var timeout = new CancellationTokenSource(MinisterTimeout);
            var periods = await electionApi.ElectionPeriodRangeGetAsync((now - MinisterLookback).ToUnixTimeMilliseconds(),
                now.ToUnixTimeMilliseconds(), 0, timeout.Token).WaitAsync(MinisterTimeout);
            return (ToMinister(periods?.Where(p => p?.Winner != null).MaxBy(p => p.Year), mayorName), true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not load the minister of {mayor}", mayorName);
            return (null, false);
        }
    }

    /// <summary>The minister known from the last load while the mayor stays the same, else null.</summary>
    private MinisterSnapshot PreviousMinisterOf(string mayorName)
        => mayorName != null && string.Equals(currentMayor?.Name, mayorName, StringComparison.OrdinalIgnoreCase) ? currentMayor.Minister : null;

    /// <summary>The period's minister if its winner is <paramref name="mayorName"/>; a minister without name or perk is unknown.</summary>
    private static MinisterSnapshot ToMinister(Mayor.Client.Model.CoflnetSkyMayorModelsModelElectionPeriod period, string mayorName)
    {
        var minister = period?.Winner?.Minister;
        if (minister?.Name == null || minister.Perk?.Name == null
            || !string.Equals(period.Winner.Name, mayorName, StringComparison.OrdinalIgnoreCase))
            return null;
        return new MinisterSnapshot(minister.Name.ToLowerInvariant(), new MayorPerk(minister.Perk.Name, minister.Perk.Description));
    }

    private static MayorSnapshot ToSnapshot(Mayor.Client.Model.CoflnetSkyMayorModelsModelCandidate mayor, MinisterSnapshot minister)
    {
        if (mayor?.Name == null)
            return null;
        var perks = mayor.Perks?.Select(p => new MayorPerk(p.Name, p.Description)).ToList();
        return new MayorSnapshot(mayor.Name.ToLowerInvariant(), perks, minister);
    }
}
