using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tasks.Gear;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The gear category of a task: the id of the gear profile bound to it (farming, mining-gem, ...). Depends on
/// <see cref="GearProfileCatalog"/> and the profile json files on purpose: WP2 keeps the catalog, the profiles and their
/// task bindings (the curated "researched, not yet measured" fallback) and only deletes the modelled rate parts.
/// </summary>
public static class GearCategories
{
    /// <summary>Task categories (<see cref="MethodTask"/> Category) whose tool is a weapon.</summary>
    private static readonly HashSet<string> CombatTaskCategories = new(StringComparer.Ordinal)
    {
        "Combat", "Slayer", "Mob Farming", "Dungeon", "Kuudra"
    };

    private static readonly Lazy<HashSet<string>> CombatTasks = new(LoadCombatTasks);

    /// <summary>The bound profile's id, <see cref="GearModifierTable.Other"/> for unbound tasks, null without a task.</summary>
    public static string? ForTask(string? task)
    {
        if (string.IsNullOrEmpty(task))
            return null;
        return GearProfileCatalog.Embedded.TryGet(task, out var bound) ? bound.Profile.Id : GearModifierTable.Other;
    }

    /// <summary>
    /// Whether the task is fought with a weapon: a registered task of a combat category (Combat, Slayer, Mob Farming,
    /// Dungeon, Kuudra), a slayer task, or one of the kill tasks filed elsewhere: Diana (Event), the mob hunting tasks
    /// (Hunting; not Lunar Moth Trapping and Pest hunting, done with traps and the vacuum) and Crimson Isle Mobs (Tracking).
    /// Unbound tasks ("other") only get a combat tool when this holds.
    /// </summary>
    public static bool IsCombatTask(string? task) => !string.IsNullOrEmpty(task) && CombatTasks.Value.Contains(task);

    private static HashSet<string> LoadCombatTasks()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in new TaskRegistry().Tasks)
            AddIfCombat(task, names);
        return names;
    }

    private static void AddIfCombat(ProfitTask task, HashSet<string> names)
    {
        if (task is IndividualSlayerTask)
        {
            names.Add(task.Name);
            return;
        }
        if (task is not MethodTask method)
            return;
        var signature = method.GetDetectionSignature();
        if (CombatTaskCategories.Contains(signature.Category) || IsKillTaskOfOtherCategory(task))
            names.Add(signature.MethodName);
    }

    private static bool IsKillTaskOfOtherCategory(ProfitTask task)
        => task is BaseDianaTask or CrimsonIsleMobsTask
           || task is BaseHuntingTask and not (LunarMothTrappingTask or PestHuntingTask);
}
