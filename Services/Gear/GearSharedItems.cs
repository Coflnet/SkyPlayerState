using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>One sighting of an item: the item, the hours it was used and by whom.</summary>
public sealed record GearSeen<T>(T Item, double Hours, string Player);

/// <summary>
/// Item specs built so that no single player's item shows (k = <c>minPlayers</c>):
/// <list type="bullet">
/// <item>a piece is only built from sightings of at least k distinct players with its tag;</item>
/// <item>of that tag, every modifier (rarity, reforge, stars, hot potato count, recombobulated, each enchant, gem slot,
/// part and applied count) is shown only with a value at least k distinct players share (the most shared value wins);
/// a counter shows the highest bucket at least k players reach;</item>
/// <item>lore stats are each player's hours weighted mean, then the median over at least k players;</item>
/// <item>the pet likewise: the type of at least k players; tier and level as one combination at least k of them reach
/// together (each player's highest counts, like the counters); the held item when k players share it.</item>
/// </list>
/// Values are unrounded; the publisher rounds below 5 players.
/// </summary>
public static class GearSharedItems
{
    public static GearPiece? SharedPiece(IReadOnlyList<GearSeen<GearPiece>> seen, int minPlayers)
    {
        if (Shared(seen, p => p.Tag, minPlayers) is not string tag)
            return null;
        var same = seen.Where(s => s.Item.Tag == tag).ToList();
        return new GearPiece
        {
            Tag = tag,
            ItemType = Shared(same, p => p.ItemType, 1) as string,
            Rarity = Shared(same, p => p.Rarity, minPlayers) as string,
            Reforge = Shared(same, p => p.Reforge, minPlayers) as string,
            Stars = Shared(same, p => p.Stars, minPlayers) as int?,
            HotPotatoCount = Shared(same, p => p.HotPotatoCount, minPlayers) as int?,
            Recombobulated = Shared(same, p => p.Recombobulated, minPlayers) as bool?,
            Enchants = SharedEntries(same, p => p.Enchants, minPlayers),
            Gems = SharedEntries(same, p => p.Gems, minPlayers),
            Parts = SharedEntries(same, p => p.Parts, minPlayers),
            Applied = SharedEntries(same, p => p.Applied, minPlayers),
            Counters = ReachedCounters(same, minPlayers),
            Stats = LoreStats(same, p => p.Stats, minPlayers)
        };
    }

    public static GearPet? SharedPet(IReadOnlyList<GearSeen<GearPet>> pets, int minPlayers)
    {
        if (Shared(pets, p => p.Type, minPlayers) is not string type)
            return null;
        var same = pets.Where(p => p.Item.Type == type).ToList();
        var (tier, level) = TierAndLevel(same, minPlayers);
        return new GearPet
        {
            Type = type,
            Tier = tier,
            Level = level,
            HeldItem = Shared(same, p => p.HeldItem, minPlayers) as string,
            Stats = LoreStats(same, p => p.Stats, minPlayers)
        };
    }

    /// <summary>
    /// Tier and level as one combination at least k players reach together: the highest tier at least k players have
    /// (at least), then the highest level at least k of those players reach with a pet of at least that tier. Without a
    /// tier k players reach, the level is read over all sightings and the tier is null.
    /// </summary>
    private static (string? Tier, int Level) TierAndLevel(List<GearSeen<GearPet>> pets, int minPlayers)
    {
        var tierRank = KthHighest(pets.Where(p => GearRarities.Rank(p.Item.Tier) >= 0)
            .Select(p => (p.Player, (double)GearRarities.Rank(p.Item.Tier))), minPlayers);
        var withTier = tierRank is { } rank ? pets.Where(p => GearRarities.Rank(p.Item.Tier) >= rank).ToList() : pets;
        var level = (int)(KthHighest(withTier.Select(p => (p.Player, (double)p.Item.Level)), minPlayers) ?? 0);
        var tier = tierRank is { } r ? pets.First(p => GearRarities.Rank(p.Item.Tier) == (int)r).Item.Tier!.ToUpperInvariant() : null;
        return (tier, level);
    }

    /// <summary>
    /// The k-th highest of the players' highest values: at least k players reach it (like the counter buckets); null
    /// below k players.
    /// </summary>
    public static double? KthHighest(IEnumerable<(string Player, double Value)> values, int minPlayers)
    {
        var best = values.GroupBy(v => v.Player, StringComparer.Ordinal).Select(g => g.Max(v => v.Value))
            .OrderByDescending(v => v).ToList();
        return best.Count < minPlayers ? null : best[minPlayers - 1];
    }

    /// <summary>Per stat: the median over players of each player's hours weighted mean; stats of fewer than k players are left out.</summary>
    public static Dictionary<string, double> StatMedians(IEnumerable<GearObservationInput> members, int minPlayers)
        => GearStatistics.PlayerMedians(members
            .Where(m => m.Observation.StatsSource == GearObservationBuilder.StatsBaseGear)
            .Select(m => (m.Player, m.Hours, (IReadOnlyDictionary<string, double>?)m.Observation.Stats)), minPlayers);

    public static int Players<T>(IEnumerable<GearSeen<T>> seen) => seen.Select(s => s.Player).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The value the most distinct players share (at least k; ties: more hours, then ordinal), else null.</summary>
    private static object? Shared<TItem, TValue>(IEnumerable<GearSeen<TItem>> seen, Func<TItem, TValue> pick, int minPlayers)
        => seen.Select(s => (Value: (object?)pick(s.Item), s.Player, s.Hours))
            .Where(v => v.Value != null)
            .GroupBy(v => v.Value!)
            .Select(g => (Value: g.Key, Players: g.Select(v => v.Player).Distinct(StringComparer.Ordinal).Count(), Hours: g.Sum(v => v.Hours)))
            .Where(g => g.Players >= minPlayers)
            .OrderByDescending(g => g.Players).ThenByDescending(g => g.Hours)
            .ThenBy(g => g.Value.ToString(), StringComparer.Ordinal)
            .Select(g => g.Value)
            .FirstOrDefault();

    private static Dictionary<string, TValue> SharedEntries<TValue>(List<GearSeen<GearPiece>> seen, Func<GearPiece, Dictionary<string, TValue>?> pick, int minPlayers)
        where TValue : notnull
    {
        var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var key in seen.SelectMany(s => pick(s.Item)?.Keys ?? Enumerable.Empty<string>()).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = Shared(seen, p => pick(p) is { } values && values.TryGetValue(key, out var v) ? (object)v : null, minPlayers);
            if (value is TValue typed)
                result[key] = typed;
        }
        return result;
    }

    /// <summary>Per counter: the highest bucket at least k players reach (each player's highest bucket counts).</summary>
    private static Dictionary<string, long> ReachedCounters(List<GearSeen<GearPiece>> seen, int minPlayers)
        => seen.SelectMany(s => (s.Item.Counters ?? new()).Select(c => (s.Player, c.Key, Bucket: GearFingerprint.BucketCounter(c.Key, c.Value))))
            .GroupBy(c => (c.Player, c.Key))
            .Select(g => (g.Key.Key, Bucket: g.Max(c => c.Bucket)))
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Where(g => g.Count() >= minPlayers)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Bucket).OrderByDescending(b => b).ElementAt(minPlayers - 1));

    private static Dictionary<string, double> LoreStats<T>(IEnumerable<GearSeen<T>> seen, Func<T, Dictionary<string, double>?> stats, int minPlayers)
        => GearStatistics.PlayerMedians(seen.Select(s => (s.Player, s.Hours, (IReadOnlyDictionary<string, double>?)stats(s.Item))), minPlayers);
}
