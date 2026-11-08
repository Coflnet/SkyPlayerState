using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Stats without the gear, so a session's stats follow its own gear after a loadout or wardrobe switch:
/// <code>
/// base[s]      = max(0, menuTotal[s] - Σ lore stat s of the armor, equipment, pet and held item shown in "Stats &amp; Equipment")
///                for the rate relevant stats the menu has a total for
/// effective[s] = base[s] + Σ lore stat s of the session's armor, equipment, pet and optimal tool
/// </code>
/// narrowed to the task's category (<see cref="GearModifierTable"/>). Approximate on purpose: additive percentage bonuses,
/// island multipliers (Ender armor x2 on the End) and set bonuses are not undone, a piece without lore contributes 0.
/// For an unchanged setup the approximation cancels out: effective equals the menu total.
/// </summary>
public static class GearBaseStats
{
    /// <summary>The base of the rate relevant stats the menu shows a total for.</summary>
    public static Dictionary<string, double> Derive(IReadOnlyDictionary<string, double> menuTotals,
        IEnumerable<IReadOnlyDictionary<string, double>> shownGear)
    {
        var names = GearModifierTable.Capture.Stats;
        var result = menuTotals.Where(t => names.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        foreach (var stats in shownGear)
            foreach (var (name, value) in stats.Where(s => result.ContainsKey(s.Key)))
                result[name] -= value;
        foreach (var name in result.Keys.ToList())
            result[name] = Math.Max(0, result[name]);
        return result;
    }

    /// <summary>
    /// The base plus the gear's lore stats, narrowed to the task's category. A session pet known without lore (tab, Pets
    /// list) borrows the lore stats of the same pet type shown with the base. Without a session tool the stats the held
    /// item was subtracted from are left out (<paramref name="withoutTool"/>): base + gear would miss the weapon or tool.
    /// </summary>
    public static Dictionary<string, double> Effective(BaseStatsReading reading, GearSnapshot gear, string? category, string? task,
        out IReadOnlyCollection<string> withoutTool)
    {
        var result = GearNormalizer.FilterStats(reading.Stats.ToDictionary(b => b.Key, b => b.Value), category, task);
        foreach (var stats in GearStats(gear, reading.Pet))
            foreach (var (name, value) in GearNormalizer.FilterStats(stats, category, task))
                result[name] = result.GetValueOrDefault(name) + value;
        withoutTool = gear.ToolPiece == null ? LeaveOutHeldStats(result, reading.HeldStats) : [];
        return result;
    }

    private static List<string> LeaveOutHeldStats(Dictionary<string, double> result, Dictionary<string, double>? heldStats)
    {
        var removed = (heldStats ?? new()).Keys.Where(result.ContainsKey).ToList();
        foreach (var name in removed)
            result.Remove(name);
        return removed;
    }

    private static IEnumerable<Dictionary<string, double>> GearStats(GearSnapshot gear, GearPet? basePet)
    {
        foreach (var piece in gear.Slots?.Values ?? Enumerable.Empty<GearPiece>())
            if (piece?.Stats != null)
                yield return piece.Stats;
        if (PetStats(gear.Pet, basePet) is { } petStats)
            yield return petStats;
    }

    private static Dictionary<string, double>? PetStats(GearPet? pet, GearPet? basePet)
    {
        if (pet == null)
            return null;
        if (pet.Stats is { Count: > 0 })
            return pet.Stats;
        return basePet != null && basePet.Type == pet.Type ? basePet.Stats : null;
    }
}
