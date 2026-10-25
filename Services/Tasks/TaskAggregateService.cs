using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Merged in-memory view of one (task, statBucket) aggregate, rebuilt from the
/// instance-owned cassandra rows with a 7 day half life decay applied at read.
/// </summary>
public class BucketAggregate
{
    public string TaskName;
    public byte Bucket;
    public double WSeconds;
    public double ResidualCoins;
    public double RareCoins;
    public double WPeriods;
    /// <summary>coin value of the item counts at fold time, used to scale the coin pools for price drift</summary>
    public double RefItemValue;
    public Dictionary<string, double> ItemCounts = new();
    public HashSet<string> Contributors = new();

    /// <summary>Distinct players that contributed data to this task+bucket.</summary>
    public int ContributorCount => Contributors.Count;
}

/// <summary>Merged, decayed view of how much time and coin value one task accumulated in one zone.</summary>
public class ZoneAggregate
{
    public string Zone;
    public double WSeconds;
    public double Value;
    public HashSet<string> Contributors = new();
}

/// <summary>
/// Per (task, bucket) additive aggregate of coin-equivalent collection data,
/// shared across instances through cassandra. Each instance writes only its own
/// rows (one writer per (day, task, bucket, instance) so no read-modify-write race)
/// and every instance merge-reads all rows to build its estimate view.
/// </summary>
public class TaskAggregateService
{
    private Table<TaskBucketAggregateRow> table;
    private Table<TaskPlayerStatRow> playerTable;
    private Table<TaskZoneAggregateRow> zoneTable;
    private readonly ISession session;
    private readonly ILogger<TaskAggregateService> logger;
    private readonly string instanceId = Environment.MachineName;
    private const int TTL_SECONDS = 1209600; // 14 days
    private const double DecayHalfLifeDays = 7;
    // with a stable instanceId a day partition holds ~tasks*buckets*instances rows (~1.8k);
    // page generously so the merge is one round trip per day even after some instanceId churn.
    private const int MergePageSize = 5000;

    // pending deltas accumulated in memory, flushed to cassandra periodically
    private readonly ConcurrentDictionary<(string task, byte bucket), PendingDelta> pending = new();
    private readonly ConcurrentDictionary<(string task, string zone), PendingZoneDelta> pendingZones = new();
    private readonly SemaphoreSlim flushLock = new(1, 1);

    // merged read snapshot, rebuilt only in the background (never on the request path)
    private Dictionary<(string task, byte bucket), BucketAggregate> snapshot = new();
    private Dictionary<string, Dictionary<string, ZoneAggregate>> zoneSnapshot = new();
    private DateTime snapshotAt = DateTime.MinValue;
    private readonly SemaphoreSlim mergeLock = new(1, 1);
    private int warmupKicked;

    public TaskAggregateService(ISession session, ILogger<TaskAggregateService> logger)
    {
        this.session = session;
        this.logger = logger;
    }

    private class PendingDelta
    {
        public double WSeconds, ResidualCoins, RareCoins, WPeriods, RefItemValue;
        public readonly ConcurrentDictionary<string, double> ItemCounts = new();
        public readonly HashSet<string> Players = new();
        public readonly List<string> TraceIds = new();
        public readonly object gate = new();
    }

    private class PendingZoneDelta
    {
        public double WSeconds, Value, Periods;
        public readonly HashSet<string> Players = new();
        public readonly object gate = new();
    }

    /// <summary>
    /// Add one folded period's share in one zone to the "best spot" aggregate. Only called for periods
    /// the estimate itself folded, with the same ramp weight applied to <paramref name="wSeconds"/>/<paramref name="value"/>.
    /// </summary>
    public void AddZoneContribution(string task, string zone, string playerUuid, double wSeconds, double value)
    {
        if (task == null || string.IsNullOrEmpty(zone))
            return;
        var delta = pendingZones.GetOrAdd((task, zone), _ => new PendingZoneDelta());
        lock (delta.gate)
        {
            delta.WSeconds += wSeconds;
            delta.Value += value;
            delta.Periods += 1;
            if (playerUuid != null)
                delta.Players.Add(playerUuid);
        }
    }

