using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Coflnet.Sky.PlayerState.Bazaar.History;

/// <summary>
/// Settings of the cross-player bazaar history tables. Config keys (double underscore in env / OpenBao):
/// <c>BAZAAR_HISTORY:SALT</c> (required, feature disabled without it), <c>BAZAAR_HISTORY:TTL_DAYS</c> (default 180),
/// <c>BAZAAR_HISTORY:WRITE_CONCURRENCY</c> (8), <c>BAZAAR_HISTORY:QUEUE_CAPACITY</c> (10000),
/// <c>BAZAAR_HISTORY:READ_CONCURRENCY</c> (4), <c>BAZAAR_HISTORY:BACKFILL</c> ("true" enables the load-time backfill).
/// </summary>
public class BazaarHistoryOptions
{
    public string? Salt { get; set; }
    public int TtlDays { get; set; } = 180;
    public int WriteConcurrency { get; set; } = 8;
    public int QueueCapacity { get; set; } = 10_000;
    public int ReadConcurrency { get; set; } = 4;
    public bool Backfill { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Salt);
    /// <summary>TTL in seconds, clamped to Cassandra's 20 year maximum so a large TTL_DAYS cannot overflow.</summary>
    public int TtlSeconds => (int)Math.Min(630_720_000L, Math.Max(1, (long)TtlDays) * 86_400L);

