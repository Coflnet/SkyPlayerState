using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// One worn or held item reduced to the modifiers that can change a money making rate (see
/// <see cref="Services.Gear.GearModifierTable"/>). Shared contract with SkyApi's acquisition: the JSON names are fixed.
/// Never contains the item uuid, lore or display name.
/// </summary>
[MessagePackObject]
public class GearPiece
{
    /// <summary>Hypixel item tag (ExtraAttributes.id), e.g. THEORETICAL_HOE_WHEAT_3.</summary>
    [Key(0)][JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;
    /// <summary>Rarity name (COMMON .. DIVINE), recombobulation included.</summary>
    [Key(1)][JsonPropertyName("rarity")] public string? Rarity { get; set; }
    /// <summary>Reforge (ExtraAttributes.modifier), lowercase.</summary>
    [Key(2)][JsonPropertyName("reforge")] public string? Reforge { get; set; }
    /// <summary>Dungeon stars (upgrade_level, legacy dungeon_item_level). Combat categories only.</summary>
    [Key(3)][JsonPropertyName("stars")] public int? Stars { get; set; }
    /// <summary>Hot potato plus fuming potato books (hot_potato_count).</summary>
    [Key(4)][JsonPropertyName("hotPotatoCount")] public int? HotPotatoCount { get; set; }
    /// <summary>rarity_upgrades &gt; 0.</summary>
    [Key(5)][JsonPropertyName("recombobulated")] public bool? Recombobulated { get; set; }
    /// <summary>Lowercase NBT enchant name to level, only the category's rate relevant ones.</summary>
    [Key(6)][JsonPropertyName("enchants")] public Dictionary<string, int> Enchants { get; set; } = new();
    /// <summary>Gem slot (JASPER_0, COMBAT_0) to the gem item tag (FINE_JASPER_GEM).</summary>
    [Key(7)][JsonPropertyName("gems")] public Dictionary<string, string> Gems { get; set; } = new();
    /// <summary>NBT key (drill_part_engine, hook, line, sinker) to the part item tag (uppercase).</summary>
    [Key(8)][JsonPropertyName("parts")] public Dictionary<string, string> Parts { get; set; } = new();
    /// <summary>
    /// Earned counters (farmed_cultivating, compact_blocks, expertise_kills, absorb_logs_chopped, mined_crops, levelable_lvl).
    /// Raw in the live state; floored to buckets (see <see cref="Services.Gear.GearFingerprint.BucketCounter"/>) in stored observations.
    /// Cannot be bought onto a base item.
    /// </summary>
    [Key(9)][JsonPropertyName("counters")] public Dictionary<string, long> Counters { get; set; } = new();
    /// <summary>Item type from the lore footer ("FARMING TOOL", "DRILL", "FISHING ROD", "AXE", "HELMET", ...).</summary>
    [Key(10)][JsonPropertyName("itemType")] public string? ItemType { get; set; }
    /// <summary>
    /// Applied count/flag modifiers that are bought as items and applied (farming_for_dummies_count, levelable_overclocks,
    /// mithril_infusion, polarvoid, divan_powder_coating, tuned_transmission). Extension of the shared contract.
    /// </summary>
    [Key(11)][JsonPropertyName("applied")] public Dictionary<string, int> Applied { get; set; } = new();
    /// <summary>
    /// Rate relevant stats from the lore ("Farming Fortune: +82 (+25) (+5)" -> 82, the item's total), whitelisted per category
    /// (see <see cref="Services.Gear.GearModifierTable"/>). Percent stats without the % sign.
    /// </summary>
    [Key(12)][JsonPropertyName("stats")] public Dictionary<string, double> Stats { get; set; } = new();

    public GearPiece() { }

    public GearPiece(GearPiece other)
    {
        Tag = other.Tag;
        Rarity = other.Rarity;
        Reforge = other.Reforge;
        Stars = other.Stars;
        HotPotatoCount = other.HotPotatoCount;
        Recombobulated = other.Recombobulated;
        Enchants = other.Enchants == null ? new() : new(other.Enchants);
        Gems = other.Gems == null ? new() : new(other.Gems);
        Parts = other.Parts == null ? new() : new(other.Parts);
        Counters = other.Counters == null ? new() : new(other.Counters);
        ItemType = other.ItemType;
        Applied = other.Applied == null ? new() : new(other.Applied);
        Stats = other.Stats == null ? new() : new(other.Stats);
    }
}

/// <summary>The active pet as far as it affects a rate. Shared contract with SkyApi.</summary>
[MessagePackObject]
public class GearPet
{
    /// <summary>petInfo.type, e.g. ELEPHANT, MOOSHROOM_COW.</summary>
    [Key(0)][JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    /// <summary>Rarity (petInfo.tier), null when only the tab list named the pet.</summary>
    [Key(1)][JsonPropertyName("tier")] public string? Tier { get; set; }
    [Key(2)][JsonPropertyName("level")] public int Level { get; set; }
    [Key(3)][JsonPropertyName("heldItem")] public string? HeldItem { get; set; }
    /// <summary>Stats from the pet's lore ("Strength: +150"), like <see cref="GearPiece.Stats"/>; empty when only the tab named the pet.</summary>
    [Key(4)][JsonPropertyName("stats")] public Dictionary<string, double> Stats { get; set; } = new();

    public GearPet() { }

    public GearPet(GearPet other)
    {
        Type = other.Type;
        Tier = other.Tier;
        Level = other.Level;
        HeldItem = other.HeldItem;
        Stats = other.Stats == null ? new() : new(other.Stats);
    }
}

/// <summary>Values of <see cref="GearSnapshot.ToolEvidence"/>.</summary>
public static class GearToolEvidence
{
    /// <summary>The optimal tool (see <see cref="Services.Gear.OptimalTool"/>) and the mod reported it as held (<see cref="ChestView.HeldSlot"/>).</summary>
    public const string HeldSlot = "heldSlot";
    /// <summary>The inventory item of the task's tool types with the best primary stat: players use their best tool for a task.</summary>
    public const string Optimal = "optimal";
    /// <summary>No tool of the task's types in the inventory, or the task is unknown: the tool is left out of the setup key.</summary>
    public const string Ambiguous = "ambiguous";

