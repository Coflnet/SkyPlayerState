using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar.History;

public class HistoryPage<T>
{
    public List<T> Rows { get; set; } = new();
    /// <summary>Driver paging state, null when the partition is exhausted.</summary>
    public byte[]? NextPagingState { get; set; }
}

/// <summary>Storage seam of the cross-player bazaar history (Scylla in production, in-memory in tests).</summary>
public interface IBazaarHistoryStore
{
    Task<BazaarOrderHistoryRow?> GetOrder(string itemTag, DateTime day, DateTime created, string accountHash, bool isSell);
    /// <summary>
    /// Writes only the <paramref name="columns"/> of the row plus <paramref name="newPoints"/> appended to fill_times
    /// (CQL UPDATE, no read, no collection tombstone). Unlisted columns are left untouched.
    /// </summary>
    Task WriteOrder(BazaarOrderHistoryRow row, OrderColumns columns, IReadOnlyDictionary<DateTime, long> newPoints, int ttlSeconds);
    Task UpsertFlip(BazaarFlipHistoryRow row, int ttlSeconds);
    Task<HistoryPage<BazaarOrderHistoryRow>> QueryOrders(string itemTag, DateTime day, DateTime from, DateTime to, int pageSize, byte[]? pagingState);
    Task<HistoryPage<BazaarFlipHistoryRow>> QueryFlips(string itemTag, int month, DateTime from, DateTime to, int pageSize, byte[]? pagingState);
}

/// <summary>
/// Scylla implementation using the service's existing <see cref="ISession"/>. Tables are created lazily and additively
/// (CREATE TABLE IF NOT EXISTS), like <see cref="BazaarProfitTracker"/>.
/// </summary>
public class CassandraBazaarHistoryStore : IBazaarHistoryStore
{
    private readonly ISession session;
    private readonly BazaarHistoryOptions options;
    private readonly ILogger<CassandraBazaarHistoryStore> logger;
    private readonly System.Threading.SemaphoreSlim initLock = new(1, 1);
    private bool initialized;
    private DateTime retryInitAfter;
    private Exception? initFailure;
    private PreparedStatement? getOrder, updateOrder, upsertFlip, queryOrders, queryFlips;
    /// <summary>How long a failed schema setup is remembered before it is attempted again.</summary>
    public static TimeSpan InitRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public CassandraBazaarHistoryStore(ISession session, BazaarHistoryOptions options, ILogger<CassandraBazaarHistoryStore> logger)
    {
        this.session = session;
        this.options = options;
        this.logger = logger;
    }

    private const string OrderSelect = "item_tag, day, created, account_hash, is_sell, price_per_unit, amount, filled_amount, claimed_amount, is_expired, closed_at, state, last_observed_at, first_seen_source, fill_times, full_fill_at";
    private const string FlipColumns = "item_tag, month, sold_at, account_hash, amount, buy_price_total, sell_price_total, profit";

