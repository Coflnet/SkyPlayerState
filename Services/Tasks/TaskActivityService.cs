using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Tracks which players are currently doing which task across all instances.
/// Each instance only writes its own partition's players (kafka partitioning makes
/// every player single writer), all instances read the shared redis sorted sets,
/// so every instance sees the same counts.
/// </summary>
public class TaskActivityService
{
    private readonly IConnectionMultiplexer redis;
    private readonly TaskRegistry registry;
    private readonly ILogger<TaskActivityService> logger;
    private Dictionary<string, int> cachedCounts = new();
    private DateTime countsFetchedAt = DateTime.MinValue;
    private readonly System.Threading.SemaphoreSlim countsLock = new(1, 1);
    /// <summary>How long after the last matching signal a player still counts as doing a task.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CountsCacheDuration = TimeSpan.FromSeconds(30);
    private const string KeyPrefix = "task:doers:";
    private const string RingPrefix = "task:doerring:";
    private const string RingLockKey = "task:doerring:lock";
    /// <summary>one ring slot per minute, 31 slots covers the 20 minute delta plus slack</summary>
    private const int RingSlots = 31;

    public TaskActivityService(IConnectionMultiplexer redis, TaskRegistry registry, ILogger<TaskActivityService> logger)
    {
        this.redis = redis;
        this.registry = registry;
        this.logger = logger;
    }

    /// <summary>
    /// Record that a player is currently doing a task. Fire and forget from the update path.
    /// </summary>
    public async Task MarkDoing(string taskName, string playerUuid)
    {
        try
        {
            var db = redis.GetDatabase();
            var key = KeyPrefix + taskName;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SortedSetAddAsync(key, playerUuid, now, flags: CommandFlags.FireAndForget);
            // prune long gone entries and keep the key from living forever if the task dies
            await db.SortedSetRemoveRangeByScoreAsync(key, 0, now - 3600, flags: CommandFlags.FireAndForget);
            await db.KeyExpireAsync(key, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to mark {player} doing {task}", playerUuid, taskName);
        }
    }

    /// <summary>
    /// Current doer count per task, merged across instances, cached for 30 seconds.
    /// </summary>
    public async Task<Dictionary<string, int>> GetCounts()
    {
        if (DateTime.UtcNow - countsFetchedAt < CountsCacheDuration)
            return cachedCounts;
        await countsLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - countsFetchedAt < CountsCacheDuration)
                return cachedCounts;
            var db = redis.GetDatabase();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cutoff = now - (long)ActiveWindow.TotalSeconds;
            var names = registry.MethodTasks.Select(t => t.GetDetectionSignature().MethodName).Distinct().ToList();
            var tasks = names.Select(async name =>
                (name, count: (int)await db.SortedSetLengthAsync(KeyPrefix + name, cutoff, now))).ToList();
            await Task.WhenAll(tasks);
            cachedCounts = tasks.ToDictionary(t => t.Result.name, t => t.Result.count);
            countsFetchedAt = DateTime.UtcNow;
            await SampleRing(db, cachedCounts);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to load task doer counts");
        }
        finally
        {
            countsLock.Release();
        }
        return cachedCounts;
    }

    /// <summary>
    /// Players currently doing a task (for the roster endpoint).
    /// </summary>
    public async Task<List<string>> GetDoers(string taskName)
    {
        var db = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cutoff = now - (long)ActiveWindow.TotalSeconds;
        var entries = await db.SortedSetRangeByScoreAsync(KeyPrefix + taskName, cutoff, now);
        return entries.Select(e => (string)e).ToList();
    }

    /// <summary>
    /// Change in doer count over the last 20 minutes per task,
    /// from the shared minute sampled ring.
    /// </summary>
    public async Task<Dictionary<string, int>> GetChange20m()
    {
        var counts = await GetCounts();
        var db = redis.GetDatabase();
        var result = new Dictionary<string, int>();
        foreach (var (name, current) in counts)
        {
            try
            {
                var ring = await db.ListRangeAsync(RingPrefix + name, 19, 20);
                if (ring.Length > 0 && ring[0].TryParse(out int past))
                    result[name] = current - past;
                else
                    result[name] = 0;
            }
            catch
            {
                result[name] = 0;
            }
        }
        return result;
    }

    /// <summary>
    /// Push the merged counts into the shared per task minute rings. A redis lock makes sure
    /// only one instance samples per minute so slots stay one minute apart.
    /// </summary>
    private async Task SampleRing(IDatabase db, Dictionary<string, int> counts)
    {
        try
        {
            var acquired = await db.StringSetAsync(RingLockKey, Environment.MachineName, TimeSpan.FromSeconds(59), When.NotExists);
            if (!acquired)
                return;
            foreach (var (name, count) in counts)
            {
                var key = RingPrefix + name;
                await db.ListLeftPushAsync(key, count, flags: CommandFlags.FireAndForget);
                await db.ListTrimAsync(key, 0, RingSlots - 1, flags: CommandFlags.FireAndForget);
                await db.KeyExpireAsync(key, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to sample doer count ring");
        }
    }
}