    public static bool IsKnown(string? evidence) => evidence is HeldSlot or Optimal;
}

/// <summary>
/// What the player wears and uses, assembled from several uploads (see <see cref="Services.GearSnapshotListener"/>):
/// armor from the bare inventory or the Stats &amp; Equipment / Loadouts menu, equipment from those menus, the tool from the
/// hotbar, the pet from the Pets menu or the tab list. Each source keeps its own timestamp so an observation can drop
/// stale parts. Pieces are captured with the union of all categories' modifiers; observations filter them per task.
/// </summary>
[MessagePackObject]
public class GearSnapshot
{
    public const string Helmet = "helmet";
    public const string Chestplate = "chestplate";
    public const string Leggings = "leggings";
    public const string Boots = "boots";
    public const string Necklace = "necklace";
    public const string Cloak = "cloak";
    public const string Belt = "belt";
    public const string Gloves = "gloves";
    public const string Tool = "tool";

    public static readonly string[] ArmorSlots = [Helmet, Chestplate, Leggings, Boots];
    public static readonly string[] EquipmentSlots = [Necklace, Cloak, Belt, Gloves];

    /// <summary>Slot name (helmet, chestplate, leggings, boots, necklace, cloak, belt, gloves, tool) to the piece; absent = unknown or empty.</summary>
    [Key(0)][JsonPropertyName("slots")] public Dictionary<string, GearPiece> Slots { get; set; } = new();
    [Key(1)][JsonPropertyName("pet")] public GearPet? Pet { get; set; }
    /// <summary>How the tool was picked, see <see cref="GearToolEvidence"/>.</summary>
    [Key(2)][JsonPropertyName("toolEvidence")] public string? ToolEvidence { get; set; }
    [Key(3)][JsonPropertyName("armorAt")] public DateTime ArmorAt { get; set; }
    [Key(4)][JsonPropertyName("equipmentAt")] public DateTime EquipmentAt { get; set; }
    [Key(5)][JsonPropertyName("toolAt")] public DateTime ToolAt { get; set; }
    [Key(6)][JsonPropertyName("petAt")] public DateTime PetAt { get; set; }
    /// <summary>Distinct bait item tags in the player inventory (fishing covariate), sorted.</summary>
    [Key(7)][JsonPropertyName("bait")] public List<string> Bait { get; set; } = new();
    [Key(8)][JsonPropertyName("baitAt")] public DateTime BaitAt { get; set; }
    /// <summary>
    /// <see cref="Services.Gear.GearFingerprint.PieceKey"/> of the item in the held slot when the mod sent
    /// <see cref="ChestView.HeldSlot"/> ("" for an empty hand), else null. Only decides heldSlot vs optimal evidence.
    /// </summary>
    [Key(9)][JsonPropertyName("heldKey")] public string? HeldKey { get; set; }
    /// <summary>Offhand item of the 1.21 bare inventory, kept for the ownership check only (not part of observations).</summary>
    [Key(10)][JsonPropertyName("offhand")] public GearPiece? Offhand { get; set; }
    /// <summary>
    /// The inventory's tools (hotbar first, then main inventory; items of any tool type, capped), from which
    /// <see cref="Services.Gear.OptimalTool"/> picks the task's tool. Not part of observations or fingerprints.
    /// </summary>
    [Key(11)][JsonPropertyName("toolCandidates")] public List<GearPiece> ToolCandidates { get; set; } = new();

