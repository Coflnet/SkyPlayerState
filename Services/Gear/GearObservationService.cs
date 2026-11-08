using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Settings of the gear observations. Config keys (double underscore in env): <c>GEAR:OBS_TTL_DAYS</c> (90),
/// <c>GEAR:QUEUE_CAPACITY</c> (5000), <c>GEAR:WRITE_CONCURRENCY</c> (2). Nothing is required.
/// </summary>
public class GearObservationOptions
{
    public int TtlDays { get; set; } = 90;
    public int QueueCapacity { get; set; } = 5_000;
    public int WriteConcurrency { get; set; } = 2;

    public int TtlSeconds => (int)Math.Min(630_720_000L, Math.Max(1, (long)TtlDays) * 86_400L);

    public static GearObservationOptions From(IConfiguration config)
    {
        static int Int(string? v, int fallback) => int.TryParse(v, out var i) && i > 0 ? i : fallback;
        return new GearObservationOptions
        {
            TtlDays = Int(config["GEAR:OBS_TTL_DAYS"], 90),
            QueueCapacity = Int(config["GEAR:QUEUE_CAPACITY"], 5_000),
            WriteConcurrency = Int(config["GEAR:WRITE_CONCURRENCY"], 2),
        };
    }
}

/// <summary>Where the folder sends Folded outcomes; implementations never throw into the caller.</summary>
public interface IGearObservationSink
{
    void Record(GearFoldInput input);
    /// <summary>Removes the player's observations (player data deletion).</summary>
    Task DeletePlayer(Guid playerUuid);
}

/// <summary>Player key of the gear tables: the uuid in "N" format, like the other task tables (task_player_stats, ...).</summary>
public static class GearPlayerKey
{
    public static string Of(Guid playerUuid) => playerUuid.ToString("N");
}

/// <summary>
/// Builds observations (<see cref="GearObservationBuilder"/>) and writes them through a bounded queue with a few workers:
/// a slow store fills the queue and further observations are dropped (counted) instead of delaying the fold. A
/// fingerprint's gear json is rewritten at most once a day per pod, which refreshes its TTL. A deleted player's key is
/// blocked for <see cref="DeletedKeyBlock"/>: queued observations of it are discarded, and the delete is repeated once
/// writes already in flight have finished.
/// </summary>
public sealed class GearObservationService : IGearObservationSink, IDisposable
{
    private static readonly Prometheus.Counter outcomes = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_gear_observations_total", "Gear observation outcomes (written, dropped, failed, skip reasons).", "outcome");
    private static readonly TimeSpan GearRefresh = TimeSpan.FromDays(1);
    public static readonly TimeSpan DeletedKeyBlock = TimeSpan.FromHours(1);
    private static readonly TimeSpan InFlightWait = TimeSpan.FromSeconds(10);
    private const int MaxRememberedFingerprints = 50_000;

    private readonly IGearObservationStore store;
    private readonly GearObservationOptions options;
    private readonly ILogger<GearObservationService> logger;
    private readonly Channel<GearObservationRecord> queue;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, DateTime> gearWrittenAt = new();
    private readonly ConcurrentDictionary<string, DateTime> deletedAt = new();
    private readonly ConcurrentDictionary<string, int> inFlight = new();
    private int pending;
    private long lastFailureLog = long.MinValue / 2;

    /// <summary>Observations queued or being written, for tests.</summary>
    public int Pending => Volatile.Read(ref pending);
    /// <summary>Clock of the fold time, replaceable in tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public GearObservationService(IGearObservationStore store, GearObservationOptions options, ILogger<GearObservationService> logger)
    {
        this.store = store;
        this.options = options;
        this.logger = logger;
        queue = Channel.CreateBounded<GearObservationRecord>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        for (var i = 0; i < Math.Max(1, options.WriteConcurrency); i++)
            _ = Task.Run(() => Work(stop.Token));
    }

