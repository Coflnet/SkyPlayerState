using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using System.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Coflnet.Sky.PlayerState.Models;

public interface ITransactionService
{
    Task AddTransactions(params Transaction[] transactions);
    Task AddTransactions(IEnumerable<Transaction> transactions);
    Task<IEnumerable<Transaction>> GetTransactions(Guid guid, TimeSpan timeSpan, DateTime end);
    Task<IEnumerable<Transaction>> GetItemTransactions(long itemId, int max);
    Task StoreUuidToItemMapping(List<(Guid, long? Id)> itemUuidAndItemId);
    Task<IEnumerable<long>> GetItemIdsFromUuid(Guid itemId);
}

public interface ICassandraService
{
    Task<ISession> GetSession();
    Task<ISession> GetCassandraSession();
    Table<CassandraItem> GetItemsTable(ISession session);
    Table<CassandraItem> GetSplitItemsTable(ISession session);
}

public class UuidToItemMapping
{
    public Guid ItemUuid { get; set; }
    public long ItemId { get; set; }
}

public class TransactionService : ITransactionService, ICassandraService
{
    ISession _session;
    ISession _oldSession;
    private SemaphoreSlim sessionOpenLock = new SemaphoreSlim(1);
    private IConfiguration config;
    private ILogger<TransactionService> logger;
    private Table<PlayerTransaction> _table;
    private Table<UuidToItemMapping> itemMappingTable;

    public TransactionService(ILogger<TransactionService> logger, IConfiguration config, ISession session)
    {
        this.logger = logger;
        this.config = config;
        this._session = session;

        _table = Create(_session).Result;
    }

    private static Prometheus.Counter insertCount = Prometheus.Metrics.CreateCounter("sky_playerstate_transaction_insert", "How many inserts were made");
    private static Prometheus.Counter insertFailed = Prometheus.Metrics.CreateCounter("sky_playerstate_transaction_insert_fail", "How many inserts failed");


    public async Task AddTransactions(params Transaction[] transactions)
    {
        await AddTransactions(transactions.AsEnumerable());
    }
    public async Task AddTransactions(IEnumerable<Transaction> transactions)
    {
        var session = await GetSession();
        var table = GetPlayerTable(session);
        var itemTable = GetItemTable(session);
        var count = transactions.Count();
        if (count == 0)
            return;
        Console.WriteLine("adding transactions " + count);
        await Task.WhenAll(transactions.GroupBy(t => new { t.PlayerUuid, t.ItemId, t.TimeStamp }).Select(g =>
        {
            if (g.Count() > 1)
            {
                return new Transaction()
                {
                    Amount = g.Sum(t => t.Amount),
                    ItemId = g.Key.ItemId,
                    PlayerUuid = g.Key.PlayerUuid,
                    ProfileUuid = g.First().ProfileUuid,
                    TimeStamp = g.Key.TimeStamp,
                    Type = g.First().Type
                };
            }
            return g.First();
        }).Select(async transaction =>
        {
            var maxTries = 5;
            for (int i = 0; i < maxTries; i++)
                try
                {
                    var statement = table.Insert(new PlayerTransaction(transaction));
                    var itemInsert = session.ExecuteAsync(itemTable.Insert(new ItemTransaction(transaction)));
                    statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
                    await session.ExecuteAsync(statement);
                    await itemInsert;
                    insertCount.Inc();
                    return;
                }
                catch (Exception e)
                {
                    insertFailed.Inc();
                    logger.LogError(e, $"storing {transaction.PlayerUuid} {transaction.TimeStamp} failed {i} times");
                    await Task.Delay(200 * i);
                    if (i >= maxTries - 1)
                        throw e;
                }
        }));
    }


    private async Task<Table<PlayerTransaction>> Create(ISession session)
    {
        var table = GetPlayerTable(session);
        var itemTable = GetItemTable(session);
        var rawitemTable = GetSplitItemsTable(session);
        var mapping = new MappingConfiguration()
            .Define(new Map<UuidToItemMapping>()
            .PartitionKey(t => t.ItemUuid)
            .ClusteringKey(new Tuple<string, SortOrder>("ItemId", SortOrder.Descending)));
        itemMappingTable = new Table<UuidToItemMapping>(session, mapping);

        // drop table
        //session.Execute("DROP TABLE IF EXISTS items");
        await table.CreateIfNotExistsAsync();
        await itemTable.CreateIfNotExistsAsync();
        await rawitemTable.CreateIfNotExistsAsync();
        await itemMappingTable.CreateIfNotExistsAsync();
        return table;
    }

