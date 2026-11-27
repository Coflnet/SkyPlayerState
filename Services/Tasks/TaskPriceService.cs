using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Merged clean price lookup (bazaar sell overlaid with auction clean prices)
/// for the estimate endpoint, cached so each request does not refetch.
/// </summary>
public class TaskPriceService
{
    private readonly ISniperApi sniperApi;
    private readonly IBazaarApi bazaarApi;
    private readonly ILogger<TaskPriceService> logger;
    private PriceSet current;
    private Task<PriceSet> refreshing;
    private readonly object gate = new();
    private bool forceStale;
    /// <summary>Fewest entries a source answer needs to count as real (the live lists hold ~2,000 bazaar products and far more clean tags).</summary>
    internal int MinSourceEntries { get; set; } = DefaultMinSourceEntries;
    internal const int DefaultMinSourceEntries = 100;
    // last good result per source, so one failing source does not drop its prices
    private List<ItemPrice> lastBazaar;
    private Dictionary<string, double> lastClean;
    private DateTime lastBazaarAt = DateTime.MinValue;
    private DateTime lastCleanAt = DateTime.MinValue;
    /// <summary>Test seam: runs on the built set before it is stored.</summary>
    internal Action<PriceSet> AfterBuild { get; set; }
    /// <summary>Last good data older than this counts as missing, like a tag missing from the sniper prices.</summary>
    internal static readonly TimeSpan MaxSourceAge = PriceHistory.MissingTagLifetime;
    private readonly PriceHistory cleanHistory = new();
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan DegradedCacheDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(2);

    private static readonly Prometheus.Gauge TagCountGauge = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_task_prices_tags", "Number of tags in the merged task price lookup");
    private static readonly Prometheus.Gauge HashGauge = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_task_prices_hash", "First 48 bits of the hash of the merged prices, equal on pods that value alike");
    private static readonly Prometheus.Gauge SourceSuccessGauge = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_task_prices_source_last_success_timestamp_seconds", "Unix time of the last successful fetch per price source", "source");
    private static readonly Prometheus.Gauge FetchedAtGauge = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_task_prices_fetched_timestamp_seconds", "Unix time of the older of the two sources' last successful fetch");

    private static readonly TimeSpan BazaarPricesCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BazaarPricesTimeout = TimeSpan.FromSeconds(2);
    private List<ItemPrice> cachedBazaarPrices;
    private DateTime bazaarPricesFetchedAt = DateTime.MinValue;
    private readonly SemaphoreSlim bazaarPricesRefreshLock = new(1, 1);

    public TaskPriceService(ISniperApi sniperApi, IBazaarApi bazaarApi, ILogger<TaskPriceService> logger)
    {
        this.sniperApi = sniperApi;
        this.bazaarApi = bazaarApi;
        this.logger = logger;
    }

    public async Task<Dictionary<string, double>> GetPrices(CancellationToken cancellationToken = default)
        => (await GetPriceSet(cancellationToken)).Prices;

    /// <summary>
    /// The merged prices with completeness and fingerprint. Stale-while-revalidate: once a set exists it is returned at
    /// once and an expired one is refreshed in the background (single flight); only callers without any set await the
    /// first refresh (bounded by the source timeouts), never an empty fallback. A caller's cancellation only stops its own wait.
    /// </summary>
    public async Task<PriceSet> GetPriceSet(CancellationToken cancellationToken = default)
    {
        Task<PriceSet> refresh;
        PriceSet existing;
        lock (gate)
        {
            existing = current;
            if (existing != null && !forceStale && IsFresh(existing))
                return existing;
            refresh = refreshing ??= Task.Run(RefreshAndStore);
        }
        // an incomplete set is not worth serving while a refresh that may complete it is about to finish
        if (existing is { IsComplete: true })
            return existing;
        var set = await refresh.WaitAsync(cancellationToken);
        System.Diagnostics.Activity.Current?.SetTag("prices.hash", set.Fingerprint.Hash);
        return set;
    }

    /// <summary>Marks the current set expired: the next call revalidates (and still returns the current set at once).</summary>
    internal void MarkStale()
    {
        lock (gate)
            forceStale = true;
    }

    internal void ExpireBazaarPricesForTest() => bazaarPricesFetchedAt = DateTime.MinValue;

    /// <summary>Drops the current set so the next call awaits a refetch (last good values per source are kept).</summary>
    internal void InvalidateCache()
    {
        lock (gate)
            current = null;
    }

    private static bool IsFresh(PriceSet set) => DateTime.UtcNow - set.CheckedAt < set.CacheTtl;

    private async Task<PriceSet> RefreshAndStore()
    {
        try
        {
            var set = await Refresh();
            lock (gate)
            {
                current = set;
                forceStale = false;
            }
            return set;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to refresh task prices, keeping the previous set");
            return KeepPrevious();
        }
        finally
        {
            lock (gate)
                refreshing = null;
        }
    }

    /// <summary>After a failed refresh: the previous set again (retried in 15 s), or an incomplete empty one.</summary>
    private PriceSet KeepPrevious()
    {
        lock (gate)
        {
            var previous = current ?? Build(null, DateTime.MinValue, null, DateTime.MinValue, true);
            current = new PriceSet(previous.Prices, previous.IsComplete, previous.FetchedAt, DateTime.UtcNow, DegradedCacheDuration, previous.Fingerprint);
            forceStale = false;
            return current;
        }
    }

