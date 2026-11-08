using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>The named parts of a setup (tool, armor, pet, tool.enchants.harvesting, ...) a modifier change is read from.</summary>
public sealed class GearChangeKey
{
    public IReadOnlyDictionary<string, string> Parts { get; }
    /// <summary>"part=value|..." with the parts in ordinal order.</summary>
    public string Text { get; }

    public GearChangeKey(SortedDictionary<string, string> parts)
    {
        Parts = parts;
        Text = string.Join("|", parts.Select(p => p.Key + "=" + p.Value));
    }
}

/// <summary>
/// The key <see cref="GearModifierEffects"/> compares between a player's adjacent periods: tool tag, armor tags, the pet
/// (type, rarity, a level 100 flag) and, for gathering categories, the rate relevant modifiers the category keeps (the
/// observation's gear is already narrowed to them, Turbo to the task's crop): enchant levels of every piece, the tool's
/// reforge, parts and applied counts, and the equipment tags. Combat categories stop at the pet (no per-modifier rates).
/// Earned counters are never part of the key: the counter enchants carry their level in the enchant, the remaining
/// counters grow while an item is used and would split one setup.
/// The key is null when a part is stale or ambiguous (flags) or unknown: no tool, no armor piece, no pet or a pet without
/// rarity, and for gathering categories no equipment piece.
/// </summary>
public static class GearChangeKeys
{
    private static readonly string[] ToolArmorFlags = ["armor_stale", "tool_stale", "tool_ambiguous"];

    public static GearChangeKey? For(GearObservationRow observation, ObservedGear gear)
    {
        var parts = ToolArmorParts(observation, gear);
        if (parts == null || !AddPet(parts, observation, gear))
            return null;
        if (IsCombat(observation.Category))
            return new GearChangeKey(parts);
        return AddModifiers(parts, observation, gear) ? new GearChangeKey(parts) : null;
    }

    public static bool IsCombat(string? category)
        => category is GearModifierTable.Dungeon or GearModifierTable.Kuudra or GearModifierTable.Other;

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

    private static bool AddPet(SortedDictionary<string, string> parts, GearObservationRow observation, ObservedGear gear)
    {
        var pet = gear.Pet;
        if (observation.Flags.Contains("pet_stale") || pet == null || string.IsNullOrEmpty(pet.Tier))
            return false;
        parts["pet"] = $"{pet.Type}:{pet.Tier}" + (pet.Level >= 100 ? ":100+" : "");
        return true;
    }

    private static bool AddModifiers(SortedDictionary<string, string> parts, GearObservationRow observation, ObservedGear gear)
    {
        var slots = gear.Slots;
        if (observation.Flags.Contains("equipment_stale") || !GearSnapshot.EquipmentSlots.Any(slots.ContainsKey))
            return false;
        parts["equipment"] = JoinTags(slots, GearSnapshot.EquipmentSlots);
        AddToolModifiers(parts, slots[GearSnapshot.Tool]);
        foreach (var slot in GearSnapshot.ArmorSlots.Concat(GearSnapshot.EquipmentSlots))
            if (slots.TryGetValue(slot, out var piece))
                AddEnchants(parts, slot, piece);
        return true;
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

/// <summary>Change keys per (fingerprint, flags, category) within one derivation: rows with the same gear share one key.</summary>
internal sealed class GearChangeKeyCache
{
    private readonly Dictionary<(string Fingerprint, string Flags, string Category), GearChangeKey?> keys = new();

    public GearObservationInput Input(GearObservationRow row, ObservedGear gear)
    {
        var id = (row.GearFingerprint, string.Join(",", row.Flags.Order(StringComparer.Ordinal)), row.Category);
        if (!keys.TryGetValue(id, out var cached))
            keys[id] = cached = GearChangeKeys.For(row, gear);
        return new GearObservationInput(row, gear, cached);
    }
}
