using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The rare tags of a row or aggregate: fold time coins per tag, the units behind them and the part of the coins that
/// came with those units (<see cref="CountedCoins"/>). Only the counted part is re-priced live, the rest counts at face
/// value. The counted coins are stored on their own because code without them (a rollback, an old pod during a rolling
/// deploy) still adds to, or decays, the coins while leaving the units as they were: its coins then fall to face value.
/// </summary>
internal sealed record RareTags(Dictionary<string, double> Coins, Dictionary<string, double> Counts,
    Dictionary<string, double> CountedCoins)
{
    public static RareTags Empty() => new(new(), new(), new());

    public static RareTags Of(TaskBucketAggregateRow row) => new(row.RareItemCoins, row.RareItemCounts, row.RareItemCountedCoins);
    public static RareTags Of(TaskPlayerStatRow row) => new(row.RareItemCoins, row.RareItemCounts, row.RareItemCountedCoins);
    public static RareTags Of(BucketAggregate agg) => new(agg.RareItemCoins, agg.RareItemCounts, agg.RareItemCountedCoins);

    public void WriteTo(TaskBucketAggregateRow row)
        => (row.RareItemCoins, row.RareItemCounts, row.RareItemCountedCoins) = (Coins, Counts, CountedCoins);

    public void WriteTo(TaskPlayerStatRow row)
        => (row.RareItemCoins, row.RareItemCounts, row.RareItemCountedCoins) = (Coins, Counts, CountedCoins);

    /// <summary>Sum of <see cref="Coins"/>, 0 without any.</summary>
    public double CoinSum => Coins?.Values.Sum() ?? 0;
}

/// <summary>
/// Live pricing of the rare pool (<see cref="TaskPeriodFolder.SplitRareAndCommon"/>) from its stored per tag counts.
/// Rare coins were stored only at their fold time price, so a price fix (a sniper outlier) never reached stored data and
/// the pool was scaled by the drift of the COMMON items instead: one wrong common price on one pod moved the whole pool.
/// With counts, each rare tag is priced on its own: count times the live unit value, within
/// [<see cref="ClampLow"/>, <see cref="ClampHigh"/>] times its counted fold time coins. Coins without a count (rows
/// written before the counts existed, or added by code that does not store them) stay at face value.
/// </summary>
internal static class RareRepricing
{
    internal const double ClampLow = 0.25;
    internal const double ClampHigh = 4;

    /// <summary>
    /// Adds a fresh contribution: <paramref name="addCoins"/> per tag, counted when <paramref name="addCounts"/> has the
    /// tag's units. Not positive or not finite coins and counts are ignored.
    /// </summary>
    internal static void AddFresh(RareTags target, Dictionary<string, double>? addCoins, Dictionary<string, double>? addCounts)
    {
        if (addCoins == null)
            return;
        foreach (var (tag, value) in addCoins.Where(e => IsPositive(e.Value)))
        {
            AddTo(target.Coins, tag, value);
            var count = addCounts?.GetValueOrDefault(tag) ?? 0;
            if (!IsPositive(count))
                continue;
            AddTo(target.Counts, tag, count);
            AddTo(target.CountedCoins, tag, value);
        }
    }

    /// <summary>Adds stored tags (see <see cref="CountedPart"/>), times <paramref name="weight"/> (a decay).</summary>
    internal static void AddStored(RareTags target, RareTags source, double weight)
    {
        if (source?.Coins == null)
            return;
        foreach (var (tag, value) in source.Coins.Where(e => IsPositive(e.Value)))
        {
            AddTo(target.Coins, tag, value * weight);
            var (count, counted) = CountedPart(source, tag, value);
            if (counted <= 0)
                continue;
            AddTo(target.Counts, tag, count * weight);
            AddTo(target.CountedCoins, tag, counted * weight);
        }
    }

    /// <summary>A consistent copy of stored tags: every counted part within its coins, counts only for counted tags.</summary>
    internal static RareTags Normalized(RareTags source)
    {
        var result = RareTags.Empty();
        AddStored(result, source, 1);
        return result;
    }

