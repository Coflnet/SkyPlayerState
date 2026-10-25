using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Bazaar.History;

/// <summary>Thrown for invalid or rejected history requests; <see cref="Status"/> is the HTTP status to return.</summary>
public class HistoryRequestException : Exception
{
    public int Status { get; }
    public HistoryRequestException(int status, string message) : base(message) { Status = status; }
}

public class HistoryResult<T>
{
    public List<T> Items { get; set; } = new();
    /// <summary>Opaque cursor for the next page, null when everything was returned.</summary>
    public string? NextPagingState { get; set; }
}

/// <summary>One order as exposed to researchers. Contains neither uuid nor player name.</summary>
public class BazaarOrderHistoryDto
{
    public string ItemTag { get; set; } = null!;
    public string AccountHash { get; set; } = null!;
    public bool IsSell { get; set; }
    public DateTime Created { get; set; }
    public double PricePerUnit { get; set; }
    public long Amount { get; set; }
    public long FilledAmount { get; set; }
    public long? ClaimedAmount { get; set; }
    public bool IsExpired { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string State { get; set; } = null!;
    public DateTime LastObservedAt { get; set; }
    public string FirstSeenSource { get; set; } = null!;
    /// <summary>(time, cumulative filled amount) points in time order.</summary>
    public List<FillPointDto> FillTimes { get; set; } = new();
    public DateTime? FullFillAt { get; set; }

    public static BazaarOrderHistoryDto From(BazaarOrderHistoryRow r) => new()
    {
        ItemTag = r.ItemTag, AccountHash = r.AccountHash, IsSell = r.IsSell, Created = r.Created, PricePerUnit = r.PricePerUnit,
        Amount = r.Amount, FilledAmount = r.FilledAmount, ClaimedAmount = r.ClaimedAmount, IsExpired = r.IsExpired, ClosedAt = r.ClosedAt,
        State = r.State, LastObservedAt = r.LastObservedAt, FirstSeenSource = r.FirstSeenSource, FullFillAt = r.FullFillAt,
        FillTimes = new(System.Linq.Enumerable.Select(r.FillTimes, p => new FillPointDto { Time = p.Key, Filled = p.Value }))
    };
}

public class FillPointDto
{
    public DateTime Time { get; set; }
    public long Filled { get; set; }
}

public class BazaarFlipHistoryDto
{
    public string ItemTag { get; set; } = null!;
    public string AccountHash { get; set; } = null!;
    public DateTime SoldAt { get; set; }
    public int Amount { get; set; }
    /// <summary>Totals in coins*10 like the per-player flip endpoint's stored values.</summary>
    public long BuyPriceTotal { get; set; }
    public long SellPriceTotal { get; set; }
    public long Profit { get; set; }

    public static BazaarFlipHistoryDto From(BazaarFlipHistoryRow r) => new()
    {
        ItemTag = r.ItemTag, AccountHash = r.AccountHash, SoldAt = r.SoldAt, Amount = r.Amount,
        BuyPriceTotal = r.BuyPriceTotal, SellPriceTotal = r.SellPriceTotal, Profit = r.Profit
    };
}

/// <summary>
/// Partition-bounded reads over the history tables: only the day/month partitions covered by a limited time span are
/// visited, pages are capped and a global semaphore turns overload into 429 instead of load on Scylla.
/// </summary>
public class BazaarHistoryReader
{
    public const int MaxPageSize = 1000;
    public const int DefaultPageSize = 500;
    public static readonly TimeSpan MaxOrderSpan = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxFlipSpan = TimeSpan.FromDays(31);

    private readonly IBazaarHistoryStore store;
    private readonly SemaphoreSlim gate;
    private readonly bool enabled;

    public BazaarHistoryReader(IBazaarHistoryStore store, BazaarHistoryOptions options)
    {
        this.store = store;
        enabled = options.Enabled;
        gate = new SemaphoreSlim(Math.Max(1, options.ReadConcurrency), Math.Max(1, options.ReadConcurrency));
    }

    public Task<HistoryResult<BazaarOrderHistoryDto>> GetOrders(string? itemTag, DateTime? from, DateTime? to, int? pageSize, string? pagingState)
    {
        var (q, cursor) = Resolve(itemTag, from, to, pageSize, pagingState, MaxOrderSpan);
        var days = new List<DateTime>();
        for (var d = q.From.Date; d <= q.To.Date; d = d.AddDays(1))
            days.Add(d);
        return Run(q, cursor, days.Count, BazaarOrderHistoryDto.From,
            (i, size, state) => store.QueryOrders(q.ItemTag, days[i], q.From, q.To, size, state));
    }

