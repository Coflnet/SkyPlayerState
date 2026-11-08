using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Keeps a task session's gear: the snapshot at the start, filled in with parts seen later (a slot, the pet or the tool
/// that was unknown), refreshed while nothing known changes. A confirmed part that changes (another piece, other modifiers,
/// another pet, another known tool) sets <see cref="TaskSession.GearChanged"/> and freezes the gear: the session then
/// mixes two setups and is not observed.
/// <para>
/// Parts read before <see cref="TaskSession.GearSince"/> (the moment the session was created) are provisional: they may
/// predate a wardrobe swap or come from the previous task, so a newer reading replaces them without counting as a
/// change. A tool whose item type is not one of the session category's tool types (the hoe remembered into a mining
/// session) is ignored.
/// </para>
/// </summary>
public static class GearSessionTracker
{
    public static void Track(TaskSession? session, GearSnapshot? current, DateTime now)
    {
        if (session == null || current == null || session.GearChanged)
            return;
        if (session.GearSince == default)
            session.GearSince = now;
        var toolTypes = SessionToolTypes(session);
        var reading = WithoutForeignTool(WithSessionTool(new GearSnapshot(current), session.DetectedTask), toolTypes);
        var known = session.Gear == null ? null : WithoutForeignTool(Confirmed(session.Gear, session.GearSince), toolTypes);
        if (known != null && Conflicts(known, reading))
        {
            session.GearChanged = true;
            return;
        }
        session.Gear = known == null ? reading : Merge(known, reading);
        session.GearFingerprint = GearFingerprint.Of(session.Gear);
    }

    /// <summary>A copy without the parts read before <paramref name="since"/>.</summary>
    internal static GearSnapshot Confirmed(GearSnapshot gear, DateTime since)
    {
        var copy = new GearSnapshot(gear);
        if (copy.ArmorAt < since)
            RemoveSlots(copy, GearSnapshot.ArmorSlots);
        if (copy.EquipmentAt < since)
            RemoveSlots(copy, GearSnapshot.EquipmentSlots);
        if (copy.ToolAt < since)
            RemoveTool(copy);
        if (copy.PetAt < since)
            copy.Pet = null;
        return copy;
    }

    /// <summary>The tool types of the session's category, null (no restriction) while the task is unknown or unbound.</summary>
    private static IReadOnlySet<string>? SessionToolTypes(TaskSession session)
    {
        var category = GearCategories.ForTask(session.DetectedTask);
        return category == null || category == GearModifierTable.Other ? null : GearModifierTable.For(category).ToolTypes;
    }

    /// <summary>The optimal tool among the reading's candidates for the session's task; no tool while the task is unknown.</summary>
    private static GearSnapshot WithSessionTool(GearSnapshot gear, string? task)
    {
        if (task == null)
        {
            // a pick made for another task (the snapshot's current task) must not become this session's tool
            RemoveTool(gear);
            return gear;
        }
        if (gear.ToolCandidates == null || gear.ToolCandidates.Count == 0)
            return gear;
        var tool = OptimalTool.Pick(gear.ToolCandidates, task);
        gear.ToolEvidence = OptimalTool.Evidence(tool, gear.HeldKey);
        if (tool == null)
            gear.Slots.Remove(GearSnapshot.Tool);
        else
            gear.Slots[GearSnapshot.Tool] = new GearPiece(tool);
        return gear;
    }

    private static GearSnapshot WithoutForeignTool(GearSnapshot gear, IReadOnlySet<string>? toolTypes)
    {
        var tool = gear.ToolPiece;
        if (toolTypes != null && tool != null && (tool.ItemType == null || !toolTypes.Contains(tool.ItemType)))
            RemoveTool(gear);
        return gear;
    }

    private static void RemoveTool(GearSnapshot gear)
    {
        gear.Slots.Remove(GearSnapshot.Tool);
        gear.ToolEvidence = GearToolEvidence.Ambiguous;
    }

    private static void RemoveSlots(GearSnapshot gear, string[] slots)
    {
        foreach (var slot in slots)
            gear.Slots.Remove(slot);
    }

    /// <summary>
    /// Adds a tab Stats widget sample to the session sums: the fallback of the observation stats when no base stats exist.
    /// The player is assumed to hold the session's (optimal) tool.
    /// </summary>
    public static bool AddStatSample(TaskSession? session, IReadOnlyDictionary<string, double> values, DateTime at)
    {
        if (session == null || values.Count == 0)
            return false;
        session.StatSums ??= new();
        session.StatSamples ??= new();
        foreach (var (name, value) in values)
        {
            session.StatSums[name] = session.StatSums.GetValueOrDefault(name) + value;
            session.StatSamples[name] = session.StatSamples.GetValueOrDefault(name) + 1;
        }
        session.LastStatSampleAt = at;
        return true;
    }