    /// <summary>The not yet flushed (seconds, value, players) of one task and zone, for tests.</summary>
    internal (double wSeconds, double value, int players) PendingZoneForTest(string task, string zone)
    {
        if (!pendingZones.TryGetValue((task, zone), out var delta))
            return (0, 0, 0);
        lock (delta.gate)
            return (delta.WSeconds, delta.Value, delta.Players.Count);
    }

    /// <summary>
    /// Fold one classified, filtered period contribution into the shared aggregate.
    /// Amounts are already weighted by the ramp.
    /// </summary>
    public void AddContribution(string task, byte bucket, string playerUuid, double wSeconds,
        Dictionary<string, double> weightedItemCounts, double residualCoins, double rareCoins,
        double refItemValue, string traceId)
    {
        var delta = pending.GetOrAdd((task, bucket), _ => new PendingDelta());
        lock (delta.gate)
        {
            delta.WSeconds += wSeconds;
            delta.ResidualCoins += residualCoins;
            delta.RareCoins += rareCoins;
            delta.WPeriods += 1;
            delta.RefItemValue += refItemValue;
            if (playerUuid != null)
                delta.Players.Add(playerUuid);
            foreach (var (tag, count) in weightedItemCounts)
                delta.ItemCounts.AddOrUpdate(tag, count, (_, prev) => prev + count);
            if (traceId != null && delta.TraceIds.Count < 5)
                delta.TraceIds.Add(traceId);
        }
    }

    /// <summary>The not yet flushed contribution of one task over all buckets (wSeconds, item counts), for tests.</summary>
    internal (double wSeconds, Dictionary<string, double> itemCounts) PendingForTest(string task)
    {
        var seconds = 0.0;
        var items = new Dictionary<string, double>();
        foreach (var ((name, _), delta) in pending.Where(p => p.Key.task == task))
            lock (delta.gate)
            {
                seconds += delta.WSeconds;
                foreach (var (tag, count) in delta.ItemCounts)
                    items[tag] = items.GetValueOrDefault(tag) + count;
            }
        return (seconds, items);
    }

    /// <summary>
    /// Flush accumulated in-memory deltas to this instance's cassandra rows.
    /// Called on a timer (every ~60s).
    /// </summary>
    public async Task Flush()
    {
        if (pending.IsEmpty && pendingZones.IsEmpty)
            return;
        await flushLock.WaitAsync();
        try
        {
            if (!pending.IsEmpty)
                await FlushBuckets();
            if (!pendingZones.IsEmpty)
                await FlushZones();
        }
        finally
        {
            flushLock.Release();
        }
    }

