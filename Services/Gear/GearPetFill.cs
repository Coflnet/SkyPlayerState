using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Fills in what pet sightings lack, for the piece catalog (pure). Only a pet item read from a menu has lore stats: a pet
/// named by the tab list has no stats, no tier and no held item, one from the Pets menu's list (<see cref="ExtractedInfo.PetState"/>)
/// a tier and a held item but no stats. Their variants (type, tier, level, held item "-") held fewer than k players with
/// stats, so 223 of 430 published pets had empty stats and scored 0 (live check 2026-11-10). A held item's bonus is part
/// of the lore stats and cannot be told apart, so stats are only ever taken from sightings with the same held item.
/// </summary>
public static class GearPetFill
{
    /// <summary>A stat may lie this share outside the range of a tier's sightings and still fit it: levels are stored in steps of 10.</summary>
    public const double StatSlack = 0.1;

    public static bool HasStats(GearPet pet) => pet.Stats is { Count: > 0 };

    /// <summary>
    /// Whether a sighting's missing held item means none: the Pets menu (a tier is known) shows the held item, the tab
    /// list (no tier) names none, so there a missing one is unknown.
    /// </summary>
    public static bool HeldItemKnown(GearPet seen) => GearRarities.Rank(seen.Tier) >= 0;

    /// <summary>
    /// A stat-less sighting of a known tier (possibly filled in) takes the stats of the same player's sighting of the same
    /// type and tier (at most at its level, the highest such) with the same held item; when its held item is unknown
    /// (<see cref="HeldItemKnown"/> of the sighting as seen) with any held item, which it then takes: the tab list named
    /// the pet the player's Pets menu showed with its lore.
    /// </summary>
    public static Func<GearPet, string, bool, GearPet> StatFiller(IReadOnlyList<GearSeen<GearPet>> pets)
    {
        var own = pets.Where(p => HasStats(p.Item) && GearRarities.Rank(p.Item.Tier) >= 0)
            .GroupBy(p => (p.Player, p.Item.Type))
            .ToDictionary(g => g.Key, g => g.ToList());
        return (pet, player, heldKnown) =>
        {
            if (HasStats(pet) || !own.TryGetValue((player, pet.Type), out var sightings))
                return pet;
            var source = sightings.Where(s => CanFill(s.Item, pet, heldKnown))
                .OrderByDescending(s => s.Item.Level).ThenByDescending(s => s.Hours).FirstOrDefault();
            return source == null ? pet : new GearPet(pet) { HeldItem = source.Item.HeldItem, Stats = new(source.Item.Stats) };
        };
    }

    private static bool CanFill(GearPet source, GearPet pet, bool heldKnown)
        => SameTier(source, pet) && source.Level <= pet.Level
            && (source.HeldItem == pet.HeldItem || (!heldKnown && pet.HeldItem == null));

    /// <summary>
    /// Stats for a published pet whose own sightings have none: the per-stat minimum over the k best players' sightings
    /// (<see cref="GearSharedItems.InstanceStats{T}"/>) of the same type and tier with the same held item (none is one) at
    /// most at its level, so the values are a lower bound k players reach. Empty below k such players or without a tier.
    /// </summary>
    public static Dictionary<string, double> SimilarStats(GearPet pet, IEnumerable<GearSeen<GearPet>> pets, int minPlayers,
        Func<IReadOnlyDictionary<string, double>, double> rank)
    {
        if (GearRarities.Rank(pet.Tier) < 0)
            return new Dictionary<string, double>(StringComparer.Ordinal);
        var similar = pets.Where(p => p.Item.Type == pet.Type && SameTier(p.Item, pet) && p.Item.HeldItem == pet.HeldItem
            && p.Item.Level <= pet.Level);
        return GearSharedItems.InstanceStats(similar, p => p.Stats, minPlayers, rank);
    }

    /// <summary>Whether a sighting of the pet's type without a held item has lore stats.</summary>
    public static bool AnyStatsWithoutHeldItem(GearPet pet, IEnumerable<GearSeen<GearPet>> pets)
        => pets.Any(p => p.Item.Type == pet.Type && p.Item.HeldItem == null && HasStats(p.Item));

    /// <summary>A pet without stats of a type other sightings show with stats: its stats were not seen, they are not none.</summary>
    public static bool StatsUnseen(GearPet pet, IEnumerable<GearSeen<GearPet>> pets)
        => !HasStats(pet) && pets.Any(p => p.Item.Type == pet.Type && HasStats(p.Item));

    /// <summary>
    /// The tier ranks (ascending) a pet's lore stats fit, compared with the known tier sightings of its type at its level
    /// with the same held item (any held item when its own is unknown): a tier fits when every stat lies within the range
    /// of that tier's sightings, widened by <see cref="StatSlack"/>. Null when there is nothing to compare (no stats, no
    /// such sightings); empty when the stats fit no tier.
    /// </summary>
    public static List<int>? FittingTiers(GearPet pet, IEnumerable<GearSeen<GearPet>> known)
    {
        if (!HasStats(pet))
            return null;
        var comparable = known.Where(k => IsComparable(k.Item, pet)).ToList();
        if (comparable.Count == 0)
            return null;
        return comparable.GroupBy(k => GearRarities.Rank(k.Item.Tier))
            .Where(tier => Fits(pet.Stats, tier.Select(k => k.Item.Stats).ToList()))
            .Select(tier => tier.Key)
            .Order()
            .ToList();
    }

    private static bool IsComparable(GearPet known, GearPet pet)
        => known.Type == pet.Type && known.Level == pet.Level && HasStats(known) && GearRarities.Rank(known.Tier) >= 0
            && (pet.HeldItem == null || known.HeldItem == pet.HeldItem);

    private static bool Fits(Dictionary<string, double> stats, List<Dictionary<string, double>> tier)
    {
        var names = stats.Keys.Union(tier.SelectMany(t => t.Keys));
        return names.All(name => Within(stats.GetValueOrDefault(name), tier.Select(t => t.GetValueOrDefault(name)).ToList()));
    }

    private static bool Within(double value, List<double> range)
    {
        var (low, high) = (range.Min(), range.Max());
        return value >= low - StatSlack * Math.Abs(low) - 1e-9 && value <= high + StatSlack * Math.Abs(high) + 1e-9;
    }

    private static bool SameTier(GearPet a, GearPet b) => string.Equals(a.Tier, b.Tier, StringComparison.OrdinalIgnoreCase);
}
