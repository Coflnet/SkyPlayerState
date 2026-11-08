using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services.Gear;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Maintains <see cref="ExtractedInfo.CurrentGear"/> from the uploads that show gear:
/// <list type="bullet">
/// <item>every view with the player inventory: the tool candidates and the task's optimal tool (<see cref="OptimalTool"/>), the bait;</item>
/// <item>the bare inventory (45/46 slots): the worn armor (slots 5-8) and the offhand;</item>
/// <item>"Stats &amp; Equipment" and every "Loadouts" page: equipment, armor and the active pet (slots see
/// <see cref="WornGearListener"/>); "Stats &amp; Equipment" also shows the held item in slot 2 and the stat totals, from which
/// <see cref="ExtractedInfo.BaseStats"/> is derived (<see cref="GearBaseStats"/>);</item>
/// <item>"Pets": the active pet with its lore stats.</item>
/// </list>
/// Pieces keep the union of all categories' modifiers and stats (<see cref="GearNormalizer"/>), no lore text and no uuid.
/// </summary>
public class GearSnapshotListener : UpdateListener
{
    /// <summary>Gear capture is optional enrichment, a failure must not stop the state save.</summary>
    public override bool Optional => true;

    private const int MaxBait = 8;
    /// <summary>Slot of the "Stats &amp; Equipment" menu showing the held item (the SkyBlock Menu star when opened from it).</summary>
    public const int StatsMenuHeldSlot = 2;
    private const int StatsMenuPetSlot = 47;

