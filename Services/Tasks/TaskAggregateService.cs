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
    private readonly ISession session;
    private readonly ILogger<TaskAggregateService> logger;
    private readonly string instanceId = Environment.MachineName;
    private const int TTL_SECONDS = 1209600; // 14 days
    private const double DecayHalfLifeDays = 7;

    // pending deltas accumulated in memory, flushed to cassandra periodically
    private readonly ConcurrentDictionary<(string task, byte bucket), PendingDelta> pending = new();
    private readonly SemaphoreSlim flushLock = new(1, 1);

    // merged read snapshot, refreshed periodically
    private Dictionary<(string task, byte bucket), BucketAggregate> snapshot = new();
    private DateTime snapshotAt = DateTime.MinValue;
    private readonly SemaphoreSlim mergeLock = new(1, 1);
    private static readonly TimeSpan MergeInterval = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Flush accumulated in-memory deltas to this instance's cassandra rows.
    /// Called on a timer (every ~60s).
    /// </summary>
    public async Task Flush()
    {
        if (pending.IsEmpty)
            return;
        await flushLock.WaitAsync();
        try
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
                    // keep only the top 12 items to bound row size
                    if (row.ItemCounts.Count > 12)
                        row.ItemCounts = row.ItemCounts.OrderByDescending(e => e.Value).Take(12).ToDictionary(e => e.Key, e => e.Value);
                    await table!.Insert(row).SetTTL(TTL_SECONDS).ExecuteAsync();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "failed to flush aggregate {task}/{bucket}", key.task, key.bucket);
                }
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    /// <summary>
    /// Merge-read all instances' rows over the last 8 days, applying read time decay,
    /// into the in-memory snapshot. Called on a timer and lazily when stale.
    /// </summary>
    public async Task<Dictionary<(string task, byte bucket), BucketAggregate>> GetSnapshot()
    {
        if (DateTime.UtcNow - snapshotAt < MergeInterval)
            return snapshot;
        await mergeLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - snapshotAt < MergeInterval)
                return snapshot;
            if (table == null)
                await Setup();
            var merged = new Dictionary<(string, byte), BucketAggregate>();
            var now = DateTime.UtcNow.Date;
            for (int dayOffset = 0; dayOffset < 8; dayOffset++)
            {
                var day = now.AddDays(-dayOffset);
                var weight = Math.Pow(0.5, dayOffset / DecayHalfLifeDays);
                var rows = (await table!.Where(r => r.DayBucket == day).ExecuteAsync()).ToList();
                foreach (var row in rows)
                {
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
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to merge task aggregates");
        }
        finally
        {
            mergeLock.Release();
        }
        return snapshot;
    }

    // ── Per player rolled up stats ──

    public async Task UpsertPlayerStat(TaskPlayerStatRow row)
    {
        if (playerTable == null)
            await Setup();
        await playerTable!.Insert(row).SetTTL(TTL_SECONDS).ExecuteAsync();
    }

    public async Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task)
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