    [IgnoreMember][JsonIgnore]
    public GearPiece? ToolPiece => Slots?.GetValueOrDefault(Tool);

    public GearSnapshot() { }

    public GearSnapshot(GearSnapshot other)
    {
        Slots = other.Slots == null ? new() : other.Slots.ToDictionary(s => s.Key, s => new GearPiece(s.Value));
        Pet = other.Pet == null ? null : new GearPet(other.Pet);
        ToolEvidence = other.ToolEvidence;
        ArmorAt = other.ArmorAt;
        EquipmentAt = other.EquipmentAt;
        ToolAt = other.ToolAt;
        PetAt = other.PetAt;
        Bait = other.Bait == null ? new() : new(other.Bait);
        BaitAt = other.BaitAt;
        HeldKey = other.HeldKey;
        Offhand = other.Offhand == null ? null : new GearPiece(other.Offhand);
        ToolCandidates = other.ToolCandidates == null ? new() : other.ToolCandidates.Select(p => new GearPiece(p)).ToList();
    }
}

/// <summary>
/// Base stats of one "Stats &amp; Equipment" read: the rate relevant menu totals minus the lore stats of the gear shown with
/// them, clamped at 0, see <see cref="Services.Gear.GearBaseStats"/>.
/// </summary>
[MessagePackObject]
public class BaseStatsReading
{
    [Key(0)] public Dictionary<string, double> Stats { get; set; } = new();
    [Key(1)] public DateTime At { get; set; }
    /// <summary>Profile the read was on (<see cref="ExtractedInfo.CurrentProfileId"/>); stats are per profile.</summary>
    [Key(2)] public Guid ProfileId { get; set; }
    /// <summary>Lore stats of the held item that were subtracted: without a known session tool they are not added back.</summary>
    [Key(3)] public Dictionary<string, double> HeldStats { get; set; } = new();
    /// <summary>The pet shown with the read, with its lore stats: lends them to a session pet of the same type known without lore.</summary>
    [Key(4)] public GearPet? Pet { get; set; }

    public BaseStatsReading() { }

    public BaseStatsReading(BaseStatsReading other)
    {
        Stats = other.Stats == null ? new() : new(other.Stats);
        At = other.At;
        ProfileId = other.ProfileId;
        HeldStats = other.HeldStats == null ? new() : new(other.HeldStats);
        Pet = other.Pet == null ? null : new GearPet(other.Pet);
    }
}
