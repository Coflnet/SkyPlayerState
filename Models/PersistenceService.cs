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
using PartitionKeyAttribute = Cassandra.Mapping.Attributes.PartitionKeyAttribute;

namespace Coflnet.Sky.PlayerState.Models;

public interface IPersistenceService
{
    Task<StateObject> GetStateObject(string playerId);
    Task SaveStateObject(StateObject stateObject);
}

public class PersistenceService : IPersistenceService
{
    ICassandraService cassandraService;
    private ILogger<PersistenceService> logger;
    private ConcurrentDictionary<string, DateTime> lastSaveLock = new();
    private ConcurrentDictionary<string, byte[]> savedHashList = new();
    private ConcurrentDictionary<string, Task> saveTasks = new();
    private Table<Inventory> _table;
    SemaphoreSlim tableLock = new(1);

    public PersistenceService(ICassandraService cassandraService, ILogger<PersistenceService> logger)
    {
        this.cassandraService = cassandraService;
        this.logger = logger;
    }

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

    public async Task SaveStateObject(StateObject stateObject)
    {
        await SaveStateObject(stateObject, false);
    }

    public async Task SaveStateObject(StateObject stateObject, bool recursive)
    {
        var table = await GetPlayerTable();
        var inventory = new Inventory(stateObject);
        byte[] hash;
        hash = GetHash(inventory);
        if (DidNothingChange(stateObject, hash))
        {
            Console.WriteLine($"\nNothing changed for {stateObject.PlayerId}, skipping save.");
            return;
        }
        // allow only one save every 5 seconds
        // start new thread to wait if necessary
        // skip if more than one thread is waiting
        var waitTime = TimeSpan.FromSeconds(8);
        if (!lastSaveLock.TryAdd(stateObject.PlayerId, (DateTime.Now + waitTime)))
        {
            if (recursive)
                return;
            _ = saveTasks.AddOrUpdate(stateObject.PlayerId, (key) => Task.Run(async () =>
            {
                await Task.Delay(waitTime);
                lastSaveLock.TryRemove(stateObject.PlayerId, out var _);
                await SaveStateObject(stateObject, true);
                saveTasks.TryRemove(stateObject.PlayerId, out var _);
                await Task.Delay(waitTime);
                if (lastSaveLock.TryRemove(stateObject.PlayerId, out var _))
                {
                    logger.LogDebug("Removed lock for {playerId}", stateObject.PlayerId);
                }
            }), (key, value) => value);
            return;
        }
        try
        {
            await table.Insert(inventory).ExecuteAsync().ConfigureAwait(false);
            logger.LogInformation("Saved state object for player {playerId}", stateObject.PlayerId);
            savedHashList[stateObject.PlayerId] = hash;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to save state object for {playerId}", stateObject.PlayerId);
        }
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