    internal static bool Conflicts(GearSnapshot before, GearSnapshot now)
    {
        foreach (var (slot, piece) in now.Slots ?? new())
        {
            if (slot == GearSnapshot.Tool && (!BothToolsKnown(before, now) || StillCarried(before.ToolPiece, now)))
                continue;
            if (before.Slots != null && before.Slots.TryGetValue(slot, out var old) && PieceChanged(old, piece))
                return true;
        }
        return PetChanged(before.Pet, now.Pet);
    }

    /// <summary>
    /// Another optimal pick while the previous tool is still among the candidates is a re-pick (a better tool bought,
    /// lore stats grown), not a swap: only a tool that left the inventory changes the setup.
    /// </summary>
    private static bool StillCarried(GearPiece? tool, GearSnapshot now)
    {
        if (tool == null || now.ToolCandidates == null)
            return false;
        var key = GearFingerprint.PieceKey(tool);
        return now.ToolCandidates.Any(c => GearFingerprint.PieceKey(c) == key);
    }

    private static bool BothToolsKnown(GearSnapshot before, GearSnapshot now)
        => GearToolEvidence.IsKnown(before.ToolEvidence) && GearToolEvidence.IsKnown(now.ToolEvidence);

    /// <summary>Another item, or the same item with other modifiers; a piece read without modifiers (menu) only gets filled in.</summary>
    internal static bool PieceChanged(GearPiece old, GearPiece now)
    {
        if (old.Tag != now.Tag)
            return true;
        return !IsBare(old) && !IsBare(now) && GearFingerprint.PieceKey(old) != GearFingerprint.PieceKey(now);
    }

    /// <summary>No modifier at all: what a menu slot without ExtraAttributes yields.</summary>
    internal static bool IsBare(GearPiece piece)
        => piece.Reforge == null && piece.Stars == null && piece.HotPotatoCount == null && piece.Recombobulated == null
            && IsEmpty(piece.Enchants) && IsEmpty(piece.Gems) && IsEmpty(piece.Parts) && IsEmpty(piece.Counters) && IsEmpty(piece.Applied);

    private static bool IsEmpty<T>(Dictionary<string, T>? values) => values == null || values.Count == 0;

    /// <summary>
    /// Another pet. A pet known only from the tab list (no tier) has a type derived from its display name, which may not
    /// match petInfo.type, so it never counts as a swap; the Pets or Stats menu confirms swaps.
    /// </summary>
    private static bool PetChanged(GearPet? before, GearPet? now)
    {
        if (before == null || now == null || before.Tier == null || now.Tier == null)
            return false;
        return before.Type != now.Type || before.Tier != now.Tier;
    }

    private static GearSnapshot Merge(GearSnapshot before, GearSnapshot now)
    {
        var merged = new GearSnapshot(now);
        foreach (var (slot, piece) in before.Slots ?? new())
            if (!merged.Slots.ContainsKey(slot) || IsBare(merged.Slots[slot]))
                merged.Slots[slot] = new GearPiece(piece);
        KeepKnownTool(before, merged);
        merged.Pet = MergePet(before.Pet, now.Pet);
        merged.ArmorAt = Max(before.ArmorAt, now.ArmorAt);
        merged.EquipmentAt = Max(before.EquipmentAt, now.EquipmentAt);
        merged.ToolAt = Max(before.ToolAt, now.ToolAt);
        merged.PetAt = Max(before.PetAt, now.PetAt);
        return merged;
    }

    /// <summary>An ambiguous reading never replaces a known tool.</summary>
    private static void KeepKnownTool(GearSnapshot before, GearSnapshot merged)
    {
        if (GearToolEvidence.IsKnown(merged.ToolEvidence) || !GearToolEvidence.IsKnown(before.ToolEvidence))
            return;
        merged.ToolEvidence = before.ToolEvidence;
        if (before.ToolPiece != null)
            merged.Slots[GearSnapshot.Tool] = new GearPiece(before.ToolPiece);
    }

    private static GearPet? MergePet(GearPet? before, GearPet? now)
    {
        if (now == null)
            return before == null ? null : new GearPet(before);
        var pet = new GearPet(now);
        if (before != null && before.Type == now.Type)
        {
            pet.Tier ??= before.Tier;
            pet.HeldItem ??= before.HeldItem;
        }
        return pet;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