    public Task<HistoryResult<BazaarFlipHistoryDto>> GetFlips(string? itemTag, DateTime? from, DateTime? to, int? pageSize, string? pagingState)
    {
        var (q, cursor) = Resolve(itemTag, from, to, pageSize, pagingState, MaxFlipSpan);
        var months = new List<int>();
        // newest month first, matching the clustering order (sold_at desc)
        for (var m = new DateTime(q.To.Year, q.To.Month, 1); m >= new DateTime(q.From.Year, q.From.Month, 1); m = m.AddMonths(-1))
            months.Add(m.Year * 100 + m.Month);
        return Run(q, cursor, months.Count, BazaarFlipHistoryDto.From,
            (i, size, state) => store.QueryFlips(q.ItemTag, months[i], q.From, q.To, size, state));
    }

    private record Query(string ItemTag, DateTime From, DateTime To, int PageSize);

    /// <summary>Cursor: item, the resolved from/to ticks (so an omitted "to" stays stable), partition index, driver state.</summary>
    private record Cursor(string I, long F, long T, int P, string? S);

    private (Query, (int Partition, byte[]? State)) Resolve(string? itemTag, DateTime? from, DateTime? to, int? pageSize, string? pagingState, TimeSpan maxSpan)
    {
        if (!enabled)
            throw new HistoryRequestException(503, "bazaar history is disabled (BAZAAR_HISTORY:SALT not configured)");
        if (string.IsNullOrWhiteSpace(itemTag) || itemTag.Length > 100)
            throw new HistoryRequestException(400, "itemTag is required");
        var size = pageSize ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
            throw new HistoryRequestException(400, $"pageSize must be between 1 and {MaxPageSize}");
        var tag = itemTag.Trim();
        DateTime start, end;
        var partition = 0;
        byte[]? state = null;
        if (string.IsNullOrEmpty(pagingState))
        {
            end = BazaarHistoryKeys.Utc(to ?? DateTime.UtcNow);
            start = BazaarHistoryKeys.Utc(from ?? end.AddDays(-1));
        }
        else
        {
            // later pages reuse the range resolved on the first call, whatever from/to the caller repeats
            var c = Decode(pagingState);
            if (c.I != tag)
                throw new HistoryRequestException(400, "pagingState does not belong to this query");
            start = new DateTime(c.F, DateTimeKind.Utc);
            end = new DateTime(c.T, DateTimeKind.Utc);
            partition = c.P;
            state = DecodeState(c.S);
        }
        if (start > end)
            throw new HistoryRequestException(400, "from must not be after to");
        if (end - start > maxSpan)
            throw new HistoryRequestException(400, $"time span must not exceed {maxSpan.TotalDays:0} days");
        return (new Query(tag, start, end, size), (partition, state));
    }

    private static string Encode(Cursor c) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(c)).Replace('+', '-').Replace('/', '_');

    private static Cursor Decode(string text)
    {
        try
        {
            var b64 = text.Replace('-', '+').Replace('_', '/');
            while (b64.Length % 4 != 0) b64 += "=";
            var c = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(b64));
            if (c == null || c.I == null || c.P < 0 || c.F < DateTime.MinValue.Ticks || c.T > DateTime.MaxValue.Ticks || c.F < 0 || c.T < 0)
                throw new HistoryRequestException(400, "pagingState is invalid");
            return c;
        }
        catch (HistoryRequestException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new HistoryRequestException(400, "pagingState is invalid");
        }
    }

    private static byte[]? DecodeState(string? s)
    {
        if (s == null)
            return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new HistoryRequestException(400, "pagingState is invalid"); }
    }

    private async Task<HistoryResult<TDto>> Run<TRow, TDto>(Query q, (int Partition, byte[]? State) cursor, int partitions, Func<TRow, TDto> map,
        Func<int, int, byte[]?, Task<HistoryPage<TRow>>> fetch)
    {
        if (cursor.Partition > partitions)
            throw new HistoryRequestException(400, "pagingState is invalid");
        if (!await gate.WaitAsync(0))
            throw new HistoryRequestException(429, "too many concurrent history queries, retry later");
        try
        {
            var result = new HistoryResult<TDto>();
            var index = cursor.Partition;
            var state = cursor.State;
            while (index < partitions && result.Items.Count < q.PageSize)
            {
                var page = await fetch(index, q.PageSize - result.Items.Count, state);
                result.Items.AddRange(System.Linq.Enumerable.Select(page.Rows, map));
                if (page.NextPagingState != null)
                {
                    state = page.NextPagingState;
                    break;
                }
                index++;
                state = null;
            }
            if (index < partitions)
                result.NextPagingState = Encode(new Cursor(q.ItemTag, q.From.Ticks, q.To.Ticks, index, state == null ? null : Convert.ToBase64String(state)));
            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}
