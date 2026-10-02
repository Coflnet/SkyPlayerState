using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Attributes Kuudra periods that happen outside the instance back to their tier zone:
/// chest loot claimed at the "Dungeon Hub"/"Forgotten Skull" and key purchases at Scarleton.
/// Pure/static; wired from <see cref="Services.CollectionListener.StoreLocationProfit"/>.
/// </summary>
public static class KuudraRewardAttribution
{
    /// <summary>How long after last being in a Kuudra tier zone a claim is still attributed to it.</summary>
    public static readonly TimeSpan MaxClaimDelay = TimeSpan.FromHours(2);

    private static readonly TimeSpan NegativeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Loot tags that mark a period as a Kuudra chest claim (same list as KuudraChestClaimsTask).</summary>
    public static readonly HashSet<string> ClaimLootTags =
    [
        "KUUDRA_TEETH", "KUUDRA_TENTACLE", "KUUDRA_MANDIBLE", "ESSENCE_CRIMSON",
        .. new[] { "CRIMSON", "AURORA", "TERROR", "FERVOR", "HOLLOW" }
            .SelectMany(set => new[] { "HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS" }.Select(piece => $"{set}_{piece}"))
    ];

    /// <summary>Key tag to tier, ascending tier order. Tags confirmed in NotEnoughUpdates-REPO items.</summary>
    private static readonly (string Tag, int Tier)[] KeyTiers =
    [
        ("KUUDRA_TIER_KEY", 1), ("KUUDRA_HOT_TIER_KEY", 2), ("KUUDRA_BURNING_TIER_KEY", 3),
        ("KUUDRA_FIERY_TIER_KEY", 4), ("KUUDRA_INFERNAL_TIER_KEY", 5)
    ];

    public static string TierZone(int tier) => $"Kuudra's Hollow (T{tier})";

    /// <summary>True for a Kuudra tier zone ("Kuudra's Hollow (T1)".."(T5)").</summary>
    public static bool IsTierZone(string zone)
    {
        var canonical = SkyblockZones.Canonical(zone);
        return canonical != null && KeyTiers.Any(k => canonical == TierZone(k.Tier));
    }

    /// <summary>Highest tier of a Kuudra key gained (positive count) in the items, or null.</summary>
    public static int? KeyTierGained(IReadOnlyDictionary<string, int> items)
    {
        if (items == null)
            return null;
        int? best = null;
        foreach (var (tag, tier) in KeyTiers)
            if (items.TryGetValue(tag, out var count) && count > 0)
                best = tier;
        return best;
    }

    public static bool HasClaimLoot(IReadOnlyDictionary<string, int> items)
        => items != null && items.Any(kv => kv.Value > 0 && ClaimLootTags.Contains(kv.Key));

    /// <summary>
    /// The location a period should be attributed to: the matching tier zone for a key purchase on
    /// the Crimson Isle, the last Kuudra tier (within <see cref="MaxClaimDelay"/>) for claim loot at
    /// the Dungeon Hub / Forgotten Skull, otherwise <paramref name="location"/> unchanged.
    /// </summary>
    public static string ResolveLocation(string location, IReadOnlyDictionary<string, int> items,
        string lastTierZone, DateTime lastTierAt, DateTime periodStart)
    {
        if (string.IsNullOrEmpty(location) || items == null)
            return location;
        var canonical = SkyblockZones.Canonical(location);
        if (canonical == "Dungeon Hub" || canonical == "Forgotten Skull")
        {
            if (string.IsNullOrEmpty(lastTierZone) || !HasClaimLoot(items))
                return location;
            var delay = periodStart - lastTierAt;
            if (delay < -NegativeTolerance || delay > MaxClaimDelay)
                return location;
            return lastTierZone;
        }
        if (SkyblockZones.IslandOf(location) == "Crimson Isle" && KeyTierGained(items) is { } tier)
            return TierZone(tier);
        return location;
    }
}
