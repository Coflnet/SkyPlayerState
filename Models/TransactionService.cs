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
    Task<Dictionary<Guid, long[]>> GetItemIdsFromUuids(List<Guid> itemIds);
    Task DeletePlayerTransactions(Guid playerUuid);
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
    ISession? _oldSession;
    private readonly SemaphoreSlim sessionOpenLock = new SemaphoreSlim(1);
    private readonly IConfiguration config;
    private readonly ILogger<TransactionService> logger;
    private readonly Table<PlayerTransaction> _table;
    private readonly Table<UuidToItemMapping> itemMappingTable;

    public TransactionService(ILogger<TransactionService> logger, IConfiguration config, ISession session)
    {
        this.logger = logger;
        this.config = config;
        this._session = session;
        _table = GetPlayerTable(_session);
        itemMappingTable = GetItemMappingTable(_session);

        EnsureSchemaCreated(_session).GetAwaiter().GetResult();
    }

    private static Prometheus.Counter insertCount = Prometheus.Metrics.CreateCounter("sky_playerstate_transaction_insert", "How many inserts were made");
    private static Prometheus.Counter insertFailed = Prometheus.Metrics.CreateCounter("sky_playerstate_transaction_insert_fail", "How many inserts failed");

    internal static bool ShouldStoreInItemTransactions(Transaction transaction)
    {
        // ItemId <= 0 means the item name was never resolved; it would pile up in a junk partition
        return transaction.ItemId > 0 && transaction.ItemId != SpecialTransactionItemIds.Coins;
    }

    internal static List<Transaction> NormalizeTransactionsForStorage(IEnumerable<Transaction> transactions)
    {
        return transactions
            .GroupBy(t => new { t.PlayerUuid, t.ItemId, t.TimeStamp })
            .Select(group =>
            {
                var groupedTransactions = group.ToList();
                if (groupedTransactions.Count == 1)
                    return groupedTransactions[0];

                var first = groupedTransactions[0];
                return new Transaction()
                {
                    Amount = groupedTransactions.Sum(t => t.Amount),
                    ItemId = group.Key.ItemId,
                    PlayerUuid = group.Key.PlayerUuid,
                    ProfileUuid = first.ProfileUuid,
                    TimeStamp = group.Key.TimeStamp,
                    Type = first.Type
                };
            })
            .ToList();
    }


    public async Task AddTransactions(params Transaction[] transactions)
    {
        await AddTransactions(transactions.AsEnumerable());
    }
    public async Task AddTransactions(IEnumerable<Transaction> transactions)
    {
        var session = await GetSession();
        var table = GetPlayerTable(session);
        var itemTable = GetItemTable(session);
        var normalizedTransactions = NormalizeTransactionsForStorage(transactions);
        var count = normalizedTransactions.Count;
        if (count == 0)
            return;
        logger.LogInformation("adding transactions {count}", count);
        await Task.WhenAll(normalizedTransactions.Select(async transaction =>
        {
            var maxTries = 5;
            for (int i = 0; i < maxTries; i++)
                try
                {
                    var statement = table.Insert(new PlayerTransaction(transaction));
                    statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
                    var playerInsert = session.ExecuteAsync(statement);
                    var itemInsert = Task.CompletedTask;
                    if (ShouldStoreInItemTransactions(transaction))
                    {
                        itemInsert = session.ExecuteAsync(itemTable.Insert(new ItemTransaction(transaction)));
                    }

                    await Task.WhenAll(playerInsert, itemInsert);
                    insertCount.Inc();
                    if (ShouldStoreInItemTransactions(transaction))
                        MaybePruneItemTransactions(session, transaction);
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


    private async Task EnsureSchemaCreated(ISession session)
    {
        var itemTable = GetItemTable(session);
        var rawitemTable = GetSplitItemsTable(session);

        // drop table
        //session.Execute("DROP TABLE IF EXISTS items");
        await CreateTableIfMissing(session, "transactions", () => _table.CreateIfNotExistsAsync());
        await CreateTableIfMissing(session, "itemtransaction", () => itemTable.CreateIfNotExistsAsync());
        await CreateTableIfMissing(session, "split_items", () => rawitemTable.CreateIfNotExistsAsync());
        await CreateTableIfMissing(session, "uuidtoitemmapping", () => itemMappingTable.CreateIfNotExistsAsync());
    }

    private async Task CreateTableIfMissing(ISession session, string tableName, Func<Task> create)
    {
        if (await TableExists(session, tableName))
        {
            logger.LogDebug("Skipping startup migration for table {TableName}; schema already exists.", tableName);
            return;
        }

        try
        {
            await create();
            logger.LogInformation("Created table {TableName} during startup migration.", tableName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Startup migration for table {TableName} failed; continuing without applying it.", tableName);
        }
    }

    private static async Task<bool> TableExists(ISession session, string tableName)
    {
        var result = await session.ExecuteAsync(new SimpleStatement(
            "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ?",
            session.Keyspace,
            tableName.ToLowerInvariant()));
        return result.Any();
    }

    private static Table<UuidToItemMapping> GetItemMappingTable(ISession session)
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<UuidToItemMapping>()
            .PartitionKey(t => t.ItemUuid)
            .ClusteringKey(new Tuple<string, SortOrder>("ItemId", SortOrder.Descending)));
        return new Table<UuidToItemMapping>(session, mapping, "uuidtoitemmapping");
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
        return _table;
    }

    public async Task<IEnumerable<Transaction>> GetTransactions(Guid guid, TimeSpan timeSpan, DateTime end)
    {
        var table = await GetPlayerTable();
        var start = end - timeSpan;
        return await table.Where(t => t.PlayerUuid == guid && t.TimeStamp > start && t.TimeStamp <= end).ExecuteAsync();
    }

    /// <summary>
    /// Drops the transaction history of a player. The per-item copies in `itemTransaction` are keyed
    /// by item and carry no player reference, so they are left alone.
    /// </summary>
    public async Task DeletePlayerTransactions(Guid playerUuid)
    {
        var table = await GetPlayerTable();
        await table.Where(t => t.PlayerUuid == playerUuid).Delete().ExecuteAsync();
    }

    /// <summary>
    /// Upper bound of rows read from one itemtransaction partition. Take(0) emits no LIMIT in the
    /// DataStax LINQ provider, so max=0 read the whole partition; hot partitions (e.g. itemid 0)
    /// hold hundreds of thousands of rows and took minutes.
    /// </summary>
    public const int MaxItemTransactions = 1000;

    /// <summary>
    /// Clamps a requested row count to 1..<see cref="MaxItemTransactions"/>; zero, negative and too large values become the cap.
    /// </summary>
    public static int ClampItemTransactionLimit(int max)
    {
        return max <= 0 || max > MaxItemTransactions ? MaxItemTransactions : max;
    }

    /// <summary>
    /// Builds the item history query: newest first (clustering column DESC) with a LIMIT.
    /// </summary>
    public static CqlQuery<ItemTransaction> BuildNewestItemTransactionsQuery(Table<ItemTransaction> table, long itemId, int limit)
    {
        return table.Where(t => t.ItemId == itemId).OrderByDescending(t => t.TimeStamp).Take(limit);
    }

    /// <summary>
    /// Returns the item history, newest first, at most <see cref="MaxItemTransactions"/> rows.
    /// </summary>
    public async Task<IEnumerable<Transaction>> GetItemTransactions(long itemId, int max)
    {
        var table = GetItemTable(await GetSession());
        return await BuildNewestItemTransactionsQuery(table, itemId, ClampItemTransactionLimit(max)).ExecuteAsync();
    }

    /// <summary>Default number of newest rows kept per item instance partition (config ITEM_TRANSACTIONS:KEEP, 0 disables pruning).</summary>
    public const int DefaultItemTransactionsKeep = MaxItemTransactions;
    /// <summary>Default number of newest rows kept per bazaar product partition (config ITEM_TRANSACTIONS:KEEP_BAZAAR, 0 disables pruning).</summary>
    public const int DefaultBazaarTransactionsKeep = 100_000;
    /// <summary>Minimum sampling rate: on average one in this many item writes triggers a prune check.</summary>
    public const int PruneSampleRate = 50;
    private const int PruneReadPageSize = 5000;

    private static readonly SemaphoreSlim pruneLock = new SemaphoreSlim(1);
    private static Prometheus.Counter pruneDeletes = Prometheus.Metrics.CreateCounter("sky_playerstate_item_transaction_prune", "Range deletes issued to trim item transaction partitions");
    private static Prometheus.Counter pruneFailed = Prometheus.Metrics.CreateCounter("sky_playerstate_item_transaction_prune_fail", "Failed item transaction prune attempts");

    /// <summary>
    /// True for transactions carrying the BAZAAR flag (BazaarBuy, BazaarSell, BazaarListBuy, BazaarListSell).
    /// </summary>
    public static bool IsBazaarTransaction(Transaction transaction)
    {
        return (transaction.Type & Transaction.TransactionType.BAZAAR) != 0;
    }

    /// <summary>
    /// Number of rows to keep for the partition a transaction was written to; 0 disables pruning.
    /// </summary>
    public static int GetKeepFor(Transaction transaction, int? keepConfig, int? keepBazaarConfig)
    {
        return IsBazaarTransaction(transaction)
            ? keepBazaarConfig ?? DefaultBazaarTransactionsKeep
            : keepConfig ?? DefaultItemTransactionsKeep;
    }

    /// <summary>
    /// Sampling rate scaled with keep so large partitions are not re-read constantly (about 5% overflow per prune).
    /// </summary>
    public static int GetPruneSampleRate(int keep)
    {
        return Math.Max(PruneSampleRate, keep / 20);
    }

    /// <summary>
    /// Sampling decision for pruning; <paramref name="roll"/> is a random value in [0, sampleRate).
    /// </summary>
    public static bool ShouldPrune(int keep, int roll)
    {
        return keep > 0 && roll == 0;
    }

    /// <summary>
    /// Given the newest-first timestamps read with LIMIT keep+1, returns the timestamp up to (inclusive)
    /// which rows must be deleted, or null when the partition holds at most <paramref name="keep"/> rows.
    /// </summary>
    public static DateTime? GetPruneCutoff(IReadOnlyList<DateTime> newestFirst, int keep)
    {
        if (keep <= 0 || newestFirst.Count <= keep)
            return null;
        return newestFirst[keep];
    }

    private void MaybePruneItemTransactions(ISession session, Transaction transaction)
    {
        var keepConfig = config.GetValue<int?>("ITEM_TRANSACTIONS:KEEP");
        var keepBazaarConfig = config.GetValue<int?>("ITEM_TRANSACTIONS:KEEP_BAZAAR");
        var keep = GetKeepFor(transaction, keepConfig, keepBazaarConfig);
        var triggerIsBazaar = IsBazaarTransaction(transaction);
        var keepBazaar = keepBazaarConfig ?? DefaultBazaarTransactionsKeep;
        var keepItem = keepConfig ?? DefaultItemTransactionsKeep;
        if (!ShouldPrune(keep, Random.Shared.Next(GetPruneSampleRate(keep))))
            return;
        // never more than one partition pruned at a time per process; skip instead of queueing
        if (!pruneLock.Wait(0))
            return;
        var itemId = transaction.ItemId;
        _ = Task.Run(async () =>
        {
            try
            {
                await PruneItemPartition(session, itemId, triggerIsBazaar, keepItem, keepBazaar);
            }
            catch (Exception e)
            {
                pruneFailed.Inc();
                logger.LogError(e, "pruning item transactions of {itemId} failed", itemId);
            }
            finally
            {
                pruneLock.Release();
            }
        });
    }

    /// <summary>
    /// Decides how many rows a prune keeps. A bazaar write uses the bazaar keep. A non-bazaar write
    /// (e.g. a trade of a bazaar product) must never shrink a partition that holds bazaar rows, so when
    /// any of the newest rows is bazaar it returns 0 (skip, a bazaar write prunes that partition with the
    /// bazaar keep). Otherwise it is an item instance partition and the item keep applies.
    /// </summary>
    public static int ResolveKeep(bool triggerIsBazaar, IEnumerable<Transaction.TransactionType> newestTypes, int keep, int keepBazaar)
    {
        if (triggerIsBazaar)
            return keepBazaar;
        return newestTypes.Any(t => (t & Transaction.TransactionType.BAZAAR) != 0) ? 0 : keep;
    }

    /// <summary>
    /// Deletes everything older than the newest rows of one item partition with a single range delete
    /// (one range tombstone). The cutoff is found by paging through the newest keep+1 rows (timestamp and
    /// type) and stopping there; the keep is resolved from the partition content, see <see cref="ResolveKeep"/>.
    /// </summary>
    public async Task<bool> PruneItemPartition(ISession session, long itemId, bool triggerIsBazaar, int keepItem, int keepBazaar)
    {
        var readKeep = triggerIsBazaar ? keepBazaar : keepItem;
        if (readKeep <= 0)
            return false;
        var select = new SimpleStatement("SELECT timestamp, type FROM itemtransaction WHERE itemid = ? ORDER BY timestamp DESC LIMIT ?", itemId, readKeep + 1);
        select.SetPageSize(PruneReadPageSize);
        select.SetConsistencyLevel(ConsistencyLevel.Quorum);
        var rowSet = await session.ExecuteAsync(select);
        var rows = new List<DateTime>(Math.Min(readKeep + 1, 200_000));
        var types = new List<Transaction.TransactionType>(Math.Min(readKeep + 1, 200_000));
        while (rows.Count <= readKeep)
        {
            foreach (var row in rowSet.GetRows())
            {
                rows.Add(row.GetValue<DateTime>("timestamp"));
                types.Add((Transaction.TransactionType)row.GetValue<int>("type"));
                if (rows.Count > readKeep)
                    break;
            }
            if (rows.Count > readKeep || rowSet.IsFullyFetched)
                break;
            await rowSet.FetchMoreResultsAsync();
        }
        var keep = ResolveKeep(triggerIsBazaar, types, keepItem, keepBazaar);
        var cutoff = GetPruneCutoff(rows, keep);
        if (cutoff == null)
            return false;
        var statement = new SimpleStatement("DELETE FROM itemtransaction WHERE itemid = ? AND timestamp <= ?", itemId, cutoff.Value);
        statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
        await session.ExecuteAsync(statement);
        pruneDeletes.Inc();
        logger.LogInformation("pruned item transactions of {itemId} up to {cutoff}", itemId, cutoff);
        return true;
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
    public async Task<Dictionary<Guid,long[]>> GetItemIdsFromUuids(List<Guid> itemIds)
    {
        var result = await itemMappingTable.Where(t => itemIds.Contains(t.ItemUuid)).Select(t => new { t.ItemUuid, t.ItemId }).ExecuteAsync();
        return result.GroupBy(t => t.ItemUuid).ToDictionary(t => t.Key, t => t.Select(i => i.ItemId).ToArray());
    }
}
#nullable restore