    private async Task Init()
    {
        if (initialized)
            return;
        await initLock.WaitAsync();
        try
        {
            if (initialized)
                return;
            // a failed setup is cached so queued writes do not re-run CREATE (and log a stack trace) every time
            if (initFailure != null && DateTime.UtcNow < retryInitAfter)
                throw new InvalidOperationException("bazaar history schema unavailable: " + initFailure.Message, initFailure);
            try
            {
                var ttl = options.TtlSeconds;
                await CreateIfMissing("bazaar_order_history", $@"CREATE TABLE IF NOT EXISTS bazaar_order_history (
                    item_tag text, day date, created timestamp, account_hash text, is_sell boolean,
                    price_per_unit double, amount bigint, filled_amount bigint, claimed_amount bigint, is_expired boolean,
                    closed_at timestamp, state text, last_observed_at timestamp, first_seen_source text,
                    fill_times map<timestamp, bigint>, full_fill_at timestamp,
                    PRIMARY KEY ((item_tag, day), created, account_hash, is_sell))
                    WITH CLUSTERING ORDER BY (created ASC, account_hash ASC, is_sell ASC) AND default_time_to_live = {ttl}");
                await CreateIfMissing("bazaar_flips_by_item", $@"CREATE TABLE IF NOT EXISTS bazaar_flips_by_item (
                    item_tag text, month int, sold_at timestamp, account_hash text,
                    amount int, buy_price_total bigint, sell_price_total bigint, profit bigint,
                    PRIMARY KEY ((item_tag, month), sold_at, account_hash))
                    WITH CLUSTERING ORDER BY (sold_at DESC, account_hash ASC) AND default_time_to_live = {ttl}");
                getOrder = await session.PrepareAsync($"SELECT {OrderSelect} FROM bazaar_order_history WHERE item_tag = ? AND day = ? AND created = ? AND account_hash = ? AND is_sell = ?");
                updateOrder = await session.PrepareAsync("UPDATE bazaar_order_history USING TTL ? SET price_per_unit = ?, amount = ?, filled_amount = ?, claimed_amount = ?, is_expired = ?, closed_at = ?, state = ?, last_observed_at = ?, first_seen_source = ?, fill_times = fill_times + ?, full_fill_at = ? WHERE item_tag = ? AND day = ? AND created = ? AND account_hash = ? AND is_sell = ?");
                upsertFlip = await session.PrepareAsync($"INSERT INTO bazaar_flips_by_item ({FlipColumns}) VALUES (?,?,?,?,?,?,?,?) USING TTL ?");
                queryOrders = await session.PrepareAsync($"SELECT {OrderSelect} FROM bazaar_order_history WHERE item_tag = ? AND day = ? AND created >= ? AND created <= ?");
                queryFlips = await session.PrepareAsync($"SELECT {FlipColumns} FROM bazaar_flips_by_item WHERE item_tag = ? AND month = ? AND sold_at >= ? AND sold_at <= ?");
                initialized = true;
                initFailure = null;
            }
            catch (Exception e)
            {
                initFailure = e;
                retryInitAfter = DateTime.UtcNow + InitRetryDelay;
                // logged once per window; callers during the window get the cached failure without a log line
                logger.LogWarning(e, "Bazaar history schema setup failed, retrying in {Delay}", InitRetryDelay);
                throw;
            }
        }
        finally
        {
            initLock.Release();
        }
    }

    /// <summary>Checks system_schema first (like TransactionService) because Scylla demands CREATE permission even for existing tables.</summary>
    private async Task CreateIfMissing(string table, string cql)
    {
        var exists = await session.ExecuteAsync(new SimpleStatement(
            "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ?", session.Keyspace, table));
        if (exists.Any())
            return;
        await session.ExecuteAsync(new SimpleStatement(cql));
        logger.LogInformation("Created table {Table}", table);
    }

    private static LocalDate Date(DateTime d) => new(d.Year, d.Month, d.Day);
    private static DateTimeOffset Ts(DateTime d) => new(BazaarHistoryKeys.Utc(d));
    private static DateTime? Dt(object? o) => o is DateTimeOffset d ? d.UtcDateTime : null;

    public async Task<BazaarOrderHistoryRow?> GetOrder(string itemTag, DateTime day, DateTime created, string accountHash, bool isSell)
    {
        await Init();
        var rs = await session.ExecuteAsync(getOrder!.Bind(itemTag, Date(day), Ts(created), accountHash, isSell));
        var row = rs.FirstOrDefault();
        return row == null ? null : MapOrder(row);
    }

    public async Task WriteOrder(BazaarOrderHistoryRow r, OrderColumns columns, IReadOnlyDictionary<DateTime, long> newPoints, int ttlSeconds)
    {
        await Init();
        // Unset leaves a column alone: no write, no tombstone
        object Pick(OrderColumns c, object? value) => columns.HasFlag(c) ? value! : Unset.Value;
        IDictionary<DateTimeOffset, long> points = newPoints.ToDictionary(p => Ts(p.Key), p => p.Value);
        await session.ExecuteAsync(updateOrder!.Bind(ttlSeconds,
            Pick(OrderColumns.Price, r.PricePerUnit), Pick(OrderColumns.Amount, r.Amount), Pick(OrderColumns.Filled, r.FilledAmount),
            columns.HasFlag(OrderColumns.Claimed) ? (object?)r.ClaimedAmount : Unset.Value, Pick(OrderColumns.Expired, r.IsExpired),
            columns.HasFlag(OrderColumns.ClosedAt) ? (object?)(r.ClosedAt == null ? null : (DateTimeOffset?)Ts(r.ClosedAt.Value)) : Unset.Value,
            Pick(OrderColumns.State, r.State), Pick(OrderColumns.LastObserved, Ts(r.LastObservedAt)), Pick(OrderColumns.FirstSeen, r.FirstSeenSource),
            points,
            columns.HasFlag(OrderColumns.FullFill) ? (object?)(r.FullFillAt == null ? null : (DateTimeOffset?)Ts(r.FullFillAt.Value)) : Unset.Value,
            r.ItemTag, Date(r.Day), Ts(r.Created), r.AccountHash, r.IsSell));
    }