    public static Table<PlayerTransaction> GetPlayerTable(ISession session)
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<PlayerTransaction>()
            .PartitionKey(t => t.PlayerUuid)
            .ClusteringKey(new Tuple<string, SortOrder>("timestamp", SortOrder.Descending), new("itemid", SortOrder.Descending))
        .Column(o => o.Type, c => c.WithName("type").WithDbType<int>()));
        var table = new Table<PlayerTransaction>(session, mapping, "transactions");
        table.SetConsistencyLevel(ConsistencyLevel.Quorum);
        return table;
    }

    public Table<CassandraItem> GetItemsTable(ISession session)
    {
        return new Table<CassandraItem>(session, new MappingConfiguration()
            .Define(new Map<CassandraItem>()
            .PartitionKey(t => t.Tag)
            .Column(o => o.Id, c => c.WithSecondaryIndex())
            .Column(o => o.Enchantments, c => c.WithDbType<Dictionary<string, int>>())
            .ClusteringKey(new Tuple<string, SortOrder>("ItemId", SortOrder.Ascending), new Tuple<string, SortOrder>("Id", SortOrder.Descending))), "items");
    }

    public Table<CassandraItem> GetSplitItemsTable(ISession session)
    {
        return new Table<CassandraItem>(session, new MappingConfiguration()
            .Define(new Map<CassandraItem>()
            .PartitionKey(t => t.Tag, t => t.ItemId)
            .Column(o => o.Id, c => c.WithSecondaryIndex())
            .Column(o => o.Enchantments, c => c.WithDbType<Dictionary<string, int>>())
            .ClusteringKey(new Tuple<string, SortOrder>("Id", SortOrder.Descending))), "split_items");
    }

    public static Table<ItemTransaction> GetItemTable(ISession session)
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<ItemTransaction>()
            .PartitionKey(t => t.ItemId)
            .ClusteringKey(new Tuple<string, SortOrder>("timestamp", SortOrder.Ascending))
        .Column(o => o.Type, c => c.WithName("type").WithDbType<int>()));
        var table = new Table<ItemTransaction>(session, mapping, "itemTransaction");
        table.SetConsistencyLevel(ConsistencyLevel.Quorum);
        return table;
    }


    public async Task<ISession> GetSession()
    {
        if (_session != null)
            return _session;
        throw new Exception("got moved to DI, migrate this");
    }

    public async Task<ISession> GetCassandraSession()
    {
        if (_oldSession != null)
            return _oldSession;
        await sessionOpenLock.WaitAsync();
        if (_oldSession != null)
            return _oldSession;
        try
        {
            var builder = Cluster.Builder()
                                .WithCredentials(config["CASSANDRA:USER"], config["CASSANDRA:PASSWORD"])
                                .AddContactPoints(config["CASSANDRA:HOSTS"].Split(","))
                                .WithDefaultKeyspace(config["CASSANDRA:KEYSPACE"]);
            var certificatePaths = config["CASSANDRA:X509Certificate_PATHS"];
            if (!string.IsNullOrEmpty(certificatePaths))
            {
                var sslOptions = new SSLOptions(
                    // TLSv1.2 is required as of October 9, 2019.
                    // See: https://www.instaclustr.com/removing-support-for-outdated-encryption-mechanisms/
                    SslProtocols.Tls12,
                    false,
                    // Custom validator avoids need to trust the CA system-wide.
                    (sender, certificate, chain, errors) => true
                ).SetCertificateCollection(new(certificatePaths.Split(',').Select(p => new X509Certificate2(p)).ToArray()));
                builder.WithSSL();
            }
            var cluster = builder.Build();
            cluster.ConnectAndCreateDefaultKeyspaceIfNotExists(new Dictionary<string, string>()
            {
                {"class", config["CASSANDRA:REPLICATION_CLASS"]},
                {"replication_factor", config["CASSANDRA:REPLICATION_FACTOR"]}
            });
            _oldSession = await cluster.ConnectAsync(config["CASSANDRA:KEYSPACE"]);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to connect to cassandra");
            throw;
        }
        finally
        {
            sessionOpenLock.Release();
        }
        return _oldSession;
    }

    private async Task<Table<PlayerTransaction>> GetPlayerTable()
    {
        if (_table != null)
            return _table;
        await GetSession();
        return _table;
    }

    public async Task<IEnumerable<Transaction>> GetTransactions(Guid guid, TimeSpan timeSpan, DateTime end)
    {
        var table = await GetPlayerTable();
        var start = end - timeSpan;
        return await table.Where(t => t.PlayerUuid == guid && t.TimeStamp > start && t.TimeStamp <= end).ExecuteAsync();
    }

    public async Task<IEnumerable<Transaction>> GetItemTransactions(long itemId, int max)
    {
        var table = GetItemTable(await GetSession());
        return await table.Where(t => t.ItemId == itemId).Take(max).ExecuteAsync();
    }

    public Task<IEnumerable<Transaction>> GetTradeTransactions(string itemTag, Guid itemId, DateTime end)
    {
        throw new NotImplementedException();
    }

    public async Task StoreUuidToItemMapping(List<(Guid, long? Id)> itemUuidAndItemId)
    {
        var table = itemMappingTable;
        logger.LogInformation($"storing uuid mapping {itemUuidAndItemId.Count}");
        foreach (var item in itemUuidAndItemId)
        {
            if (item.Id == null)
                continue;
            var insert = table.Insert(new UuidToItemMapping()
            {
                ItemUuid = item.Item1,
                ItemId = item.Id.Value
            });
            insert.SetTTL(60 * 60 * 24 * 30);
            await insert.ExecuteAsync();
            logger.LogInformation($"stored uuid mapping {item.Item1} -> {item.Id}");
        }
    }

    public async Task<IEnumerable<long>> GetItemIdsFromUuid(Guid itemId)
    {
        return await itemMappingTable.Where(t => t.ItemUuid == itemId).Select(t => t.ItemId).ExecuteAsync();
    }
}
#nullable restore