    private async Task FlushBuckets()
    {
        {
            if (table == null)
                await Setup();
            var dayBucket = DateTime.UtcNow.Date;
            var keys = pending.Keys.ToList();
            foreach (var key in keys)
            {
                if (!pending.TryRemove(key, out var delta))
                    continue;
                TaskBucketAggregateRow row;
                lock (delta.gate)
                {
                    row = new TaskBucketAggregateRow
                    {
                        DayBucket = dayBucket,
                        TaskName = key.task,
                        Bucket = key.bucket,
                        InstanceId = instanceId,
                        WSeconds = delta.WSeconds,
                        ResidualCoins = delta.ResidualCoins,
                        RareCoins = delta.RareCoins,
                        WPeriods = delta.WPeriods,
                        RefItemValue = delta.RefItemValue,
                        ItemCounts = new Dictionary<string, double>(delta.ItemCounts),
                        Players = new HashSet<string>(delta.Players),
                        LastFoldTraceIds = delta.TraceIds.ToList()
                    };
                }
                try
                {
                    // read existing own row for today and add to it (single writer, no race)
                    var bucketInt = (int)key.bucket;
                    var existing = (await table!.Where(r =>
                        r.DayBucket == dayBucket && r.TaskName == key.task && r.Bucket == bucketInt && r.InstanceId == instanceId)
                        .ExecuteAsync()).FirstOrDefault();
                    if (existing != null)
                    {
                        row.WSeconds += existing.WSeconds;
                        row.ResidualCoins += existing.ResidualCoins;
                        row.RareCoins += existing.RareCoins;
                        row.WPeriods += existing.WPeriods;
                        row.RefItemValue += existing.RefItemValue;
                        if (existing.ItemCounts != null)
                            foreach (var (tag, count) in existing.ItemCounts)
                                row.ItemCounts[tag] = row.ItemCounts.GetValueOrDefault(tag) + count;
                        if (existing.Players != null)
                            row.Players.UnionWith(existing.Players);
                    }
                    // keep every cost plus the top 12 items to bound row size
                    if (row.ItemCounts.Count > 12)
                        row.ItemCounts = KeepCostsAndTop(row.ItemCounts, 12);
                    await table!.Insert(row).SetTTL(TTL_SECONDS).ExecuteAsync();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "failed to flush aggregate {task}/{bucket}", key.task, key.bucket);
                }
            }
        }
    }

    /// <summary>Same single writer day-row pattern as the bucket rows, for the per (task, zone) "best spot" data.</summary>
    private async Task FlushZones()
    {
        try
        {
            if (zoneTable == null)
                await SetupZones();
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to set up the task zone aggregate table");
            return; // keep the pending deltas, retried next flush
        }
        var dayBucket = DateTime.UtcNow.Date;
        foreach (var key in pendingZones.Keys.ToList())
        {
            if (!pendingZones.TryRemove(key, out var delta))
                continue;
            TaskZoneAggregateRow row;
            lock (delta.gate)
            {
                row = new TaskZoneAggregateRow
                {
                    DayBucket = dayBucket,
                    TaskName = key.task,
                    Zone = key.zone,
                    InstanceId = instanceId,
                    WSeconds = delta.WSeconds,
                    Value = delta.Value,
                    Periods = delta.Periods,
                    Players = new HashSet<string>(delta.Players)
                };
            }
            try
            {
                var existing = (await zoneTable!.Where(r =>
                    r.DayBucket == dayBucket && r.TaskName == key.task && r.Zone == key.zone && r.InstanceId == instanceId)
                    .ExecuteAsync()).FirstOrDefault();
                if (existing != null)
                {
                    row.WSeconds += existing.WSeconds;
                    row.Value += existing.Value;
                    row.Periods += existing.Periods;
                    if (existing.Players != null)
                        row.Players.UnionWith(existing.Players);
                }
                await zoneTable!.Insert(row).SetTTL(TTL_SECONDS).ExecuteAsync();
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to flush zone aggregate {task}/{zone}", key.task, key.zone);
            }
        }
    }

    /// <summary>Replaces the zone read snapshot, for tests.</summary>
    internal void SetZoneSnapshotForTest(Dictionary<string, Dictionary<string, ZoneAggregate>> s) => zoneSnapshot = s;

    /// <summary>Last merged per task, per zone view; never touches cassandra (see <see cref="GetSnapshot"/>).</summary>
    public Dictionary<string, Dictionary<string, ZoneAggregate>> GetZoneSnapshot() => zoneSnapshot;

    /// <summary>
    /// Keeps every negative entry (costs, bounded by the declared cost tags) plus the
    /// <paramref name="limit"/> highest positive entries.
    /// </summary>
    internal static Dictionary<string, double> KeepCostsAndTop(Dictionary<string, double> counts, int limit)
        => counts.Where(e => e.Value < 0)
            .Concat(counts.Where(e => e.Value >= 0).OrderByDescending(e => e.Value).Take(limit))
            .ToDictionary(e => e.Key, e => e.Value);

    /// <summary>Replaces the read snapshot, for tests.</summary>
    internal void SetSnapshotForTest(Dictionary<(string task, byte bucket), BucketAggregate> s)
    {
        snapshot = s;
        snapshotAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Returns the last snapshot built by the background <see cref="RefreshSnapshot"/>.
    /// This is a non-blocking, allocation-free read: it never touches cassandra, so the
    /// request path (and the fold path) can never stall behind a slow merge. The snapshot
    /// is at most one refresh interval (~60s) stale, which is well within the estimate's
    /// tolerance. Empty until the first background refresh completes, in which case the
    /// estimator simply degrades to the formula tier. The first call kicks a one-off warm
    /// up so a fresh pod does not have to wait for the flusher's initial delay.
    /// </summary>
    public Dictionary<(string task, byte bucket), BucketAggregate> GetSnapshot()
    {
        if (snapshotAt == DateTime.MinValue && System.Threading.Interlocked.Exchange(ref warmupKicked, 1) == 0)
            _ = Task.Run(RefreshSnapshot);
        return snapshot;
    }

    /// <summary>
    /// Merge-read all instances' rows over the last 8 days, applying read time decay,
    /// into the in-memory snapshot. Called only from the background flusher (and once at
    /// warm up) so no caller ever awaits the merge. Concurrent invocations are coalesced.
    /// </summary>
    public async Task RefreshSnapshot()
    {
        if (!await mergeLock.WaitAsync(0))
            return; // a refresh is already running; skip this tick
        try
        {
            if (table == null)
                await Setup();
            var startedAt = DateTime.UtcNow;
            var merged = new Dictionary<(string, byte), BucketAggregate>();
            var now = DateTime.UtcNow.Date;
            for (int dayOffset = 0; dayOffset < 8; dayOffset++)
            {
                var day = now.AddDays(-dayOffset);
                var weight = Math.Pow(0.5, dayOffset / DecayHalfLifeDays);
                var rows = (await table!.Where(r => r.DayBucket == day).SetPageSize(MergePageSize).ExecuteAsync()).ToList();
                foreach (var row in rows)
                {
                    if (TaskDataResets.IsBeforeReset(row.TaskName, row.DayBucket))
                        continue;
                    var key = (row.TaskName, (byte)row.Bucket);
                    if (!merged.TryGetValue(key, out var agg))
                    {
                        agg = new BucketAggregate { TaskName = row.TaskName, Bucket = (byte)row.Bucket };
                        merged[key] = agg;
                    }
                    agg.WSeconds += row.WSeconds * weight;
                    agg.ResidualCoins += row.ResidualCoins * weight;
                    agg.RareCoins += row.RareCoins * weight;
                    agg.WPeriods += row.WPeriods * weight;
                    agg.RefItemValue += row.RefItemValue * weight;
                    if (row.ItemCounts != null)
                        foreach (var (tag, count) in row.ItemCounts)
                            agg.ItemCounts[tag] = agg.ItemCounts.GetValueOrDefault(tag) + count * weight;
                    if (row.Players != null)
                        agg.Contributors.UnionWith(row.Players);
                }
            }
            snapshot = merged;
            snapshotAt = DateTime.UtcNow;
            await RefreshZoneSnapshot();
            var elapsed = DateTime.UtcNow - startedAt;
            if (elapsed > TimeSpan.FromSeconds(10))
                logger.LogWarning("task aggregate merge took {seconds:0.0}s for {keys} task/bucket keys - check task_bucket_aggregates partition size (instanceId churn)", elapsed.TotalSeconds, merged.Count);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to merge task aggregates");
        }
        finally
        {
            mergeLock.Release();
        }
    }

    /// <summary>Merge-reads the zone rows of the last 8 days with the same 7 day half life decay. A failure keeps the previous zone snapshot.</summary>
    private async Task RefreshZoneSnapshot()
    {
        try
        {
            if (zoneTable == null)
                await SetupZones();
            var merged = new Dictionary<string, Dictionary<string, ZoneAggregate>>();
            var now = DateTime.UtcNow.Date;
            for (int dayOffset = 0; dayOffset < 8; dayOffset++)
            {
                var day = now.AddDays(-dayOffset);
                var weight = Math.Pow(0.5, dayOffset / DecayHalfLifeDays);
                var rows = (await zoneTable!.Where(r => r.DayBucket == day).SetPageSize(MergePageSize).ExecuteAsync()).ToList();
                foreach (var row in rows)
                {
                    if (TaskDataResets.IsBeforeReset(row.TaskName, row.DayBucket))
                        continue;
                    if (!merged.TryGetValue(row.TaskName, out var zones))
                        merged[row.TaskName] = zones = new();
                    if (!zones.TryGetValue(row.Zone, out var agg))
                        zones[row.Zone] = agg = new ZoneAggregate { Zone = row.Zone };
                    agg.WSeconds += row.WSeconds * weight;
                    agg.Value += row.Value * weight;
                    if (row.Players != null)
                        agg.Contributors.UnionWith(row.Players);
                }
            }
            zoneSnapshot = merged;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to merge task zone aggregates");
        }
    }

    // ── Per player rolled up stats ──

    public virtual async Task UpsertPlayerStat(TaskPlayerStatRow row)
    {
        if (playerTable == null)
            await Setup();
        await playerTable!.Insert(row).SetTTL(TTL_SECONDS).ExecuteAsync();
    }

    public virtual async Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task)
    {
        if (playerTable == null)
            await Setup();
        return (await playerTable!.Where(r => r.PlayerUuid == playerUuid && r.TaskName == task).ExecuteAsync()).FirstOrDefault();
    }

    public async Task<List<TaskPlayerStatRow>> GetPlayerStats(string playerUuid)
    {
        if (playerTable == null)
            await Setup();
        return (await playerTable!.Where(r => r.PlayerUuid == playerUuid).ExecuteAsync()).ToList();
    }

    /// <summary>
    /// Drops the per player task stats. The day bucket aggregates the player was folded into still
    /// list the uuid in their Players set, but those rows are written with a 14 day TTL and no
    /// further deltas arrive for a deleted player, so the last trace expires on its own.
    /// </summary>
    public async Task DeletePlayerStats(string playerUuid)
    {
        if (playerTable == null)
            await Setup();
        await playerTable!.Where(r => r.PlayerUuid == playerUuid).Delete().ExecuteAsync();
    }

    private async Task SetupZones()
    {
        var zoneMapping = new MappingConfiguration().Define(new Map<TaskZoneAggregateRow>()
            .TableName("task_zone_aggregates")
            .PartitionKey(r => r.DayBucket)
            .ClusteringKey(r => r.TaskName)
            .ClusteringKey(r => r.Zone)
            .ClusteringKey(r => r.InstanceId));
        var created = new Table<TaskZoneAggregateRow>(session, zoneMapping);
        await created.CreateIfNotExistsAsync();
        zoneTable = created;
    }

    private async Task Setup()
    {
        var mapping = new MappingConfiguration().Define(new Map<TaskBucketAggregateRow>()
            .TableName("task_bucket_aggregates")
            .PartitionKey(r => r.DayBucket)
            .ClusteringKey(r => r.TaskName)
            .ClusteringKey(r => r.Bucket)
            .ClusteringKey(r => r.InstanceId));
        table = new Table<TaskBucketAggregateRow>(session, mapping);
        await table.CreateIfNotExistsAsync();

        var playerMapping = new MappingConfiguration().Define(new Map<TaskPlayerStatRow>()
            .TableName("task_player_stats")
            .PartitionKey(r => r.PlayerUuid)
            .ClusteringKey(r => r.TaskName));
        playerTable = new Table<TaskPlayerStatRow>(session, playerMapping);
        await playerTable.CreateIfNotExistsAsync();
    }
}

