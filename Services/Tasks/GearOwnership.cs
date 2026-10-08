using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Decides, centrally and once per request, whether the player owns the gear listed in
/// <see cref="MethodBreakdown.RequiredItems"/> and fills <see cref="RequiredItem.Owned"/>,
/// <see cref="MethodBreakdown.MissingItemsCost"/> and <see cref="MethodBreakdown.GearOwned"/>.
/// Purely additive: it never touches profit, sorting or accessibility.
/// </summary>
public static class GearOwnership
{
    /// <summary>
    /// Example melee weapons: the AOTD entries only name a typical starter weapon, anything at or above
    /// that tier does the job. Curated, so a miss means "unknown", never "missing".
    /// </summary>
    private static readonly HashSet<string> MeleeWeapons = new(StringComparer.Ordinal)
    {
        "ASPECT_OF_THE_DRAGON", "MIDAS_SWORD", "LIVID_DAGGER", "SHADOW_FURY", "GIANTS_SWORD", "REAPER_SCYTHE",
        "FLOWER_OF_TRUTH", "HYPERION", "SCYLLA", "ASTRAEA", "VALKYRIE", "TERMINATOR"
    };

    /// <summary>Tags that name a concrete item but are satisfied by any of the group's members.</summary>
    private static readonly string[][] EquivalentGroups =
    [
        ["HYPERION", "SCYLLA", "ASTRAEA", "VALKYRIE"],
        // the Voidgloom entries list VOID_SWORD as the alternative to TERMINATOR
        ["TERMINATOR", "VOID_SWORD"],
        // the spade tiers only make longer chains
        ["ANCESTRAL_SPADE", "ARCHAIC_SPADE", "DEIFIC_SPADE"],
        // Divan's Drill out-mines the Titanium Drill DR-X555 (higher tiers are covered by HasHigherTier)
        ["TITANIUM_DRILL_1", "DIVAN_DRILL"],
    ];

    /// <summary>Example entries: when none is owned this stays unknown (an unlisted equivalent may exist).</summary>
    private static readonly HashSet<string> ExampleOnly = new(StringComparer.Ordinal) { "ASPECT_OF_THE_DRAGON" };
    /// <summary>Worn armor is not part of the stored state (inventory is the 36 main slots only).</summary>
    private static readonly HashSet<string> WornGear = new(StringComparer.Ordinal) { "WITHER_CHESTPLATE", "TERROR_CHESTPLATE" };
    /// <summary>Bought per run, owning one says nothing about whether the method is unlocked.</summary>
    private static readonly HashSet<string> Consumables = new(StringComparer.Ordinal) { "KISMET_FEATHER", "SHARD_VIPER" };

    /// <summary>
    /// Collects every item tag the state holds: inventory, the hunting weapon and toolkit, pets
    /// (as <c>PET_&lt;TYPE&gt;</c>) and the given persisted storage containers (ender chest, backpacks).
    /// Not available in the state, so not covered: worn armor/equipment, wardrobe, accessory bag.
    /// </summary>
    public static HashSet<string> BuildOwnedTags(StateObject state, IEnumerable<IEnumerable<Item>> storage = null)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(IEnumerable<Item> items)
        {
            if (items == null)
                return;
            foreach (var item in items)
                if (!string.IsNullOrEmpty(item?.Tag))
                    owned.Add(item.Tag);
        }
        AddAll(state?.Inventory);
        if (state?.Storage != null)
            foreach (var container in state.Storage)
                AddAll(container);
        if (storage != null)
            foreach (var container in storage)
                AddAll(container);
        var info = state?.ExtractedInfo;
        if (info?.WeaponInHuntaxe?.Tag != null)
            owned.Add(info.WeaponInHuntaxe.Tag);
        AddAll(info?.HuntingToolkitItems);
        if (info?.Pets != null)
            foreach (var pet in info.Pets)
            {
                if (!string.IsNullOrWhiteSpace(pet?.Type))
                    owned.Add("PET_" + pet.Type.Trim().ToUpperInvariant().Replace(' ', '_'));
                if (!string.IsNullOrWhiteSpace(pet?.Tag))
                    owned.Add(pet.Tag);
            }
        return owned;
    }

    /// <summary>Annotates a result's breakdown in place. No-op when it has no breakdown.</summary>
    public static void Annotate(TaskResult result, TaskParams parameters)
    {
        var breakdown = result?.Breakdown;
        if (breakdown == null)
            return;
        var required = breakdown.RequiredItems;
        foreach (var item in required ?? [])
            item.Owned = IsOwned(item, parameters?.OwnedItemTags, parameters?.OwnedItemsIncomplete ?? false);
        if (required == null || required.Count == 0)
        {
            breakdown.GearOwned = null;
            breakdown.MissingItemsCost = null;
            return;
        }
        var missing = required.Where(r => r.Owned == false).ToList();
        if (missing.Count > 0)
        {
            breakdown.GearOwned = false;
            breakdown.MissingItemsCost = missing.Sum(r => r.EstimatedPrice);
        }
        else if (required.All(r => r.Owned == true))
        {
            breakdown.GearOwned = true;
            breakdown.MissingItemsCost = 0;
        }
        else
        {
            breakdown.GearOwned = null;
            breakdown.MissingItemsCost = null;
        }
    }

    /// <summary>True/false when decidable from the state, null when not.</summary>
    public static bool? IsOwned(RequiredItem item, HashSet<string> owned, bool incomplete = false)
    {
        var tag = item?.ItemTag;
        if (owned == null || string.IsNullOrEmpty(tag) || tag == "SHARD_VIPER")
            return null;
        if (Satisfied(item, owned))
            return true;
        if (incomplete || ExampleOnly.Contains(tag) || WornGear.Contains(tag) || Consumables.Contains(tag)
            || tag.StartsWith("PET_", StringComparison.Ordinal) || item.Category != null)
            return null;
        return false;
    }

    private static bool Satisfied(RequiredItem item, HashSet<string> owned)
    {
        var tag = item.ItemTag;
        if (owned.Contains(tag))
            return true;
        if (item.Category == "HUNT_WEAPON"
            && GearLadders.Ladders["HUNT_WEAPON"].Any(owned.Contains))
            return true;
        if (ExampleOnly.Contains(tag) && MeleeWeapons.Any(owned.Contains))
            return true;
        if (EquivalentGroups.Any(g => g.Contains(tag) && g.Any(owned.Contains)))
            return true;
        return HasHigherTier(tag, owned);
    }

    /// <summary>
    /// Tiered tools share a stem and end in a number (THEORETICAL_HOE_WHEAT_1 -&gt; _2/_3, TITANIUM_DRILL_1 -&gt;
    /// _2.., PUMPKIN_DICER -&gt; PUMPKIN_DICER_2): a higher tier of the same stem satisfies the requirement.
    /// </summary>
    private static bool HasHigherTier(string tag, HashSet<string> owned)
    {
        var stem = tag;
        var tier = 1;
        var cut = tag.LastIndexOf('_');
        if (cut > 0 && int.TryParse(tag.AsSpan(cut + 1), out var parsed))
        {
            stem = tag[..cut];
            tier = parsed;
        }
        foreach (var other in owned)
        {
            if (other.Length <= stem.Length + 1 || !other.StartsWith(stem + "_", StringComparison.Ordinal))
                continue;
            if (int.TryParse(other.AsSpan(stem.Length + 1), out var otherTier) && otherTier > tier)
                return true;
        }
        return false;
    }
}