    public static BazaarHistoryOptions From(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        static int Int(string? v, int fallback) => int.TryParse(v, out var i) && i > 0 ? i : fallback;
        return new BazaarHistoryOptions
        {
            Salt = config["BAZAAR_HISTORY:SALT"],
            TtlDays = Int(config["BAZAAR_HISTORY:TTL_DAYS"], 180),
            WriteConcurrency = Int(config["BAZAAR_HISTORY:WRITE_CONCURRENCY"], 8),
            QueueCapacity = Int(config["BAZAAR_HISTORY:QUEUE_CAPACITY"], 10_000),
            ReadConcurrency = Int(config["BAZAAR_HISTORY:READ_CONCURRENCY"], 4),
            Backfill = string.Equals(config["BAZAAR_HISTORY:BACKFILL"], "true", StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>Pseudonymises player uuids with a server side secret so rows can be grouped per account but not resolved.</summary>
public static class BazaarAccountHasher
{
    private sealed class KeyHolder { public string Salt = null!; public byte[] Bytes = null!; }
    private static KeyHolder? cached;

    /// <summary>Lowercase hex HMAC-SHA256 keyed with the salt over the uuid ("N" format).</summary>
    public static string Hash(string salt, Guid playerUuid)
    {
        var holder = cached;
        if (holder == null || holder.Salt != salt)
            cached = holder = new KeyHolder { Salt = salt, Bytes = Encoding.UTF8.GetBytes(salt) };
        return Convert.ToHexString(HMACSHA256.HashData(holder.Bytes, Encoding.UTF8.GetBytes(playerUuid.ToString("N")))).ToLowerInvariant();
    }
}

public static class BazaarHistoryKeys
{
    public static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
    /// <summary>UTC date (midnight) of the order creation, the day half of the order partition key.</summary>
    public static DateTime DayOf(DateTime created) => Utc(created).Date;
    /// <summary>yyyyMM of the sale, the month half of the flip partition key.</summary>
    public static int MonthOf(DateTime soldAt) { var u = Utc(soldAt); return u.Year * 100 + u.Month; }
    public static DateTime MonthStart(int month) => new(month / 100, month % 100, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>One row of <c>bazaar_order_history</c>. Never contains uuid or name, only the salted account hash.</summary>
public class BazaarOrderHistoryRow
{
    public string ItemTag { get; set; } = null!;
    /// <summary>UTC date of <see cref="Created"/> (partition key).</summary>
    public DateTime Day { get; set; }
    public DateTime Created { get; set; }
    public string AccountHash { get; set; } = null!;
    public bool IsSell { get; set; }
    /// <summary>Coins per unit exactly as stored on <see cref="Offer.PricePerUnit"/>.</summary>
    public double PricePerUnit { get; set; }
    public long Amount { get; set; }
    public long FilledAmount { get; set; }
    public long? ClaimedAmount { get; set; }
    public bool IsExpired { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string State { get; set; } = null!;
    public DateTime LastObservedAt { get; set; }
    /// <summary>Source of the first observation (chat-setup = exact placement time, gui-view/backfill = estimated).</summary>
    public string FirstSeenSource { get; set; } = null!;
    /// <summary>Cumulative filled amount by time: customer fill timestamps (exact) and observation times (upper bound).</summary>
    public SortedDictionary<DateTime, long> FillTimes { get; set; } = new();
    /// <summary>When the order became completely filled; set once, never changed afterwards.</summary>
    public DateTime? FullFillAt { get; set; }

    public BazaarOrderHistoryRow Clone()
    {
        var c = (BazaarOrderHistoryRow)MemberwiseClone();
        c.FillTimes = new SortedDictionary<DateTime, long>(FillTimes);
        return c;
    }
}

/// <summary>Columns of an order row that a write has to set; everything else is left untouched (no tombstones).</summary>
[Flags]
public enum OrderColumns
{
    None = 0,
    Price = 1, Amount = 2, Filled = 4, Claimed = 8, Expired = 16, ClosedAt = 32, State = 64,
    LastObserved = 128, FirstSeen = 256, FullFill = 512,
    All = 1023
}

/// <summary>One row of <c>bazaar_flips_by_item</c>. Prices are coins*10 like <see cref="CompletedBazaarFlip"/>.</summary>
public class BazaarFlipHistoryRow
{
    public string ItemTag { get; set; } = null!;
    /// <summary>yyyyMM of <see cref="SoldAt"/> (partition key).</summary>
    public int Month { get; set; }
    public DateTime SoldAt { get; set; }
    public string AccountHash { get; set; } = null!;
    public int Amount { get; set; }
    public long BuyPriceTotal { get; set; }
    public long SellPriceTotal { get; set; }
    public long Profit { get; set; }
}

/// <summary>An observation of an order, already pseudonymised (holds no uuid and no player names).</summary>
public class BazaarOrderObservation
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
    public DateTime ObservedAt { get; set; }
    public string Source { get; set; } = null!;
    /// <summary>Fill timestamps with their amounts (from <see cref="Offer.Customers"/>), names dropped.</summary>
    public List<(DateTime Time, long Amount)> Fills { get; set; } = new();
    /// <summary>Backfilled observations carry no trustworthy observation time, so no observation fill point is recorded.</summary>
    public bool IsBackfill { get; set; }
}

public static class BazaarOrderHistoryMerge
{
    public const string SourceBackfill = "backfill";
    private const int MaxFillPoints = 256;

    /// <summary>
    /// Compares a row before and after <see cref="Apply"/>. Fill points are append-only, so only keys that are new are
    /// returned. A null <paramref name="before"/> means the row is new and every column is written.
    /// </summary>
    public static (OrderColumns Changed, Dictionary<DateTime, long> NewPoints) Diff(BazaarOrderHistoryRow? before, BazaarOrderHistoryRow after)
    {
        if (before == null)
        {
            // never write nulls for a new row: they would only create tombstones
            var all = OrderColumns.All;
            if (after.ClaimedAmount == null) all &= ~OrderColumns.Claimed;
            if (after.ClosedAt == null) all &= ~OrderColumns.ClosedAt;
            if (after.FullFillAt == null) all &= ~OrderColumns.FullFill;
            return (all, new Dictionary<DateTime, long>(after.FillTimes));
        }
        var c = OrderColumns.None;
        if (before.PricePerUnit != after.PricePerUnit) c |= OrderColumns.Price;
        if (before.Amount != after.Amount) c |= OrderColumns.Amount;
        if (before.FilledAmount != after.FilledAmount) c |= OrderColumns.Filled;
        if (before.ClaimedAmount != after.ClaimedAmount) c |= OrderColumns.Claimed;
        if (before.IsExpired != after.IsExpired) c |= OrderColumns.Expired;
        if (before.ClosedAt != after.ClosedAt) c |= OrderColumns.ClosedAt;
        if (before.State != after.State) c |= OrderColumns.State;
        if (before.LastObservedAt != after.LastObservedAt) c |= OrderColumns.LastObserved;
        if (before.FirstSeenSource != after.FirstSeenSource) c |= OrderColumns.FirstSeen;
        if (before.FullFillAt != after.FullFillAt) c |= OrderColumns.FullFill;
        var points = after.FillTimes.Where(p => !before.FillTimes.TryGetValue(p.Key, out var v) || v != p.Value).ToDictionary(p => p.Key, p => p.Value);
        return (c, points);
    }

    public static string StateFor(Offer o) =>
        o.ClosedAt != null ? BazaarOrderLifecycleEvent.StateClosed
        : o.IsExpired ? BazaarOrderLifecycleEvent.StateExpired
        : o.Amount > 0 && o.FilledAmount >= o.Amount ? BazaarOrderLifecycleEvent.StateFilled
        : BazaarOrderLifecycleEvent.StateOpen;

    private static int Rank(string? state) => state switch
    {
        BazaarOrderLifecycleEvent.StateOpen => 0,
        BazaarOrderLifecycleEvent.StateFilled => 1,
        BazaarOrderLifecycleEvent.StateClaimed => 2,
        BazaarOrderLifecycleEvent.StateCancelled => 3,
        BazaarOrderLifecycleEvent.StateExpired => 3,
        BazaarOrderLifecycleEvent.StateClosed => 4,
        _ => -1
    };

    public static BazaarOrderObservation Observe(BazaarOrderLifecycleEvent e, IEnumerable<Fill>? fills, string accountHash) => new()
    {
        ItemTag = e.ItemTag,
        AccountHash = accountHash,
        IsSell = e.IsSell,
        Created = BazaarHistoryKeys.Utc(e.Created),
        PricePerUnit = e.PricePerUnit,
        Amount = e.Amount,
        FilledAmount = e.FilledAmount,
        ClaimedAmount = e.ClaimedAmount,
        IsExpired = e.IsExpired,
        ClosedAt = e.ClosedAt == null ? null : BazaarHistoryKeys.Utc(e.ClosedAt.Value),
        State = e.State,
        ObservedAt = BazaarHistoryKeys.Utc(e.ObservedAt),
        Source = e.Source,
        Fills = (fills ?? Array.Empty<Fill>()).Where(f => f != null).Select(f => (BazaarHistoryKeys.Utc(f.TimeStamp), f.Amount)).ToList(),
    };

    /// <summary>
    /// Folds an observation into the stored row (or creates it). Pure and idempotent: replaying the same observation
    /// yields the same row. Fill points are only added when the cumulative filled amount grew, and
    /// <see cref="BazaarOrderHistoryRow.FullFillAt"/> is set exactly once.
    /// </summary>
    public static BazaarOrderHistoryRow Apply(BazaarOrderHistoryRow? existing, BazaarOrderObservation obs)
    {
        var row = existing ?? new BazaarOrderHistoryRow
        {
            ItemTag = obs.ItemTag,
            Day = BazaarHistoryKeys.DayOf(obs.Created),
            Created = obs.Created,
            AccountHash = obs.AccountHash,
            IsSell = obs.IsSell,
            State = obs.State,
            FirstSeenSource = obs.Source,
        };
        row.PricePerUnit = obs.PricePerUnit;
        row.Amount = Math.Max(row.Amount, obs.Amount);
        row.FilledAmount = Math.Max(row.FilledAmount, obs.FilledAmount);
        row.ClaimedAmount = row.ClaimedAmount == null ? obs.ClaimedAmount
            : obs.ClaimedAmount == null ? row.ClaimedAmount : Math.Max(row.ClaimedAmount.Value, obs.ClaimedAmount.Value);
        row.IsExpired |= obs.IsExpired;
        row.ClosedAt ??= obs.ClosedAt;
        if (Rank(obs.State) >= Rank(row.State))
            row.State = obs.State;
        if (obs.ObservedAt > row.LastObservedAt)
            row.LastObservedAt = obs.ObservedAt;
        row.FirstSeenSource ??= obs.Source;

        long cumulative = 0;
        foreach (var fill in obs.Fills.Where(f => f.Amount > 0 && f.Time.Year >= 2020).OrderBy(f => f.Time))
        {
            cumulative += fill.Amount;
            if (row.FillTimes.Count < MaxFillPoints || row.FillTimes.ContainsKey(fill.Time))
                row.FillTimes[fill.Time] = Math.Max(cumulative, row.FillTimes.GetValueOrDefault(fill.Time));
        }
        var known = row.FillTimes.Count == 0 ? 0 : row.FillTimes.Values.Max();
        if (!obs.IsBackfill && obs.FilledAmount > known && row.FillTimes.Count < MaxFillPoints)
            row.FillTimes[obs.ObservedAt] = obs.FilledAmount;

        if (row.FullFillAt == null && row.Amount > 0 && row.FilledAmount >= row.Amount)
        {
            var point = row.FillTimes.Where(p => p.Value >= row.Amount).Select(p => (DateTime?)p.Key).FirstOrDefault();
            row.FullFillAt = point ?? (obs.IsBackfill ? null : obs.ObservedAt);
        }
        return row;
    }

    public static BazaarFlipHistoryRow ToFlipRow(CompletedBazaarFlip flip, string accountHash) => new()
    {
        ItemTag = flip.ItemTag,
        Month = BazaarHistoryKeys.MonthOf(flip.SoldAt),
        SoldAt = BazaarHistoryKeys.Utc(flip.SoldAt),
        AccountHash = accountHash,
        Amount = flip.Amount,
        BuyPriceTotal = flip.BuyPrice,
        SellPriceTotal = flip.SellPrice,
        Profit = flip.Profit,
    };
}