    private async Task<PriceSet> Refresh()
    {
        using var span = TaskTelemetry.Source.StartActivity("task-prices-refresh");
        using var cts = new CancellationTokenSource(SourceTimeout);
        var bazaarTask = FetchSource("bazaar", () => bazaarApi.GetAllPricesAsync(0, cts.Token), cts.Token);
        var cleanTask = FetchSource("sniper clean", () => sniperApi.ApiSniperPricesCleanGetAsync(0, cts.Token), cts.Token);
        var bazaar = await bazaarTask;
        var clean = await cleanTask;
        var now = DateTime.UtcNow;
        var degraded = bazaar == null || clean == null;
        if (bazaar != null)
            RecordBazaar(bazaar.ToList(), now);
        if (clean != null)
            RecordClean(cleanHistory.Add(clean.ToDictionary(c => c.Key, c => (double)c.Value), now), now);
        var set = Build(lastBazaar, lastBazaarAt, lastClean, lastCleanAt, degraded);
        AfterBuild?.Invoke(set);
        Publish(set, span);
        return set;
    }

    private void RecordBazaar(List<ItemPrice> bazaar, DateTime now)
    {
        lastBazaar = bazaar;
        lastBazaarAt = now;
        SourceSuccessGauge.WithLabels("bazaar").Set(new DateTimeOffset(now).ToUnixTimeSeconds());
    }

    private void RecordClean(Dictionary<string, double> clean, DateTime now)
    {
        lastClean = clean;
        lastCleanAt = now;
        SourceSuccessGauge.WithLabels("clean").Set(new DateTimeOffset(now).ToUnixTimeSeconds());
    }

    /// <summary>A source whose last good data is older than <see cref="MaxSourceAge"/> counts as missing.</summary>
    internal static PriceSet Build(List<ItemPrice> bazaar, DateTime bazaarAt, Dictionary<string, double> clean, DateTime cleanAt, bool degraded)
    {
        var now = DateTime.UtcNow;
        if (now - bazaarAt > MaxSourceAge)
            bazaar = null;
        if (now - cleanAt > MaxSourceAge)
            clean = null;
        var prices = PriceMerge.Merge(bazaar, clean, CopperValue.Current);
        var complete = bazaar != null && clean != null;
        var fetchedAt = OldestSuccess(bazaar != null ? bazaarAt : null, clean != null ? cleanAt : null) ?? now;
        return new PriceSet(prices, complete, fetchedAt, now, degraded || !complete ? DegradedCacheDuration : CacheDuration,
            PriceFingerprint.Of(prices, fetchedAt));
    }

    private static DateTime? OldestSuccess(DateTime? first, DateTime? second)
        => first == null ? second : second == null ? first : first < second ? first : second;

    /// <summary>
    /// Fetches one source. An answer with fewer than <see cref="MinSourceEntries"/> entries counts as a failure:
    /// SkyBazaar answers 200 with [] before its first pull after a restart, which would replace the last good prices.
    /// </summary>
    private async Task<T> FetchSource<T>(string name, Func<Task<T>> fetch, CancellationToken token) where T : class, System.Collections.ICollection
    {
        try
        {
            var result = await fetch().WaitAsync(token);
            if (result != null && result.Count >= MinSourceEntries)
                return result;
            logger.LogWarning("{source} returned {count} entries (minimum {min}), using last good", name, result?.Count ?? 0, MinSourceEntries);
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "{source} prices unavailable for task estimates, using last good", name);
            return null;
        }
    }

    private static void Publish(PriceSet set, System.Diagnostics.Activity span)
    {
        span?.SetTag("prices.tags", set.Fingerprint.TagCount);
        span?.SetTag("prices.hash", set.Fingerprint.Hash);
        span?.SetTag("prices.complete", set.IsComplete);
        TagCountGauge.Set(set.Fingerprint.TagCount);
        HashGauge.Set(set.Fingerprint.HashAsNumber);
        FetchedAtGauge.Set(new DateTimeOffset(set.FetchedAt).ToUnixTimeSeconds());
    }

    /// <summary>
    /// Raw bazaar prices (buy and sell, per product) for <see cref="Tasks.TaskParams.BazaarPrices"/> -
    /// used by the bazaar crafting tasks (see CraftingTasks.cs) that need both sides of the spread
    /// rather than the single merged clean price <see cref="GetPrices"/> returns. Cached the same way
    /// as <see cref="TaskExecutionService.GetNames"/>: a short TTL, and on a timeout/failure the last
    /// good value is served (falling back to an empty list only when nothing has ever been fetched).
    /// </summary>
    public async Task<List<ItemPrice>> GetBazaarPrices(CancellationToken cancellationToken = default)
    {
        if (cachedBazaarPrices != null && DateTime.UtcNow - bazaarPricesFetchedAt < BazaarPricesCacheDuration)
            return cachedBazaarPrices;
        if (!await bazaarPricesRefreshLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            return cachedBazaarPrices ?? new List<ItemPrice>();
        try
        {
            if (cachedBazaarPrices != null && DateTime.UtcNow - bazaarPricesFetchedAt < BazaarPricesCacheDuration)
                return cachedBazaarPrices;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(BazaarPricesTimeout);
            var prices = await bazaarApi.GetAllPricesAsync(0, cts.Token);
            if (prices == null || prices.Count < MinSourceEntries)
                throw new InvalidOperationException($"bazaar answered {prices?.Count ?? 0} entries, minimum {MinSourceEntries}");
            cachedBazaarPrices = prices.ToList();
            bazaarPricesFetchedAt = DateTime.UtcNow;
            return cachedBazaarPrices;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            bazaarPricesFetchedAt = DateTime.UtcNow - BazaarPricesCacheDuration + TimeSpan.FromSeconds(30);
            logger.LogWarning(e, "bazaar prices unavailable for task results, serving {count} stale", cachedBazaarPrices?.Count ?? 0);
            return cachedBazaarPrices ?? new List<ItemPrice>();
        }
        finally
        {
            bazaarPricesRefreshLock.Release();
        }
    }
}