    /// <summary>
    /// The units and counted coins of a stored tag with <paramref name="coins"/>: the counted coins at most the coins (older
    /// code may have decayed or trimmed the coins only), the units scaled alike. (0, 0) without a count or counted coins.
    /// </summary>
    internal static (double count, double countedCoins) CountedPart(RareTags tags, string tag, double coins)
    {
        var count = tags.Counts?.GetValueOrDefault(tag) ?? 0;
        var counted = tags.CountedCoins?.GetValueOrDefault(tag) ?? 0;
        if (!IsPositive(count) || !IsPositive(counted) || coins <= 0)
            return (0, 0);
        if (counted <= coins)
            return (count, counted);
        return (count * coins / counted, coins);
    }

    /// <summary>The <paramref name="limit"/> most valuable tags, their counts and counted coins with them.</summary>
    internal static RareTags Trimmed(RareTags tags, int limit)
    {
        if (tags.Coins == null || tags.Coins.Count <= limit)
            return tags;
        var kept = tags.Coins.OrderByDescending(e => e.Value).Take(limit).ToDictionary(e => e.Key, e => e.Value);
        return new RareTags(kept, OnlyTags(tags.Counts, kept), OnlyTags(tags.CountedCoins, kept));
    }

    /// <summary>
    /// <paramref name="tags"/> (normalized) scaled down so their coins are at most <paramref name="pool"/>: a player row's
    /// tags can not explain more than its rare pool. Older code decays the pool but not the tags.
    /// </summary>
    internal static RareTags BoundedBy(RareTags tags, double pool)
    {
        var normalized = Normalized(tags);
        var sum = normalized.CoinSum;
        if (sum <= pool || sum <= 0)
            return normalized;
        var bounded = RareTags.Empty();
        AddStored(bounded, normalized, Math.Max(0, pool) / sum);
        return bounded;
    }

    /// <summary>
    /// The live value of <paramref name="countedCoins"/> fold time coins of <paramref name="count"/> units: count times
    /// <paramref name="liveUnit"/>, clamped to [<see cref="ClampLow"/>, <see cref="ClampHigh"/>] times the fold coins.
    /// Without a live price (or count) the fold coins: a missing price is unknown, not worthless.
    /// </summary>
    internal static double Reprice(double countedCoins, double count, double liveUnit)
    {
        var live = count * liveUnit;
        if (countedCoins <= 0 || live <= 0 || !double.IsFinite(live))
            return countedCoins;
        return Math.Clamp(live, countedCoins * ClampLow, countedCoins * ClampHigh);
    }

    /// <summary>Each tag at its live value: the counted part re-priced, the rest at face value.</summary>
    internal static Dictionary<string, double> LiveItemCoins(RareTags tags, Func<string, double> liveUnit)
        => tags.Coins?.ToDictionary(e => e.Key, e => LiveTagCoins(tags, e.Key, e.Value, liveUnit)) ?? new();

    /// <summary>
    /// What live pricing adds to the face value of the tags (negative when prices fell), so a pool that also holds
    /// untraced coins (rare coins beyond its tags) keeps those at face value.
    /// </summary>
    internal static double LiveAdjustment(RareTags tags, Func<string, double> liveUnit)
        => tags.Coins?.Sum(e => LiveTagCoins(tags, e.Key, e.Value, liveUnit) - e.Value) ?? 0;

    private static double LiveTagCoins(RareTags tags, string tag, double coins, Func<string, double> liveUnit)
    {
        var (count, counted) = CountedPart(tags, tag, coins);
        if (counted <= 0)
            return coins;
        return coins - counted + Reprice(counted, count, liveUnit(tag));
    }

    /// <summary><paramref name="values"/> times <paramref name="factor"/> (a ramp weight), null stays null.</summary>
    internal static Dictionary<string, double> Scaled(Dictionary<string, double> values, double factor)
        => values?.ToDictionary(e => e.Key, e => e.Value * factor);

    private static Dictionary<string, double> OnlyTags(Dictionary<string, double> values, Dictionary<string, double> tags)
        => values?.Where(e => tags.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);

    private static void AddTo(Dictionary<string, double> target, string tag, double value)
        => target[tag] = target.GetValueOrDefault(tag) + value;

    private static bool IsPositive(double value) => value > 0 && double.IsFinite(value);
}