    public override Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest?.Items == null || chest.Items.Count == 0)
            return Task.CompletedTask;
        var info = args.currentState.ExtractedInfo;
        var now = args.msg.ReceivedAt == default ? DateTime.UtcNow : args.msg.ReceivedAt;
        var gear = info.CurrentGear == null ? new GearSnapshot() : new GearSnapshot(info.CurrentGear);
        var changed = ReadMenus(gear, chest, info, now);
        changed |= ReadPlayerInventory(gear, info, chest, now);
        if (!changed)
            return Task.CompletedTask;
        info.CurrentGear = gear;
        if (IsStatsMenu(chest))
            StoreBaseStats(info, chest.Items, chest.HeldSlot, now);
        return Task.CompletedTask;
    }

    private static bool IsStatsMenu(ChestView chest) => WornGearListener.WornGearPetSlot(chest.Name) == StatsMenuPetSlot;

    private static bool ReadMenus(GearSnapshot gear, ChestView chest, ExtractedInfo info, DateTime now)
    {
        if (WornGearListener.WornGearPetSlot(chest.Name) is { } petSlot)
        {
            ReadWornMenu(gear, chest.Items, petSlot, now);
            return true;
        }
        if (MenuTitleNormalizer.StripColors(chest.Name ?? "").Trim() != "Pets")
            return false;
        return ReadActivePet(gear, chest.Items, info, now);
    }

    internal static void ReadWornMenu(GearSnapshot gear, IReadOnlyList<Item> items, int petSlot, DateTime now)
    {
        SetSlots(gear, items, WornGearListener.EquipmentMenuSlots, GearSnapshot.EquipmentSlots);
        SetSlots(gear, items, WornGearListener.ArmorMenuSlots, GearSnapshot.ArmorSlots);
        gear.EquipmentAt = now;
        gear.ArmorAt = now;
        ReadWornPet(gear, petSlot < items.Count ? items[petSlot] : null, now);
    }

    /// <summary>The pet slot holds the active pet; no pet item there means no pet is summoned.</summary>
    private static void ReadWornPet(GearSnapshot gear, Item? slot, DateTime now)
    {
        gear.Pet = GearNormalizer.NormalizePet(slot);
        gear.PetAt = now;
    }

    private static void SetSlots(GearSnapshot gear, IReadOnlyList<Item> items, int[] indices, string[] slots)
    {
        for (var i = 0; i < indices.Length; i++)
            SetSlot(gear, slots[i], indices[i] < items.Count ? items[indices[i]] : null);
    }

    /// <summary>An empty slot clears the piece; a piece read without modifiers does not replace the same item read with them.</summary>
    private static void SetSlot(GearSnapshot gear, string slot, Item? item)
    {
        var piece = GearNormalizer.Normalize(item);
        if (piece == null)
        {
            gear.Slots.Remove(slot);
            return;
        }
        if (gear.Slots.TryGetValue(slot, out var known) && known.Tag == piece.Tag && GearSessionTracker.IsBare(piece))
            return;
        gear.Slots[slot] = piece;
    }

    /// <summary>The summoned pet of the Pets page (petInfo.active, with its lore stats), else the one ActivePetListener found.</summary>
    private static bool ReadActivePet(GearSnapshot gear, IReadOnlyList<Item> items, ExtractedInfo info, DateTime now)
    {
        var pet = GearNormalizer.NormalizePet(items.FirstOrDefault(GearNormalizer.IsActivePet))
            ?? GearNormalizer.FromPetState(info.Pets?.FirstOrDefault(p => p.IsActive));
        if (pet == null)
            return false;
        gear.Pet = pet;
        gear.PetAt = now;
        return true;
    }

    private static bool ReadPlayerInventory(GearSnapshot gear, ExtractedInfo info, ChestView chest, DateTime now)
    {
        if (chest.Items.Count < InventoryLayout.PlayerSlots)
            return false;
        ReadBareArmor(gear, chest.Items, now);
        gear.HeldKey = HeldKey(chest);
        ReadTool(gear, info, chest.Items, now);
        ReadBait(gear, chest.Items, now);
        if (chest.Items.Count == InventoryLayout.BareWithOffhandSlots)
            gear.Offhand = GearNormalizer.Normalize(InventoryLayout.Offhand(chest.Items));
        return true;
    }

    /// <summary>The held item of this upload: the mod's held slot, else slot 2 of "Stats &amp; Equipment"; null when unknown.</summary>
    private static string? HeldKey(ChestView chest)
    {
        var slot = chest.HeldSlot ?? (IsStatsMenu(chest) ? StatsMenuHeldSlot : null);
        if (slot is not { } index || index < 0 || index >= chest.Items.Count)
            return null;
        var held = GearNormalizer.Normalize(chest.Items[index]);
        return held == null ? "" : GearFingerprint.PieceKey(held);
    }

    private static readonly string[] ArmorTypes = ["HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS"];

    /// <summary>
    /// Armor of a bare inventory. Read only when at least one slot holds an item typed as that slot's armor and none holds
    /// anything else: a 45 slot view can be a one row container, and four empty slots there say nothing about the armor.
    /// </summary>
    internal static void ReadBareArmor(GearSnapshot gear, IReadOnlyList<Item> items, DateTime now)
    {
        var armor = InventoryLayout.Armor(items);
        if (armor.Count != 4 || !LooksLikeArmor(armor))
            return;
        for (var i = 0; i < 4; i++)
            SetSlot(gear, GearSnapshot.ArmorSlots[i], armor[i]);
        gear.ArmorAt = now;
    }

    private static bool LooksLikeArmor(IReadOnlyList<Item?> armor)
    {
        var typed = 0;
        for (var i = 0; i < armor.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(armor[i]?.Tag))
                continue;
            var type = ItemFooter.ItemType(armor[i]);
            if (type != null && type != ArmorTypes[i])
                return false;
            if (type == ArmorTypes[i])
                typed++;
        }
        return typed > 0;
    }

    /// <summary>Keeps the inventory's tools and the current task's optimal one (the session re-picks for its own task).</summary>
    internal static void ReadTool(GearSnapshot gear, ExtractedInfo info, IReadOnlyList<Item> items, DateTime now)
    {
        gear.ToolCandidates = OptimalTool.Candidates(items);
        gear.ToolAt = now;
        var tool = OptimalTool.Pick(gear.ToolCandidates, info.CurrentTask ?? info.CurrentSession?.DetectedTask);
        gear.ToolEvidence = OptimalTool.Evidence(tool, gear.HeldKey);
        if (tool == null)
            gear.Slots.Remove(GearSnapshot.Tool);
        else
            gear.Slots[GearSnapshot.Tool] = tool;
    }

    private static void ReadBait(GearSnapshot gear, IReadOnlyList<Item> items, DateTime now)
    {
        gear.Bait = InventoryLayout.PlayerInventory(items)
            .Where(i => !string.IsNullOrWhiteSpace(i?.Tag) && ItemFooter.ItemType(i) == "BAIT")
            .Select(i => i.Tag).Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(MaxBait).ToList();
        gear.BaitAt = now;
    }

    /// <summary>Stat totals of "Stats &amp; Equipment" minus the gear shown in it, see <see cref="GearBaseStats"/>.</summary>
    /// <summary>
    /// Stat totals of "Stats &amp; Equipment" minus the gear shown in it, see <see cref="GearBaseStats"/>. Not inside a dungeon or
    /// Kuudra run, where the stats are scaled (dungeon stat boost, Kuudra buffs).
    /// </summary>
    private static void StoreBaseStats(ExtractedInfo info, IReadOnlyList<Item> items, int? heldSlot, DateTime now)
    {
        var totals = PlayerStatsListener.ParseStats(items);
        if (totals.Count == 0 || IsInInstance(info))
            return;
        var held = HeldPiece(items, heldSlot);
        var pet = StatsMenuPetSlot < items.Count ? GearNormalizer.NormalizePet(items[StatsMenuPetSlot]) : null;
        info.BaseStats = new BaseStatsReading
        {
            Stats = GearBaseStats.Derive(totals, StatsMenuGear(items, held, pet)),
            At = now,
            ProfileId = info.CurrentProfileId,
            HeldStats = held?.Stats ?? new(),
            Pet = pet
        };
    }

    internal static bool IsInInstance(ExtractedInfo info)
    {
        static bool Instance(string? zone) => zone != null
            && (zone.Contains("Catacombs", StringComparison.OrdinalIgnoreCase) || zone.Contains("Kuudra", StringComparison.OrdinalIgnoreCase));
        return Instance(info.CurrentLocation) || Instance(info.CurrentIsland);
    }

    /// <summary>The held item: slot 2, else the mod's held slot.</summary>
    private static GearPiece? HeldPiece(IReadOnlyList<Item> items, int? heldSlot)
    {
        var index = !string.IsNullOrWhiteSpace(items.ElementAtOrDefault(StatsMenuHeldSlot)?.Tag) ? StatsMenuHeldSlot : heldSlot ?? -1;
        return index >= 0 && index < items.Count ? GearNormalizer.Normalize(items[index]) : null;
    }

    /// <summary>The stat sources the menu shows: equipment, armor, the held item and the pet.</summary>
    private static IEnumerable<IReadOnlyDictionary<string, double>> StatsMenuGear(IReadOnlyList<Item> items, GearPiece? held, GearPet? pet)
    {
        var slots = WornGearListener.EquipmentMenuSlots.Concat(WornGearListener.ArmorMenuSlots);
        foreach (var slot in slots.Where(s => s < items.Count))
            if (GearNormalizer.Normalize(items[slot]) is { } piece)
                yield return piece.Stats;
        if (held != null)
            yield return held.Stats;
        if (pet != null)
            yield return pet.Stats;
    }
}
