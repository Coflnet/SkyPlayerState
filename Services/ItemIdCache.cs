using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Process-local cache of item identities to item ids, shared between the per-message
/// <see cref="ItemIdAssignUpdate"/> and the per-batch prefetch.
/// </summary>
public class ItemIdCache
{
    private const int ItemIdCacheLimit = 50_000;
    /// <summary>
    /// FindOrCreate fires one query per item concurrently, keep a single call bounded.
    /// </summary>
    internal const int PrefetchChunkSize = 200;
    /// <summary>
    /// Chunks run one after another, a batch prefetch must not burst Scylla more than the per-message path did.
    /// </summary>
    private const int PrefetchParallelism = 1;
    private readonly ConcurrentDictionary<Item, long> itemIdCache = new(new ItemCompare());
    private readonly ConcurrentQueue<Item> itemIdCacheInsertionOrder = new();
    private static readonly Prometheus.Counter itemIdCacheHitCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_itemid_cache_hit_total",
        "Total number of item IDs resolved from the process-local cache.");
    private static readonly Prometheus.Gauge itemIdCacheSize = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_itemid_cache_size",
        "Number of item identities held in the process-local cache.");
    private static readonly Prometheus.Counter itemIdPrefetchCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_itemid_prefetch_total",
        "Total number of item identities resolved by the per-batch prefetch.");
    private static readonly Prometheus.Counter itemIdPrefetchFailedCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_itemid_prefetch_failed_total",
        "Total number of item-id prefetch calls that failed and fell back to per-message lookups.");
    private static readonly Prometheus.Histogram itemIdPrefetchDuration = Prometheus.Metrics.CreateHistogram(
        "sky_playerstate_itemid_prefetch_seconds",
        "Duration of the per-batch item-id prefetch.",
        new Prometheus.HistogramConfiguration { Buckets = Prometheus.Histogram.ExponentialBuckets(0.005, 2, 12) });

    public int Count => itemIdCache.Count;

    public bool TryAssignCachedId(Item item)
    {
        if (!itemIdCache.TryGetValue(item, out var id))
            return false;
        item.Id = id;
        itemIdCacheHitCount.Inc();
        return true;
    }

    public bool Contains(Item item) => itemIdCache.ContainsKey(item);

    public void Cache(IEnumerable<Item> items)
    {
        foreach (var item in items.Where(item => item.Id != null))
        {
            // Display data is not part of ItemCompare identity; avoid retaining large lore strings.
            var cacheKey = new Item(item) { ItemName = null, Description = null, Count = null };
            if (itemIdCache.TryAdd(cacheKey, item.Id!.Value))
                itemIdCacheInsertionOrder.Enqueue(cacheKey);
        }
        while (itemIdCache.Count > ItemIdCacheLimit && itemIdCacheInsertionOrder.TryDequeue(out var oldest))
            itemIdCache.TryRemove(oldest, out _);
        itemIdCacheSize.Set(itemIdCache.Count);
    }

    /// <summary>
    /// Collects the distinct chest items of a whole batch that would need a database lookup and
    /// resolves them in few <see cref="IItemsService.FindOrCreate"/> calls, so the serial per-player
    /// processing afterwards only hits the cache. Never throws: on failure the per-message path
    /// resolves the ids as before.
    /// </summary>
    /// <param name="updates">the batch</param>
    /// <param name="service">used to resolve ids</param>
    /// <param name="getLoadedState">returns the already loaded state of a state key or null; items present
    /// with an id in its RecentViews never hit the database in the per-message path and are skipped</param>
    /// <param name="logger"></param>
    public async Task Prefetch(IEnumerable<UpdateMessage> updates, IItemsService service,
        Func<string, StateObject?> getLoadedState, ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var toFetch = CollectUncached(updates, getLoadedState);
            if (toFetch.Count == 0)
                return;
            using var limiter = new SemaphoreSlim(PrefetchParallelism);
            var resolved = 0;
            await Task.WhenAll(toFetch.Chunk(PrefetchChunkSize).Select(async chunk =>
            {
                await limiter.WaitAsync();
                try
                {
                    var found = await service.FindOrCreate(chunk);
                    Cache(found);
                    Interlocked.Add(ref resolved, found.Count(i => i.Id != null));
                }
                finally
                {
                    limiter.Release();
                }
            }));
            itemIdPrefetchCount.Inc(resolved);
        }
        catch (Exception e)
        {
            itemIdPrefetchFailedCount.Inc();
            logger.LogWarning(e, "Item id prefetch failed, falling back to per message lookups");
        }
        finally
        {
            itemIdPrefetchDuration.Observe(sw.Elapsed.TotalSeconds);
        }
    }

    private List<Item> CollectUncached(IEnumerable<UpdateMessage> updates, Func<string, StateObject?> getLoadedState)
    {
        var comparer = new ItemCompare();
        var result = new HashSet<Item>(comparer);
        var inRecentViews = new Dictionary<string, HashSet<Item>?>();
        foreach (var msg in updates)
        {
            if (!msg.Kind.HasFlag(UpdateMessage.UpdateKind.INVENTORY) || msg.Chest?.Items == null)
                continue;
            var candidates = ItemIdAssignUpdate.ItemsToLookUp(msg.Chest).Where(i => !Contains(i)).ToList();
            if (candidates.Count == 0)
                continue;
            var key = PlayerStateBackgroundService.StateKey(msg);
            if (!inRecentViews.TryGetValue(key, out var local))
            {
                var state = getLoadedState(key);
                local = state?.RecentViews == null ? null : state.RecentViews.ToList()
                    .SelectMany(v => v.Items).Where(i => i.Id != null).ToHashSet(comparer);
                inRecentViews[key] = local;
            }
            foreach (var item in candidates)
                if (local == null || !local.Contains(item))
                    result.Add(item);
        }
        return result.ToList();
    }
}