public class TaskBucketAggregateRow
{
    public DateTime DayBucket { get; set; }
    public string TaskName { get; set; }
    // stored as cassandra int; the driver has no CLR byte mapping
    public int Bucket { get; set; }
    public string InstanceId { get; set; }
    public double WSeconds { get; set; }
    public double ResidualCoins { get; set; }
    public double RareCoins { get; set; }
    public double WPeriods { get; set; }
    public double RefItemValue { get; set; }
    public Dictionary<string, double> ItemCounts { get; set; } = new();
    public HashSet<string> Players { get; set; } = new();
    public List<string> LastFoldTraceIds { get; set; } = new();
}

/// <summary>One instance's contribution to a (day, task, zone) of the "best spot" data.</summary>
public class TaskZoneAggregateRow
{
    public DateTime DayBucket { get; set; }
    public string TaskName { get; set; }
    public string Zone { get; set; }
    public string InstanceId { get; set; }
    public double WSeconds { get; set; }
    public double Value { get; set; }
    public double Periods { get; set; }
    public HashSet<string> Players { get; set; } = new();
}

public class TaskPlayerStatRow
{
    public string PlayerUuid { get; set; }
    public string TaskName { get; set; }
    public double WSeconds { get; set; }
    public double ResidualCoins { get; set; }
    public double RareCoins { get; set; }
    public double RefItemValue { get; set; }
    public Dictionary<string, double> ItemCounts { get; set; } = new();
    /// <summary>decayed cumulative minutes on the task, drives the personal blend ramp</summary>
    public double CumulativeMinutes { get; set; }
    public DateTime LastFold { get; set; }
}
