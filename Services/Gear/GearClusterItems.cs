using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The item specs of a cluster, built so that no single player's item shows (k = <c>minPlayers</c>):
/// <list type="bullet">
/// <item>a slot shows the tag most players have, and only when at least k distinct players have that tag;</item>
/// <item>of that tag, every modifier (rarity, reforge, stars, hot potato count, recombobulated, each enchant, gem slot,
/// part and applied count) is shown only with a value at least k distinct players share (the most shared value wins);
/// a counter shows the highest bucket at least k players reach;</item>
/// <item>lore stats are each player's hours weighted mean, then the median over at least k players;</item>
/// <item>the pet likewise: the type of at least k players, tier and held item when shared, the level when shared
/// (else 0 = not published).</item>
/// </list>
/// Stat medians come from "base+gear" observations, per player first. <paramref name="round"/> rounds the published
/// stat values (clusters below 5 players).
/// </summary>
public static class GearClusterItems
{
    private sealed record Seen<T>(T Item, double Hours, string Player);

    public static Dictionary<string, GearPiece> Slots(IReadOnlyList<GearClusterInput> members, int minPlayers, Func<double, double>? round = null)
    {
        var result = new Dictionary<string, GearPiece>(StringComparer.Ordinal);
        var bySlot = members.SelectMany(m => (m.Gear.Slots ?? new()).Select(s => (Slot: s.Key, Seen: new Seen<GearPiece>(s.Value, m.Hours, m.Player))))
            .GroupBy(s => s.Slot, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var slot in bySlot)
        {
            var piece = SharedPiece(slot.Select(s => s.Seen).ToList(), minPlayers, round ?? Identity);
            if (piece != null)
                result[slot.Key] = piece;
        }
        return result;
    }

    public static GearPet? Pet(IReadOnlyList<GearClusterInput> members, int minPlayers, Func<double, double>? round = null)
    {
        var pets = members.Where(m => m.Gear.Pet != null).Select(m => new Seen<GearPet>(m.Gear.Pet!, m.Hours, m.Player)).ToList();
        var type = Shared(pets, p => p.Type, minPlayers) as string;
        if (type == null)
            return null;
        var same = pets.Where(p => p.Item.Type == type).ToList();
        return new GearPet
        {
            Type = type,
            Tier = Shared(same, p => p.Tier, minPlayers) as string,
            Level = Shared(same, p => p.Level, minPlayers) as int? ?? 0,
            HeldItem = Shared(same, p => p.HeldItem, minPlayers) as string,
            Stats = Rounded(LoreStats(same, p => p.Stats, minPlayers), round ?? Identity)
        };
    }

    /// <summary>Per stat: the median over players of each player's hours weighted mean; stats of fewer than k players are left out.</summary>
    public static Dictionary<string, double> StatMedians(IReadOnlyList<GearClusterInput> members, int minPlayers)
        => GearStatistics.PlayerMedians(members
            .Where(m => m.Observation.StatsSource == GearObservationBuilder.StatsBaseGear)
            .Select(m => (m.Player, m.Hours, (IReadOnlyDictionary<string, double>?)m.Observation.Stats)), minPlayers);

    private static double Identity(double value) => value;

    private static GearPiece? SharedPiece(List<Seen<GearPiece>> seen, int minPlayers, Func<double, double> round)
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
            Stats = Rounded(LoreStats(same, p => p.Stats, minPlayers), round)
        };
    }

    /// <summary>The value the most distinct players share (at least k; ties: more hours, then ordinal), else null.</summary>
    private static object? Shared<TItem, TValue>(IEnumerable<Seen<TItem>> seen, Func<TItem, TValue> pick, int minPlayers)
        => seen.Select(s => (Value: (object?)pick(s.Item), s.Player, s.Hours))
            .Where(v => v.Value != null)
            .GroupBy(v => v.Value!)
            .Select(g => (Value: g.Key, Players: g.Select(v => v.Player).Distinct(StringComparer.Ordinal).Count(), Hours: g.Sum(v => v.Hours)))
            .Where(g => g.Players >= minPlayers)
            .OrderByDescending(g => g.Players).ThenByDescending(g => g.Hours)
            .ThenBy(g => g.Value.ToString(), StringComparer.Ordinal)
            .Select(g => g.Value)
            .FirstOrDefault();

    private static Dictionary<string, TValue> SharedEntries<TValue>(List<Seen<GearPiece>> seen, Func<GearPiece, Dictionary<string, TValue>?> pick, int minPlayers)
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
    private static Dictionary<string, long> ReachedCounters(List<Seen<GearPiece>> seen, int minPlayers)
        => seen.SelectMany(s => (s.Item.Counters ?? new()).Select(c => (s.Player, c.Key, Bucket: GearFingerprint.BucketCounter(c.Key, c.Value))))
            .GroupBy(c => (c.Player, c.Key))
            .Select(g => (g.Key.Key, Bucket: g.Max(c => c.Bucket)))
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Where(g => g.Count() >= minPlayers)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Bucket).OrderByDescending(b => b).ElementAt(minPlayers - 1));

    private static Dictionary<string, double> LoreStats<T>(IEnumerable<Seen<T>> seen, Func<T, Dictionary<string, double>?> stats, int minPlayers)
        => GearStatistics.PlayerMedians(seen.Select(s => (s.Player, s.Hours, (IReadOnlyDictionary<string, double>?)stats(s.Item))), minPlayers);

    private static Dictionary<string, double> Rounded(Dictionary<string, double> stats, Func<double, double> round)
        => stats.ToDictionary(s => s.Key, s => round(s.Value), StringComparer.Ordinal);
}
