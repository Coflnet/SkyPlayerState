using System.Diagnostics;
using Prometheus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.Bazaar.Client.Client;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderSync(HttpClient client)
{
    internal const string SourceName = "Coflnet.Sky.UserState.Bazaar";
    internal static readonly ActivitySource Traces = new(SourceName);
    internal static readonly Counter Syncs = Metrics.CreateCounter("sky_userstate_bazaar_sync_total", "Order synchronization attempts", new CounterConfiguration { LabelNames = new[] { "operation", "result" } });
    internal static readonly Counter OrderSyncs = Metrics.CreateCounter("sky_playerstate_bazaar_order_sync_total", "Personal order book observations by outcome (sent, skipped, failed)", new CounterConfiguration { LabelNames = new[] { "result" } });
    /// <summary>Unchanged views are not re-posted until this interval passed, so the server can repair drift.</summary>
    internal static readonly TimeSpan FullResendInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheRetention = TimeSpan.FromHours(1);
    private const int CacheSweepThreshold = 5000;
    private static readonly ConcurrentDictionary<string, SyncedView> lastSynced = new();
    private static long lastSweepTicks;
    private static readonly Gauge Waiting = Metrics.CreateGauge("sky_userstate_bazaar_retry_waits", "Order observations currently waiting ten seconds to retry");

    internal static async Task Retry(Func<Task> send, ILogger logger, CancellationToken cancellationToken = default,
        string operation = "observe", string userId = null, string itemTag = null, DateTime? created = null)
    {
        using var span = Traces.StartActivity("bazaar.order.sync");
        span?.SetTag("bazaar.user_id", userId);
        span?.SetTag("bazaar.item_tag", itemTag);
        span?.SetTag("bazaar.operation", operation);
        span?.SetTag("bazaar.order_created", created?.ToString("O"));
        var started = Stopwatch.GetTimestamp();
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                await send();
                Syncs.WithLabels(operation, "success").Inc();
                logger.Log(attempts > 1 ? LogLevel.Information : LogLevel.Debug,
                    "Bazaar sync {Operation} succeeded for {UserId}/{ItemTag}, order created {OrderCreated}, attempts {Attempts}, elapsed {ElapsedMs} ms; TraceId {TraceId}",
                    operation, userId, itemTag, created, attempts, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Activity.Current?.TraceId.ToString());
                return;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && IsTemporary(e))
            {
                Syncs.WithLabels(operation, "retry").Inc();
                logger.LogWarning(e, "Bazaar sync {Operation} unavailable for {UserId}/{ItemTag}, order created {OrderCreated}, attempt {Attempt}; retry in 10 seconds; TraceId {TraceId}",
                    operation, userId, itemTag, created, attempts, Activity.Current?.TraceId.ToString());
                Waiting.Inc();
                try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
                finally { Waiting.Dec(); }
            }
            catch (Exception e)
            {
                Syncs.WithLabels(operation, "failure").Inc();
                span?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
                logger.LogError(e, "Bazaar sync {Operation} rejected for {UserId}/{ItemTag}, order created {OrderCreated}; TraceId {TraceId}",
                    operation, userId, itemTag, created, Activity.Current?.TraceId.ToString());
                throw;
            }
        }
    }

    private static bool IsTemporary(Exception error) => error switch {
        ApiException e => e.ErrorCode == 0 || e.ErrorCode == 408 || e.ErrorCode == 429 || e.ErrorCode >= 500,
        HttpRequestException e => e.StatusCode == null || (int)e.StatusCode >= 500
            || e.StatusCode == HttpStatusCode.RequestTimeout || (int)e.StatusCode == 429,
        TaskCanceledException => true,
        _ => false
    };

    /// <summary>What the server last accepted for a player: hashes of open and closed orders plus when the full set was last sent.</summary>
    private sealed record SyncedView(HashSet<long> Open, HashSet<long> Closed, DateTime FullSentAt);

    internal static void ResetSyncCache() => lastSynced.Clear();

    internal static long Fingerprint(ObservedOrder o)
    {
        var text = string.Join('|', o.ItemId, o.Timestamp.Ticks, o.IsSell, o.Amount, o.Filled, o.PricePerUnit,
            o.IsExpired, o.Claimed, o.ClosedAt?.Ticks, o.PlayerName);
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static void SweepCache(DateTime now)
    {
        if (lastSynced.Count < CacheSweepThreshold)
            return;
        var tick = Environment.TickCount64;
        if (tick - Interlocked.Read(ref lastSweepTicks) < 300_000)
            return;
        Interlocked.Exchange(ref lastSweepTicks, tick);
        foreach (var entry in lastSynced.Where(e => now - e.Value.FullSentAt > CacheRetention).ToList())
            lastSynced.TryRemove(entry.Key, out _);
    }

    internal sealed class ObservedOrder : OrderEntry
    {
        public bool IsExpired { get; set; }
        public int? Claimed { get; set; }
        public DateTime? ClosedAt { get; set; }
    }

    internal static ObservedOrder ToOrder(UpdateArgs args, Offer order) => new() {
        ItemId = order.ItemTag, Amount = (int)order.Amount, Filled = (int)order.FilledAmount,
        PricePerUnit = order.PricePerUnit, IsSell = order.IsSell, Timestamp = order.Created,
        IsExpired = order.IsExpired, Claimed = (int?)order.ClaimedAmount, ClosedAt = order.ClosedAt,
        UserId = args.msg.UserId, PlayerName = args.currentState.McInfo.Name
    };

    /// <summary>The fingerprint the order book sync uses for change detection, optionally with another tag / close time.</summary>
    internal static long Revision(UpdateArgs args, Offer order, string itemTag, DateTime? closedAt)
    {
        var observed = ToOrder(args, order);
        observed.ItemId = itemTag;
        observed.ClosedAt = closedAt;
        return Fingerprint(observed);
    }

    public virtual async Task Claim(UpdateArgs args, Offer order)
    {
        if (string.IsNullOrWhiteSpace(order.ItemTag))
            order.ItemTag = await BazaarOrderListener.GetTagForName(args, order.ItemName);
        if (string.IsNullOrWhiteSpace(order.ItemTag))
        {
            // Root cause of a dominant production error: GetTagForName used to throw here for
            // unresolvable item names (e.g. "Turbo-Cane I") - called from this UNCAUGHT path, that
            // aborted the whole state update. Skip the order-book sync instead; the claim's own
            // coin/item transactions (recorded by the caller before this) are unaffected.
            args.GetService<ILogger<BazaarOrderSync>>().LogInformation(
                "Skipping Bazaar order sync for {UserId}: could not resolve item tag for {ItemName}",
                args.msg.UserId, order.ItemName);
            return;
        }
        var update = ToOrder(args, order);
        await Retry(async () => {
            using var response = await client.PostAsJsonAsync("OrderBook", update);
            response.EnsureSuccessStatusCode();
        }, args.GetService<ILogger<BazaarOrderSync>>(), operation: "claim", userId: args.msg.UserId,
            itemTag: order.ItemTag, created: order.Created);
        BazaarOrderLifecycle.Emit(args, order, BazaarOrderLifecycleEvent.SourceChatClaim, BazaarOrderLifecycleEvent.StateClaimed);
    }

    private static void EmitLifecycle(UpdateArgs args, List<ObservedOrder> sent, Dictionary<ObservedOrder, Offer> offerOf, SyncedView previous, bool full)
    {
        try
        {
            var producer = BazaarOrderLifecycle.Resolve(args);
            if (producer == null)
                return;
            foreach (var order in sent)
            {
                if (!offerOf.TryGetValue(order, out var offer))
                    continue;
                var hash = Fingerprint(order);
                var unchanged = previous != null && (previous.Open.Contains(hash) || previous.Closed.Contains(hash));
                if (unchanged && !full)
                    continue; // only re-posted as part of an otherwise changed view
                var source = unchanged ? BazaarOrderLifecycleEvent.SourceGuiFullResend : BazaarOrderLifecycleEvent.SourceGuiView;
                var state = order.ClosedAt.HasValue ? BazaarOrderLifecycleEvent.StateClosed
                    : order.IsExpired ? BazaarOrderLifecycleEvent.StateExpired
                    : order.Filled >= order.Amount ? BazaarOrderLifecycleEvent.StateFilled
                    : BazaarOrderLifecycleEvent.StateOpen;
                BazaarOrderLifecycle.Emit(args, offer, source, state);
            }
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderSync>>().LogWarning(e, "Could not emit bazaar order lifecycle events");
        }
    }

    public virtual async Task Observe(UpdateArgs args)
    {
        var logger = args.GetService<ILogger<BazaarOrderSync>>();
        // Chat-created orders may not have received an item tag from a menu yet.
        var offers = args.currentState.BazaarOffers.Concat(args.currentState.ClosedBazaarOffers ?? new())
            .Where(o => o != null && (!o.ClosedAt.HasValue || o.ClosedAt >= args.msg.ReceivedAt.AddDays(-7)))
            .DistinctBy(o => (o.Created, o.IsSell, o.ItemName)).ToList();
        foreach (var offer in offers.Where(o => string.IsNullOrWhiteSpace(o.ItemTag)))
            offer.ItemTag = await BazaarOrderListener.GetTagForName(args, offer.ItemName);
        // An item name the search still can't resolve (see GetTagForName) must never be posted with
        // a blank item id - drop it from this sync rather than sending a garbage order book entry.
        var unresolved = offers.Where(o => string.IsNullOrWhiteSpace(o.ItemTag)).ToList();
        if (unresolved.Count > 0)
            logger.LogInformation(
                "Skipping {count} Bazaar offer(s) with unresolved item tags from personal sync for {UserId}: {items}",
                unresolved.Count, args.msg.UserId, string.Join(", ", unresolved.Select(o => o.ItemName)));
        var resolvedOffers = unresolved.Count == 0 ? offers : offers.Except(unresolved).ToList();
        // Capture the observation once so a retry cannot change its identity or timestamp.
        var orders = resolvedOffers.Select(o => ToOrder(args, o)).ToList();
        // The server only removes open orders missing from a view; closed orders merely update what it
        // already stores, so an unchanged closed order never needs to be posted again until the full re-send.
        var syncKey = $"{args.msg.UserId}:{args.currentState.McInfo.Name}";
        var open = new HashSet<long>();
        var closed = new HashSet<long>();
        var fingerprints = orders.Select(o => (Order: o, Hash: Fingerprint(o))).ToList();
        var offerOf = resolvedOffers.Zip(orders).ToDictionary(p => p.Second, p => p.First);
        foreach (var (order, hash) in fingerprints)
            (order.ClosedAt.HasValue ? closed : open).Add(hash);
        var receivedAt = args.msg.ReceivedAt;
        lastSynced.TryGetValue(syncKey, out var previous);
        var full = previous == null || receivedAt - previous.FullSentAt >= FullResendInterval;
        if (!full && previous.Open.SetEquals(open) && closed.IsSubsetOf(previous.Closed))
        {
            OrderSyncs.WithLabels("skipped").Inc();
            logger.LogDebug("Skipping unchanged personal Bazaar view for {UserId}: {OrderCount} orders", args.msg.UserId, orders.Count);
            return;
        }
        if (!full)
            orders = fingerprints.Where(f => !f.Order.ClosedAt.HasValue || !previous.Closed.Contains(f.Hash)).Select(f => f.Order).ToList();
        var observation = new {
            args.msg.UserId, PlayerName = args.currentState.McInfo.Name,
            Timestamp = args.msg.ReceivedAt, Orders = orders
        };
        logger.LogDebug("Synchronizing personal Bazaar view for {UserId}/{PlayerName}: {OrderCount} orders at {ObservedAt:o}",
            args.msg.UserId, observation.PlayerName, orders.Count, observation.Timestamp);
        try
        {
        await Retry(async () => {
            using var response = await client.PostAsJsonAsync("OrderBook/player", observation);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Using legacy Bazaar registration for {UserId}; personal reconciliation endpoint is unavailable", args.msg.UserId);
                // Older SkyBazaar instances support registration but not full reconciliation.
                foreach (var order in orders.Where(o => !o.IsExpired))
                {
                    using var legacy = await client.PostAsJsonAsync("OrderBook", order);
                    legacy.EnsureSuccessStatusCode();
                }
                return;
            }
            response.EnsureSuccessStatusCode();
        }, logger, operation: "observe", userId: args.msg.UserId);
        }
        catch
        {
            OrderSyncs.WithLabels("failed").Inc();
            throw; // nothing is recorded, so the next view sends again
        }
        OrderSyncs.WithLabels("sent").Inc();
        EmitLifecycle(args, orders, offerOf, previous, full);
        lastSynced[syncKey] = new SyncedView(open, closed, full ? receivedAt : previous.FullSentAt);
        SweepCache(receivedAt);
    }
}
