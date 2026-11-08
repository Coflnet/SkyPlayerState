using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The columns of a <c>task_gear_observations</c> row the setup derivation needs (a slim read model of
/// <see cref="GearObservation"/>, so a task's 13 weeks of rows stay small in memory).
/// </summary>
public sealed record GearObservationRow
{
    public static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    public static readonly IReadOnlyDictionary<string, double> NoStats = new Dictionary<string, double>();

    public string PlayerKey { get; init; } = string.Empty;
    public DateTime FoldedAt { get; init; }
    public double Seconds { get; init; }
    public double CommonCoins { get; init; }
    public double RareCoins { get; init; }
    public string Category { get; init; } = string.Empty;
    public string GearFingerprint { get; init; } = string.Empty;
    public IReadOnlySet<string> Flags { get; init; } = NoFlags;
    public string StatsSource { get; init; } = GearObservationBuilder.StatsNone;
    public IReadOnlyDictionary<string, double> Stats { get; init; } = NoStats;

    public static GearObservationRow From(GearObservation o) => new()
    {
        PlayerKey = o.PlayerKey,
        FoldedAt = o.FoldedAt,
        Seconds = o.Seconds,
        CommonCoins = o.CommonCoins,
        RareCoins = o.RareCoins,
        Category = o.Category,
        GearFingerprint = o.GearFingerprint,
        Flags = o.Flags ?? NoFlags,
        StatsSource = o.StatsSource,
        Stats = o.Stats ?? new()
    };
}

/// <summary>One observation with its stored gear and its setup keys (computed once, shared by clusters and effects).</summary>
public sealed class GearClusterInput
{
    public GearObservationRow Observation { get; }
    public ObservedGear Gear { get; }
    /// <summary>The keys by level (index 0 = L1), see <see cref="GearSetupKeys.For(GearObservationRow, ObservedGear)"/>.</summary>
    public GearSetupKey?[] Keys { get; }

    public GearClusterInput(GearObservationRow observation, ObservedGear gear, GearSetupKey?[]? keys = null)
    {
        Observation = observation;
        Gear = gear;
        Keys = keys ?? GearSetupKeys.For(observation, gear);
    }

    public double Hours => Observation.Seconds / 3600;
    public string Player => Observation.PlayerKey;
}

/// <summary>A setup key: its level and its named parts (tool, armor, pet, tool.enchants.harvesting, ...).</summary>
public sealed class GearSetupKey
{
    public int Level { get; }
    public IReadOnlyDictionary<string, string> Parts { get; }
    /// <summary>"L&lt;level&gt;|part=value|..." with the parts in ordinal order.</summary>
    public string Text { get; }

    public GearSetupKey(int level, SortedDictionary<string, string> parts)
    {
        Level = level;
        Parts = parts;
        Text = $"L{level}|" + string.Join("|", parts.Select(p => p.Key + "=" + p.Value));
    }
}

/// <summary>
/// Setup keys of an observation, coarse to fine. The levels are nested: an L2 key extends its L1 key, an L3 key its L2 key.
/// <list type="bullet">
/// <item>L1 = tool tag + armor tags (helmet, chestplate, leggings, boots);</item>
/// <item>L2 = L1 + pet type and rarity (+ a level 100 flag);</item>
/// <item>L3 = L2 + the rate relevant modifiers the category keeps (the observation's gear is already narrowed to them,
/// Turbo to the task's crop): enchant levels of every piece, the tool's reforge, parts and applied counts, and the
/// equipment tags. Combat categories stop at L2 (no per-modifier rates).</item>
/// </list>
/// Earned counters are never part of a key: the counter enchants (cultivating / farmed_cultivating, compact /
/// compact_blocks, expertise / expertise_kills, absorb / absorb_logs_chopped) carry their level in the enchant, which is
/// in the key; the remaining counters (mined_crops, levelable_lvl) grow while an item is used and would split one setup.
/// A level is null when one of its parts is stale or ambiguous (flags) or unknown: no tool, no armor piece at all, no pet
/// or a pet without rarity, no equipment piece at all. Absent armor or equipment cannot be told apart from never seen.
/// </summary>
public static class GearSetupKeys
{
    public const int ToolArmor = 1;
    public const int WithPet = 2;
    public const int WithModifiers = 3;

    private static readonly string[] ToolArmorFlags = ["armor_stale", "tool_stale", "tool_ambiguous"];

    /// <summary>The keys the input was built with.</summary>
    public static GearSetupKey?[] For(GearClusterInput input) => input.Keys;

    /// <summary>The keys by level (index 0 = L1); each is null when the observation cannot be keyed at that level.</summary>
    public static GearSetupKey?[] For(GearObservationRow observation, ObservedGear gear)
    {
        var l1 = ToolArmorParts(observation, gear);
        var l2 = l1 == null ? null : WithPetParts(observation, gear, l1);
        var l3 = l2 == null ? null : WithModifierParts(observation, gear, l2);
        return [Key(ToolArmor, l1), Key(WithPet, l2), Key(WithModifiers, l3)];
    }

    /// <summary>The finest level a category has: combat categories have no modifier level.</summary>
    public static int FinestLevel(string? category) => IsCombat(category) ? WithPet : WithModifiers;

    private static bool IsCombat(string? category)
        => category is GearModifierTable.Dungeon or GearModifierTable.Kuudra or GearModifierTable.Other;

    private static GearSetupKey? Key(int level, SortedDictionary<string, string>? parts)
        => parts == null ? null : new GearSetupKey(level, parts);

    private static SortedDictionary<string, string>? ToolArmorParts(GearObservationRow observation, ObservedGear gear)
    {
        if (ToolArmorFlags.Any(observation.Flags.Contains))
            return null;
        var slots = gear.Slots ?? new();
        var tool = slots.GetValueOrDefault(GearSnapshot.Tool);
        if (tool == null || !GearSnapshot.ArmorSlots.Any(slots.ContainsKey))
            return null;
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = tool.Tag,
            ["armor"] = JoinTags(slots, GearSnapshot.ArmorSlots)
        };
    }

    private static SortedDictionary<string, string>? WithPetParts(GearObservationRow observation, ObservedGear gear, SortedDictionary<string, string> l1)
    {
        var pet = gear.Pet;
        if (observation.Flags.Contains("pet_stale") || pet == null || string.IsNullOrEmpty(pet.Tier))
            return null;
        return new SortedDictionary<string, string>(l1, StringComparer.Ordinal)
        {
            ["pet"] = $"{pet.Type}:{pet.Tier}" + (pet.Level >= 100 ? ":100+" : "")
        };
    }

    private static SortedDictionary<string, string>? WithModifierParts(GearObservationRow observation, ObservedGear gear, SortedDictionary<string, string> l2)
    {
        var slots = gear.Slots;
        if (FinestLevel(observation.Category) < WithModifiers || observation.Flags.Contains("equipment_stale"))
            return null;
        if (!GearSnapshot.EquipmentSlots.Any(slots.ContainsKey))
            return null;
        var parts = new SortedDictionary<string, string>(l2, StringComparer.Ordinal)
        {
            ["equipment"] = JoinTags(slots, GearSnapshot.EquipmentSlots)
        };
        AddToolModifiers(parts, slots[GearSnapshot.Tool]);
        foreach (var slot in GearSnapshot.ArmorSlots.Concat(GearSnapshot.EquipmentSlots))
            if (slots.TryGetValue(slot, out var piece))
                AddEnchants(parts, slot, piece);
        return parts;
    }

    private static void AddToolModifiers(SortedDictionary<string, string> parts, GearPiece tool)
    {
        if (!string.IsNullOrEmpty(tool.Reforge))
            parts["tool.reforge"] = tool.Reforge;
        AddEnchants(parts, GearSnapshot.Tool, tool);
        foreach (var (key, tag) in tool.Parts ?? new())
            parts[$"tool.parts.{key}"] = tag;
        foreach (var (key, count) in tool.Applied ?? new())
            parts[$"tool.applied.{key}"] = count.ToString();
    }

    private static void AddEnchants(SortedDictionary<string, string> parts, string slot, GearPiece piece)
    {
        foreach (var (name, level) in piece.Enchants ?? new())
            parts[$"{slot}.enchants.{name}"] = level.ToString();
    }

    private static string JoinTags(Dictionary<string, GearPiece> slots, string[] order)
        => string.Join(",", order.Select(s => slots.GetValueOrDefault(s)?.Tag ?? "-"));
}
