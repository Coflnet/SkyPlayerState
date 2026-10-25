using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar.History;

/// <summary>
/// Fire-and-forget sink for the cross-player history tables. Implementations must never throw into the caller.
/// </summary>
public interface IBazaarOrderHistoryWriter
{
    bool Enabled { get; }
    void EnqueueOrder(BazaarOrderLifecycleEvent lifecycleEvent, IEnumerable<Fill>? fills);
    void EnqueueFlip(CompletedBazaarFlip flip);
    /// <summary>Writes the orders of a just loaded player state (opt-in via <c>BAZAAR_HISTORY:BACKFILL</c>).</summary>
    void EnqueueBackfill(StateObject state, DateTime now);
}

/// <summary>
/// Bounded queue plus a fixed number of workers: when Scylla is slow the queue fills and further writes are dropped
/// (counted) instead of piling up memory or slowing the state update. Order rows are read-merge-written so the
/// fill-time accumulation survives restarts; same key always overwrites, making replays harmless.
/// </summary>
public sealed class BazaarHistoryWriter : IBazaarOrderHistoryWriter, IDisposable
{
    private static readonly Prometheus.Counter written = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_bazaar_history_written_total", "History rows written.", "kind");
    private static readonly Prometheus.Counter failed = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_bazaar_history_failed_total", "History writes that failed.", "kind");
    private static readonly Prometheus.Counter dropped = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_bazaar_history_dropped_total", "History writes dropped because the queue was full.", "kind");

    private const int MaxBackfillOffers = 300;
    private readonly IBazaarHistoryStore store;
    private readonly BazaarHistoryOptions options;
    private readonly ILogger<BazaarHistoryWriter> logger;
    private readonly Channel<object>? queue;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim[] keyLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly List<Task> workers = new();
    private int pending;
    private long lastFailureLog = long.MinValue / 2;

    public bool Enabled => options.Enabled;
    /// <summary>Items queued or being processed, for tests and shutdown draining.</summary>
    public int Pending => Volatile.Read(ref pending);

    public BazaarHistoryWriter(IBazaarHistoryStore store, BazaarHistoryOptions options, ILogger<BazaarHistoryWriter> logger)
    {
        this.store = store;
        this.options = options;
        this.logger = logger;
        if (!options.Enabled)
        {
            logger.LogInformation("Bazaar history disabled: BAZAAR_HISTORY:SALT is not configured");
            return;
        }
        queue = Channel.CreateBounded<object>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        for (var i = 0; i < Math.Max(1, options.WriteConcurrency); i++)
            workers.Add(Task.Run(() => Work(stop.Token)));
    }

    public void EnqueueOrder(BazaarOrderLifecycleEvent lifecycleEvent, IEnumerable<Fill>? fills)
    {
        try
        {
            if (!Enabled || lifecycleEvent == null || lifecycleEvent.PlayerUuid == Guid.Empty || string.IsNullOrWhiteSpace(lifecycleEvent.ItemTag))
                return;
            var hash = BazaarAccountHasher.Hash(options.Salt!, lifecycleEvent.PlayerUuid);
            Enqueue(BazaarOrderHistoryMerge.Observe(lifecycleEvent, fills?.ToList(), hash), "order");
        }
        catch (Exception e)
        {
            failed.WithLabels("order").Inc();
            logger.LogWarning(e, "Could not queue bazaar order history");
        }
    }

    public void EnqueueFlip(CompletedBazaarFlip flip)
    {
        try
        {
            if (!Enabled || flip == null || flip.PlayerUuid == Guid.Empty || string.IsNullOrWhiteSpace(flip.ItemTag))
                return;
            Enqueue(BazaarOrderHistoryMerge.ToFlipRow(flip, BazaarAccountHasher.Hash(options.Salt!, flip.PlayerUuid)), "flip");
        }
        catch (Exception e)
        {
            failed.WithLabels("flip").Inc();
            logger.LogWarning(e, "Could not queue bazaar flip history");
        }
    }