    public async Task UpsertFlip(BazaarFlipHistoryRow r, int ttlSeconds)
    {
        await Init();
        await session.ExecuteAsync(upsertFlip!.Bind(r.ItemTag, r.Month, Ts(r.SoldAt), r.AccountHash, r.Amount, r.BuyPriceTotal, r.SellPriceTotal, r.Profit, ttlSeconds));
    }

    public async Task<HistoryPage<BazaarOrderHistoryRow>> QueryOrders(string itemTag, DateTime day, DateTime from, DateTime to, int pageSize, byte[]? pagingState)
    {
        await Init();
        var statement = queryOrders!.Bind(itemTag, Date(day), Ts(from), Ts(to)).SetPageSize(pageSize);
        if (pagingState != null)
            statement.SetPagingState(pagingState);
        var rs = await session.ExecuteAsync(statement);
        // GetRows yields the current page only, so the result never exceeds pageSize
        return new HistoryPage<BazaarOrderHistoryRow> { Rows = rs.GetRows().Select(MapOrder).ToList(), NextPagingState = rs.PagingState };
    }

    public async Task<HistoryPage<BazaarFlipHistoryRow>> QueryFlips(string itemTag, int month, DateTime from, DateTime to, int pageSize, byte[]? pagingState)
    {
        await Init();
        var statement = queryFlips!.Bind(itemTag, month, Ts(from), Ts(to)).SetPageSize(pageSize);
        if (pagingState != null)
            statement.SetPagingState(pagingState);
        var rs = await session.ExecuteAsync(statement);
        return new HistoryPage<BazaarFlipHistoryRow> { Rows = rs.GetRows().Select(MapFlip).ToList(), NextPagingState = rs.PagingState };
    }

    private static BazaarOrderHistoryRow MapOrder(Row r)
    {
        var day = r.GetValue<LocalDate>("day");
        var fills = r.GetValue<IDictionary<DateTimeOffset, long>>("fill_times");
        return new BazaarOrderHistoryRow
        {
            ItemTag = r.GetValue<string>("item_tag"),
            Day = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc),
            Created = r.GetValue<DateTimeOffset>("created").UtcDateTime,
            AccountHash = r.GetValue<string>("account_hash"),
            IsSell = r.GetValue<bool>("is_sell"),
            PricePerUnit = r.GetValue<double>("price_per_unit"),
            Amount = r.GetValue<long>("amount"),
            FilledAmount = r.GetValue<long>("filled_amount"),
            ClaimedAmount = r.IsNull("claimed_amount") ? null : r.GetValue<long>("claimed_amount"),
            IsExpired = r.GetValue<bool>("is_expired"),
            ClosedAt = Dt(r.IsNull("closed_at") ? null : r.GetValue<DateTimeOffset>("closed_at")),
            State = r.GetValue<string>("state"),
            LastObservedAt = r.GetValue<DateTimeOffset>("last_observed_at").UtcDateTime,
            FirstSeenSource = r.GetValue<string>("first_seen_source"),
            FillTimes = new SortedDictionary<DateTime, long>((fills ?? new Dictionary<DateTimeOffset, long>()).ToDictionary(p => p.Key.UtcDateTime, p => p.Value)),
            FullFillAt = Dt(r.IsNull("full_fill_at") ? null : r.GetValue<DateTimeOffset>("full_fill_at")),
        };
    }

    private static BazaarFlipHistoryRow MapFlip(Row r) => new()
    {
        ItemTag = r.GetValue<string>("item_tag"),
        Month = r.GetValue<int>("month"),
        SoldAt = r.GetValue<DateTimeOffset>("sold_at").UtcDateTime,
        AccountHash = r.GetValue<string>("account_hash"),
        Amount = r.GetValue<int>("amount"),
        BuyPriceTotal = r.GetValue<long>("buy_price_total"),
        SellPriceTotal = r.GetValue<long>("sell_price_total"),
        Profit = r.GetValue<long>("profit"),
    };
}
