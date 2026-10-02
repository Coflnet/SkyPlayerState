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
        if (KeyTierGained(items) is not { } tier)
            return location;
        // On the Crimson Isle (Scarleton) a key gain is a key purchase whatever else the period
        // holds; anywhere else (production: a KUUDRA_HOT_TIER_KEY at "Village") only a period that
        // is nothing but the key - a farming period that happens to receive one stays where it is.
        if (SkyblockZones.IslandOf(location) == "Crimson Isle" || !HasOtherRealGain(items))
            return TierZone(tier);
        return location;
    }

    /// <summary>True when the items hold a positive count of something that is neither a Kuudra key nor a pseudo tag.</summary>
    private static bool HasOtherRealGain(IReadOnlyDictionary<string, int> items)
        => items.Any(kv => kv.Value > 0 && !PseudoItems.IsPseudo(kv.Key) && !KeyTiers.Any(k => k.Tag == kv.Key));

    /// <summary>True for the locations where <see cref="ResolveLocation"/> reattributes chest-claim loot ("Dungeon Hub"/"Forgotten Skull").</summary>
    public static bool IsClaimLocation(string location)
    {
        var canonical = SkyblockZones.Canonical(location);
        return canonical == "Dungeon Hub" || canonical == "Forgotten Skull";
    }

    /// <summary>Longest single fragment that counts as Kuudra run time (anything longer is an idle/stale artifact).</summary>
    private static readonly TimeSpan MaxRunFragment = TimeSpan.FromHours(1);

    /// <summary>
    /// Bookkeeping for <see cref="Models.ExtractedInfo.KuudraPendingRunSeconds"/>: the run itself
    /// collects nothing so its tier-zone periods are never stored, and the claim period lasts seconds -
    /// the tier task would show loot over seconds of tracked time. Returns the new pending seconds and
    /// the time <paramref name="claimShift"/> the claim period's start has to move back by.
    /// Stale pending (no tier zone seen for <see cref="MaxClaimDelay"/>) is dropped first; an empty
    /// tier-zone fragment (<paramref name="emptyTierFragment"/>) adds its duration (non-positive or
    /// over an hour ignored); a claim period (<paramref name="claimedToTier"/>) consumes everything.
    /// A stored tier-zone period carries its own time, so it is neither added nor consumed.
    /// </summary>
    internal static double UpdatePendingRun(double pending, DateTime lastTierAt, DateTime now,
        bool emptyTierFragment, TimeSpan fragmentDuration, bool claimedToTier, out TimeSpan claimShift)
    {
        claimShift = TimeSpan.Zero;
        if (now - lastTierAt > MaxClaimDelay)
            pending = 0;
        if (emptyTierFragment && fragmentDuration > TimeSpan.Zero && fragmentDuration <= MaxRunFragment)
            pending += fragmentDuration.TotalSeconds;
        if (claimedToTier && pending > 0)
        {
            claimShift = TimeSpan.FromSeconds(pending);
            pending = 0;
        }
        return pending;
    }
}