    public void EnqueueBackfill(StateObject state, DateTime now)
    {
        try
        {
            if (!Enabled || !options.Backfill)
                return;
            var uuid = state.McInfo?.Uuid ?? Guid.Empty;
            if (uuid == Guid.Empty)
                return;
            var hash = BazaarAccountHasher.Hash(options.Salt!, uuid);
            var offers = (state.BazaarOffers ?? new()).Concat(state.ClosedBazaarOffers ?? new())
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.ItemTag) && o.Created != default)
                .Take(MaxBackfillOffers);
            foreach (var o in offers)
            {
                var obs = new BazaarOrderObservation
                {
                    ItemTag = o.ItemTag,
                    AccountHash = hash,
                    IsSell = o.IsSell,
                    Created = BazaarHistoryKeys.Utc(o.Created),
                    PricePerUnit = o.PricePerUnit,
                    Amount = o.Amount,
                    FilledAmount = o.FilledAmount,
                    ClaimedAmount = o.ClaimedAmount,
                    IsExpired = o.IsExpired,
                    ClosedAt = o.ClosedAt == null ? null : BazaarHistoryKeys.Utc(o.ClosedAt.Value),
                    State = BazaarOrderHistoryMerge.StateFor(o),
                    ObservedAt = BazaarHistoryKeys.Utc(now),
                    Source = BazaarOrderHistoryMerge.SourceBackfill,
                    Fills = o.Customers?.Where(f => f != null).Select(f => (BazaarHistoryKeys.Utc(f.TimeStamp), f.Amount)).ToList() ?? new(),
                    IsBackfill = true
                };
                Enqueue(obs, "order");
            }
        }
        catch (Exception e)
        {
            failed.WithLabels("order").Inc();
            logger.LogWarning(e, "Could not queue bazaar history backfill");
        }
    }

    private void Enqueue(object item, string kind)
    {
        Interlocked.Increment(ref pending);
        // TryWrite never blocks: a full queue drops the write instead of delaying the state update
        if (queue == null || !queue.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref pending);
            dropped.WithLabels(kind).Inc();
        }
    }

    private async Task Work(CancellationToken token)
    {
        try
        {
            await foreach (var item in queue!.Reader.ReadAllAsync(token))
            {
                try { await Process(item); }
                finally { Interlocked.Decrement(ref pending); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Writes one queued item; failures are logged and counted, never thrown.</summary>
    public async Task Process(object item)
    {
        var kind = item is BazaarFlipHistoryRow ? "flip" : "order";
        try
        {
            if (item is BazaarFlipHistoryRow flip)
                await store.UpsertFlip(flip, options.TtlSeconds);
            else if (item is BazaarOrderObservation obs)
                await ProcessOrder(obs);
            written.WithLabels(kind).Inc();
        }
        catch (Exception e)
        {
            failed.WithLabels(kind).Inc();
            // a down store fails every queued write: log the message at most once a minute, no stack trace flood
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref lastFailureLog);
            if (now - last >= 60_000 && Interlocked.CompareExchange(ref lastFailureLog, now, last) == last)
                logger.LogWarning("Bazaar {Kind} history write failed (further failures counted, not logged): {Error}", kind, e.Message);
        }
    }

    /// <summary>Re-sends that change nothing but LastObservedAt are written at most this often per order.</summary>
    public static TimeSpan LastObservedRefresh { get; set; } = TimeSpan.FromHours(1);
    private const int CacheCapacity = 20_000;

    private sealed class CacheEntry
    {
        public BazaarOrderHistoryRow Row = null!;
        public DateTime ObservedWritten;
    }

    private readonly Dictionary<string, LinkedListNode<(string Key, CacheEntry Entry)>> cacheIndex = new();
    private readonly LinkedList<(string Key, CacheEntry Entry)> cacheOrder = new();
    private readonly object cacheGate = new();

    private CacheEntry? CacheGet(string key)
    {
        lock (cacheGate)
        {
            if (!cacheIndex.TryGetValue(key, out var node))
                return null;
            cacheOrder.Remove(node);
            cacheOrder.AddFirst(node);
            return node.Value.Entry;
        }
    }

    private void CachePut(string key, CacheEntry entry)
    {
        lock (cacheGate)
        {
            if (cacheIndex.TryGetValue(key, out var old))
                cacheOrder.Remove(old);
            cacheIndex[key] = cacheOrder.AddFirst((key, entry));
            while (cacheIndex.Count > CacheCapacity)
            {
                cacheIndex.Remove(cacheOrder.Last!.Value.Key);
                cacheOrder.RemoveLast();
            }
        }
    }

    /// <summary>
    /// Update-only write path: the last written state per order is kept in a bounded LRU so unchanged re-sends
    /// (the 10 minute full resend) cost nothing, and only advanced columns plus new fill points are sent as a CQL UPDATE
    /// (no collection tombstone). A cache miss reads the row once, except for chat-setup which is by definition the first sighting.
    /// <para>
    /// Residual race: the cache is per pod, so two pods writing the same order, or a stale cache after a TTL expiry, may
    /// miss a fill point or set full_fill_at to a later observation time. Orders are handled by one pod per player, and
    /// the map is append-only so no write can remove data.
    /// </para>
    /// </summary>
    private async Task ProcessOrder(BazaarOrderObservation obs)
    {
        var day = BazaarHistoryKeys.DayOf(obs.Created);
        var key = $"{obs.ItemTag}|{obs.Created.Ticks}|{obs.AccountHash}|{obs.IsSell}";
        var lockIndex = (int)((uint)HashCode.Combine(key) % (uint)keyLocks.Length);
        var gate = keyLocks[lockIndex];
        await gate.WaitAsync();
        try
        {
            var cached = CacheGet(key);
            var before = cached?.Row;
            if (before == null && obs.Source != BazaarOrderLifecycleEvent.SourceChatSetup)
                before = await store.GetOrder(obs.ItemTag, day, obs.Created, obs.AccountHash, obs.IsSell);
            var after = BazaarOrderHistoryMerge.Apply(before?.Clone(), obs);
            var (changed, newPoints) = BazaarOrderHistoryMerge.Diff(before, after);
            var writtenObserved = cached?.ObservedWritten ?? before?.LastObservedAt ?? DateTime.MinValue;
            var substantive = (changed & ~OrderColumns.LastObserved) != OrderColumns.None || newPoints.Count > 0;
            var due = after.LastObservedAt - writtenObserved >= LastObservedRefresh;
            if (!substantive && !due)
            {
                // a heartbeat that changed nothing but the observation time: no read, no write
                CachePut(key, new CacheEntry { Row = after, ObservedWritten = writtenObserved });
                return;
            }
            await store.WriteOrder(after, changed, newPoints, options.TtlSeconds);
            CachePut(key, new CacheEntry { Row = after, ObservedWritten = after.LastObservedAt });
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        queue?.Writer.TryComplete();
    }
}

/// <summary>Registered when the feature is disabled so the order and flip hooks cost nothing.</summary>
public sealed class NoopBazaarOrderHistoryWriter : IBazaarOrderHistoryWriter
{
    public bool Enabled => false;
    public void EnqueueOrder(BazaarOrderLifecycleEvent lifecycleEvent, IEnumerable<Fill>? fills) { }
    public void EnqueueFlip(CompletedBazaarFlip flip) { }
    public void EnqueueBackfill(StateObject state, DateTime now) { }
}
