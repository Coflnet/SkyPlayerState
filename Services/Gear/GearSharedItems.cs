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
/// <item>numeric and ordered modifiers (rarity, stars, hot potato count, recombobulated, each enchant level, applied count,
/// counter bucket) show the highest value at least k distinct players reach (each player's highest counts): a published
/// piece is a minimum that k players have at least, so the top players' upgrades are kept;</item>
/// <item>categorical modifiers (reforge, each gem slot and part) are shown only with a value at least k distinct players
/// share (the most shared value wins);</item>
/// <item>lore stats are each player's hours weighted mean, then the highest value at least k players reach;</item>
/// <item>the pet likewise: the type of at least k players; tier and level as one combination at least k of them reach
/// together (each player's highest counts, like the counters); the held item when k players share it; its lore stats from
/// a pet of at least the published tier. Unknown tiers are filled in first where they can be told (<see cref="TierFiller"/>).</item>
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
            Rarity = ReachedRarity(same, minPlayers),
            Reforge = Shared(same, p => p.Reforge, minPlayers) as string,
            Stars = Reached(same, p => p.Stars, minPlayers),
            HotPotatoCount = Reached(same, p => p.HotPotatoCount, minPlayers),
            Recombobulated = Reached(same, p => p.Recombobulated is { } r ? (r ? 1 : 0) : null, minPlayers) is { } recomb ? recomb == 1 : null,
            Enchants = ReachedEntries(same, p => p.Enchants, minPlayers),
            Gems = SharedEntries(same, p => p.Gems, minPlayers),
            Parts = SharedEntries(same, p => p.Parts, minPlayers),
            Applied = ReachedEntries(same, p => p.Applied, minPlayers),
            Counters = ReachedCounters(same, minPlayers),
            Stats = ReachedStats(same, p => p.Stats, minPlayers)
        };
    }

    /// <summary>The highest value at least k players reach (each player's highest counts); null below k players with a value.</summary>
    private static int? Reached<T>(IEnumerable<GearSeen<T>> seen, Func<T, int?> pick, int minPlayers)
        => KthHighest(seen.Where(s => pick(s.Item) != null).Select(s => (s.Player, (double)pick(s.Item)!.Value)), minPlayers) is { } value
            ? (int)value : null;

    private static string? ReachedRarity(List<GearSeen<GearPiece>> seen, int minPlayers)
    {
        var rank = Reached(seen, p => GearRarities.Rank(p.Rarity) >= 0 ? GearRarities.Rank(p.Rarity) : (int?)null, minPlayers);
        return rank == null ? null : seen.First(s => GearRarities.Rank(s.Item.Rarity) == rank).Item.Rarity!.ToUpperInvariant();
    }

    /// <summary>Per key (enchant, applied count): the highest level at least k of the players that have the key reach.</summary>
    private static Dictionary<string, int> ReachedEntries(List<GearSeen<GearPiece>> seen, Func<GearPiece, Dictionary<string, int>?> pick, int minPlayers)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in seen.SelectMany(s => pick(s.Item)?.Keys ?? Enumerable.Empty<string>()).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            if (Reached(seen, p => pick(p) is { } values && values.TryGetValue(key, out var v) ? v : null, minPlayers) is { } level)
                result[key] = level;
        }
        return result;
    }

    /// <summary>
    /// Per stat: each player's hours weighted mean, then the highest value at least k players reach (consistent with the
    /// reached modifiers: a published piece is what at least k players have at least).
    /// </summary>
    private static Dictionary<string, double> ReachedStats<T>(IEnumerable<GearSeen<T>> seen, Func<T, Dictionary<string, double>?> stats, int minPlayers)
        => seen.Where(s => s.Hours > 0)
            .SelectMany(s => (stats(s.Item) ?? new()).Select(v => (s.Player, Name: v.Key, v.Value, s.Hours)))
            .GroupBy(v => (v.Player, v.Name))
            .Select(g => (g.Key.Player, g.Key.Name, Mean: g.Sum(v => v.Value * v.Hours) / g.Sum(v => v.Hours)))
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Value: KthHighest(g.Select(v => (v.Player, v.Mean)), minPlayers)))
            .Where(v => v.Value != null)
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToDictionary(v => v.Name, v => v.Value!.Value, StringComparer.Ordinal);

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
            Stats = ReachedStats(AtLeastTier(same, tier), p => p.Stats, minPlayers)
        };
    }

    /// <summary>The sightings of a pet of at least the tier, all of them without a tier.</summary>
    private static List<GearSeen<GearPet>> AtLeastTier(List<GearSeen<GearPet>> pets, string? tier)
        => tier == null ? pets : pets.Where(p => GearRarities.Rank(p.Item.Tier) >= GearRarities.Rank(tier)).ToList();

    /// <summary>
    /// Fills in unknown pet tiers (a pet known only from the tab list has none) where they can be told, so such pets are
    /// published with a rarity (and score their perks) instead of without one (scoring 0): a pet of unknown tier takes the
    /// highest tier the same player's pet of that type is seen with; a player never seen with a tier for the type takes
    /// the type's lowest known tier when at least k players are seen with one (all of them reach that tier, so it shows
    /// nothing of one player; it is a lower bound of the known players, an assumption for the unknown one). Otherwise the
    /// tier stays unknown.
    /// </summary>
    public static Func<GearPet, string, GearPet> TierFiller(IReadOnlyList<GearSeen<GearPet>> pets, int minPlayers)
    {
        var known = pets.Where(p => GearRarities.Rank(p.Item.Tier) >= 0).ToList();
        var playerTier = known.GroupBy(p => (p.Player, p.Item.Type))
            .ToDictionary(g => g.Key, g => g.MaxBy(p => GearRarities.Rank(p.Item.Tier))!.Item.Tier!);
        var typeFloor = known.GroupBy(p => p.Item.Type, StringComparer.Ordinal)
            .Where(g => Players(g) >= minPlayers)
            .ToDictionary(g => g.Key, g => g.MinBy(p => GearRarities.Rank(p.Item.Tier))!.Item.Tier!, StringComparer.Ordinal);
        return (pet, player) =>
        {
            if (GearRarities.Rank(pet.Tier) >= 0)
                return pet;
            var tier = playerTier.GetValueOrDefault((player, pet.Type)) ?? typeFloor.GetValueOrDefault(pet.Type);
            return tier == null ? pet : new GearPet(pet) { Tier = tier.ToUpperInvariant() };
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
}
