using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using MessagePack;
using Microsoft.Extensions.Logging;
using Cassandra.Mapping.Attributes;
using StackExchange.Redis;
using PartitionKeyAttribute = Cassandra.Mapping.Attributes.PartitionKeyAttribute;

namespace Coflnet.Sky.PlayerState.Models;

public interface IPersistenceService
{
    Task<StateObject> GetStateObject(string playerId);
    Task SaveStateObject(StateObject stateObject);
    Task ForceSave(StateObject stateObject);
}

public class PersistenceService : IPersistenceService
{
    ICassandraService cassandraService;
    private ILogger<PersistenceService> logger;
    private readonly IConnectionMultiplexer redis;
    private static readonly Prometheus.Counter stateSaveCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_state_save_total",
        "Total number of player state saves.");
    private static readonly Prometheus.Counter stateSaveSkippedUnchangedCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_state_save_skipped_unchanged_total",
        "Total number of skipped state saves because nothing changed.");
    private static readonly Prometheus.Counter stateSaveSkippedAnonymousCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_state_save_skipped_anonymous_total",
        "Total number of skipped persistence operations for anonymous/unidentified players.");
    private static readonly Prometheus.Counter redisSyncCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_redis_sync_total",
        "Total number of player states synced to redis for near-real-time reads.");
    private static readonly Prometheus.Counter redisHitCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_redis_read_total",
        "Player state reads served, labelled by source (redis hit vs cassandra fallback).",
        new Prometheus.CounterConfiguration { LabelNames = new[] { "source" } });
    private ConcurrentDictionary<string, DateTime> lastSaveLock = new();
    private ConcurrentDictionary<string, byte[]> savedHashList = new();
    private ConcurrentDictionary<string, Task> saveTasks = new();
    // independent of the 8s cassandra throttle: bounds how often we (re)serialize and push to
    // redis per player. There are hundreds of updates/s overall, so without this we'd burn cpu
    // serializing and saturate redis network for no extra freshness.
    private ConcurrentDictionary<string, DateTime> redisNextSync = new();
    private static readonly TimeSpan redisSyncThrottle = TimeSpan.FromSeconds(1);
    // kept short on purpose: cassandra is the durable store and catches up within saveThrottle (8s),
    // so redis only has to bridge that gap. A small ttl keeps redis ram bounded to recently-active
    // players instead of accumulating every player that was ever touched.
    private static readonly TimeSpan redisTtl = TimeSpan.FromSeconds(60);
    private Table<Inventory> _table;
    SemaphoreSlim tableLock = new(1);

    public PersistenceService(ICassandraService cassandraService, ILogger<PersistenceService> logger, IConnectionMultiplexer redis)
    {
        this.cassandraService = cassandraService;
        this.logger = logger;
        this.redis = redis;
    }

    private static RedisKey RedisKeyFor(string playerId) => "pstate:" + playerId;

    private async Task<Table<Inventory>> GetPlayerTable()
    {
        if (_table != null)
        {
            return _table;
        }
        logger.LogInformation("Constructing state table access");
        await tableLock.WaitAsync();
        if (_table != null)
        {
            return _table;
        }
        var mapping = new MappingConfiguration()
            .Define(new Map<Inventory>()
            .PartitionKey(t => t.PlayerId)
        );
        var table = new Table<Inventory>(await cassandraService.GetSession(), mapping, "skyPlayerState");
        table.SetConsistencyLevel(ConsistencyLevel.Quorum);
        logger.LogInformation("Creating table if not exists");
        await table.CreateIfNotExistsAsync();
        _table = table;
        tableLock.Release();
        return table;
    }

    public async Task<StateObject> GetStateObject(string playerId)
    {
        // Anonymous states are never persisted, so there is nothing to load - hand back a fresh state.
        if (StateObject.IsAnonymous(playerId))
            return new StateObject() { PlayerId = playerId };
        // Redis-first: the pod owning this player's kafka partition pushes its live state here
        // within the last second, so this serves near-real-time inventory from any pod without
        // waiting on the 8s cassandra save. Cassandra remains the durable fallback below.
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(RedisKeyFor(playerId));
            if (cached.HasValue)
            {
                redisHitCount.WithLabels("redis").Inc();
                return new Inventory() { PlayerId = playerId, Serialized = cached }.GetStateObject();
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to read state from redis for {playerId}, falling back to cassandra", playerId);
        }
        redisHitCount.WithLabels("cassandra").Inc();
        var table = await GetPlayerTable();
        var result = await table.Where(t => t.PlayerId == playerId).First().ExecuteAsync();
        if (result == null)
        {
            return new StateObject() { PlayerId = playerId };
        }
        savedHashList[playerId] = GetHash(result);
        logger.LogInformation("Loaded state object for player {playerId} size:{size}", playerId, result.Serialized.Length);
        return result.GetStateObject();
    }

    /// <summary>
    /// Pushes the current state to redis for near-real-time reads, throttled to once per second
    /// per player and independent of the (8s) cassandra throttle. Best-effort: any failure is
    /// logged and swallowed so it can never fail or stall the update pipeline.
    /// </summary>
    private async Task SyncToRedis(StateObject stateObject)
    {
        if (StateObject.IsAnonymous(stateObject.PlayerId))
            return;
        var now = DateTime.UtcNow;
        if (redisNextSync.TryGetValue(stateObject.PlayerId, out var next) && now < next)
            return;
        // reserve the slot before doing the work so concurrent updates for the same player don't
        // all serialize; on failure we still back off a second rather than hammering redis.
        redisNextSync[stateObject.PlayerId] = now + redisSyncThrottle;
        try
        {
            var serialized = new Inventory(stateObject).Serialized;
            await redis.GetDatabase().StringSetAsync(RedisKeyFor(stateObject.PlayerId), serialized, redisTtl);
            redisSyncCount.Inc();
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to sync state to redis for {playerId}", stateObject.PlayerId);
        }
    }

    public async Task SaveStateObject(StateObject stateObject)
    {
        await SaveStateObject(stateObject, false);
    }

    private static readonly TimeSpan saveThrottle = TimeSpan.FromSeconds(8);

    public async Task SaveStateObject(StateObject stateObject, bool recursive)
    {
        // Anonymous/unidentified states have no valid partition key (and would all collide on one
        // shared bucket). Skip every persistence path for them, including the redis mirror below.
        if (StateObject.IsAnonymous(stateObject.PlayerId))
        {
            stateSaveSkippedAnonymousCount.Inc();
            return;
        }
        // Near-real-time mirror to redis (own 1s throttle), independent of the 8s cassandra save
        // below. Driven off the same per-update call site so every processed update keeps redis warm.
        if (!recursive)
            await SyncToRedis(stateObject);
        // Throttle BEFORE serializing. Building the Inventory deep-copies the whole state,
        // MessagePack serializes + LZ4 compresses and SHA256-hashes it, which is expensive
        // to run on every single update. An actual save happens at most once per
        // saveThrottle per player, so if one ran recently (or is pending) just make sure a
        // delayed save is queued (it serializes the latest state when it fires) and bail.
        if (!recursive && lastSaveLock.ContainsKey(stateObject.PlayerId))
        {
            QueueDelayedSave(stateObject);
            return;
        }
        var table = await GetPlayerTable();
        var inventory = new Inventory(stateObject);
        byte[] hash;
        hash = GetHash(inventory);
        if (DidNothingChange(stateObject, hash))
        {
            stateSaveSkippedUnchangedCount.Inc();
            return;
        }
        // allow only one save every saveThrottle
        // start new thread to wait if necessary
        // skip if more than one thread is waiting
        if (!lastSaveLock.TryAdd(stateObject.PlayerId, (DateTime.Now + saveThrottle)))
        {
            if (recursive)
                return;
            QueueDelayedSave(stateObject);
            return;
        }
        try
        {
            await table.Insert(inventory).ExecuteAsync().ConfigureAwait(false);
            stateSaveCount.Inc();
            logger.LogInformation("Saved state object for player {playerId}", stateObject.PlayerId);
            savedHashList[stateObject.PlayerId] = hash;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to save state object for {playerId}", stateObject.PlayerId);
        }
    }

    /// <summary>
    /// Persists immediately, bypassing the 8s throttle and the delayed-save queue. Used for graceful
    /// shutdown so in-memory state that hasn't reached its save window yet isn't lost on a planned
    /// restart. Still skips the write when nothing changed since the last save.
    /// </summary>
    public async Task ForceSave(StateObject stateObject)
    {
        if (StateObject.IsAnonymous(stateObject.PlayerId))
        {
            stateSaveSkippedAnonymousCount.Inc();
            return;
        }
        var table = await GetPlayerTable();
        var inventory = new Inventory(stateObject);
        var hash = GetHash(inventory);
        if (DidNothingChange(stateObject, hash))
        {
            stateSaveSkippedUnchangedCount.Inc();
            return;
        }
        await table.Insert(inventory).ExecuteAsync().ConfigureAwait(false);
        stateSaveCount.Inc();
        savedHashList[stateObject.PlayerId] = hash;
    }

    private void QueueDelayedSave(StateObject stateObject)
    {
        _ = saveTasks.AddOrUpdate(stateObject.PlayerId, (key) => Task.Run(async () =>
        {
            await Task.Delay(saveThrottle);
            lastSaveLock.TryRemove(stateObject.PlayerId, out var _);
            await SaveStateObject(stateObject, true);
            saveTasks.TryRemove(stateObject.PlayerId, out var _);
            await Task.Delay(saveThrottle);
            if (lastSaveLock.TryRemove(stateObject.PlayerId, out var _))
            {
                logger.LogDebug("Removed lock for {playerId}", stateObject.PlayerId);
            }
        }), (key, value) => value);
    }

    private static byte[] GetHash(Inventory inventory)
    {
        byte[] hash;
        using (SHA256 sha256Hash = SHA256.Create())
        {
            hash = sha256Hash.ComputeHash(inventory.Serialized);

        }

        return hash;
    }

    private bool DidNothingChange(StateObject stateObject, byte[] hash)
    {
        return savedHashList.TryGetValue(stateObject.PlayerId, out var lastHash) && Enumerable.SequenceEqual(lastHash, hash);
    }
}

public class Inventory
{
    [PartitionKey]
    public string PlayerId { get; set; }
    [Frozen]
    public byte[] Serialized { get; set; }
    static MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

    public Inventory(StateObject stateObject)
    {
        PlayerId = stateObject.PlayerId;
        var copy = new StateObject(stateObject);

        Serialized = MessagePackSerializer.Serialize(copy, options);
    }

    public Inventory()
    {
    }

    public StateObject GetStateObject()
    {
        return MessagePackSerializer.Deserialize<StateObject>(Serialized, options);
    }
}
#nullable restore