    public void Record(GearFoldInput input)
    {
        try
        {
            if (input?.Period == null || !Guid.TryParse(input.Period.PlayerUuid, out var uuid) || uuid == Guid.Empty)
                return;
            var key = GearPlayerKey.Of(uuid);
            if (IsDeleted(key))
                return;
            var (record, skip) = GearObservationBuilder.Build(input, key, Now());
            if (record == null)
            {
                outcomes.WithLabels(skip ?? "skipped").Inc();
                return;
            }
            Enqueue(record);
        }
        catch (Exception e)
        {
            outcomes.WithLabels("failed").Inc();
            LogFailure("queue", e);
        }
    }

    private void Enqueue(GearObservationRecord record)
    {
        Interlocked.Increment(ref pending);
        if (queue.Writer.TryWrite(record))
            return;
        Interlocked.Decrement(ref pending);
        outcomes.WithLabels("dropped").Inc();
    }

    private async Task Work(CancellationToken token)
    {
        try
        {
            await foreach (var record in queue.Reader.ReadAllAsync(token))
                await WriteQueued(record);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteQueued(GearObservationRecord record)
    {
        try { await Write(record); }
        finally { Interlocked.Decrement(ref pending); }
    }

    /// <summary>Writes one record unless its player was deleted meanwhile; failures are counted and logged at most once a minute.</summary>
    public async Task Write(GearObservationRecord record)
    {
        var key = record.Observation.PlayerKey;
        inFlight.AddOrUpdate(key, 1, (_, n) => n + 1);
        try
        {
            if (IsDeleted(key))
            {
                outcomes.WithLabels("deleted").Inc();
                return;
            }
            await WriteToStore(record);
        }
        catch (Exception e)
        {
            outcomes.WithLabels("failed").Inc();
            LogFailure("write", e);
        }
        finally
        {
            inFlight.AddOrUpdate(key, 0, (_, n) => n - 1);
        }
    }

    private async Task WriteToStore(GearObservationRecord record)
    {
        var fingerprint = record.Observation.GearFingerprint;
        var json = GearDue(fingerprint) ? record.GearJson : null;
        await store.Write(record.Observation, json, options.TtlSeconds);
        if (json != null)
            RememberGear(fingerprint);
        outcomes.WithLabels("written").Inc();
    }

    /// <summary>A down store fails every queued write: log at most once a minute, without a stack trace flood.</summary>
    private void LogFailure(string what, Exception e)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastFailureLog);
        if (now - last >= 60_000 && Interlocked.CompareExchange(ref lastFailureLog, now, last) == last)
            logger.LogWarning("Gear observation {What} failed (further failures counted, not logged): {Error}", what, e.Message);
    }

    private bool GearDue(string fingerprint)
        => !gearWrittenAt.TryGetValue(fingerprint, out var at) || DateTime.UtcNow - at >= GearRefresh;

    private void RememberGear(string fingerprint)
    {
        if (gearWrittenAt.Count >= MaxRememberedFingerprints)
            gearWrittenAt.Clear();
        gearWrittenAt[fingerprint] = DateTime.UtcNow;
    }

    private bool IsDeleted(string key)
        => deletedAt.TryGetValue(key, out var at) && DateTime.UtcNow - at < DeletedKeyBlock;

    public async Task DeletePlayer(Guid playerUuid)
    {
        if (playerUuid == Guid.Empty)
            return;
        var key = GearPlayerKey.Of(playerUuid);
        foreach (var (old, at) in deletedAt)
            if (DateTime.UtcNow - at >= DeletedKeyBlock)
                deletedAt.TryRemove(old, out _);
        deletedAt[key] = DateTime.UtcNow;
        await store.DeletePlayer(key);
        if (!await WaitForInFlight(key))
            logger.LogWarning("Gear observation writes of a deleted player were still running after {Wait}", InFlightWait);
        // a write that started before the block may have landed after the first delete
        await store.DeletePlayer(key);
    }

    private async Task<bool> WaitForInFlight(string key)
    {
        var until = DateTime.UtcNow + InFlightWait;
        while (inFlight.GetValueOrDefault(key) > 0)
        {
            if (DateTime.UtcNow > until)
                return false;
            await Task.Delay(50);
        }
        return true;
    }

    public void Dispose()
    {
        stop.Cancel();
        queue.Writer.TryComplete();
    }
}
