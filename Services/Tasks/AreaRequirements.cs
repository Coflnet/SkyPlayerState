using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// What the player needs to be allowed into an island or zone, applied automatically to every task from
/// the zones it is done in (see <see cref="ProfitTask.AreaZones"/>) instead of repeating it on each task class.
/// Facts from the owner and the community wiki (hypixelskyblock.minecraft.wiki area pages: The_End,
/// Crimson_Isle, Dwarven_Mines, Crystal_Hollows, Glacite_Tunnels, The_Rift, The_Garden, Galatea,
/// Backwater_Bayou). Island names are the ones <see cref="SkyblockZones.IslandOf"/> returns.
/// </summary>
public static class AreaRequirements
{
    private static readonly Dictionary<string, Func<List<TaskRequirement>>> Islands = new(StringComparer.Ordinal)
    {
        ["The End"] = () => [TaskRequirement.Skill("combat", 12)],
        ["Crimson Isle"] = () => [TaskRequirement.Skill("combat", 22)],
        ["Kuudra"] = () => [TaskRequirement.Skill("combat", 22)],
        ["Dwarven Mines"] = () => [TaskRequirement.Skill("mining", 12)],
        ["Crystal Hollows"] = () => [TaskRequirement.Skill("mining", 12), TaskRequirement.HeartOfTheMountain(3)],
        ["Rift"] = () => [TaskRequirement.SkyBlockLevel(12)],
        ["Garden"] = () => [TaskRequirement.SkyBlockLevel(5)],
        ["Moonglade"] = () => [TaskRequirement.Skill("foraging", 12)],
        ["Torrhus"] = () => [TaskRequirement.Skill("foraging", 12), TaskRequirement.HeartOfTheForest(4)],
        ["Backwater Bayou"] = () => [TaskRequirement.Skill("fishing", 5)],
    };

    /// <summary>Zones with a stricter requirement than their island, checked on top of the island's.</summary>
    private static readonly Dictionary<string, Func<List<TaskRequirement>>> Zones = new(StringComparer.Ordinal)
    {
        // Glacite Tunnels / Mineshafts sit inside the Dwarven Mines island but need Heart of the Mountain 7
        ["Glacite Tunnels"] = () => [TaskRequirement.HeartOfTheMountain(7)],
        ["Great Glacite Lake"] = () => [TaskRequirement.HeartOfTheMountain(7)],
        ["Glacite Mineshafts"] = () => [TaskRequirement.HeartOfTheMountain(7)],
        ["Zealot Bruiser Hideout"] = () => [TaskRequirement.Skill("combat", 20)],
        ["Void Sepulture"] = () => [TaskRequirement.Skill("combat", 25)],
    };

    /// <summary>Requirements of one zone: its island's plus the zone's own. Empty when the zone or island is unknown.</summary>
    public static List<TaskRequirement> ForZone(string zone)
    {
        var result = new List<TaskRequirement>();
        var island = SkyblockZones.IslandOf(zone);
        if (island != null && Islands.TryGetValue(island, out var forIsland))
            result.AddRange(forIsland());
        var canonical = SkyblockZones.Canonical(zone);
        if (canonical != null && Zones.TryGetValue(canonical, out var forZone))
            result.AddRange(forZone());
        return result;
    }

    /// <summary>
    /// Requirements of a task done in any of the given zones: the player only needs to reach one of them, so
    /// only what every zone needs applies, at the lowest target any zone asks. A zone without a known island
    /// (ambiguous or unlisted) needs nothing we know of, which empties the result - the safe side.
    /// </summary>
    public static List<TaskRequirement> ForZones(IEnumerable<string> zones)
    {
        var perZone = zones.Where(z => !string.IsNullOrEmpty(z)).Select(ForZone).ToList();
        if (perZone.Count == 0)
            return [];
        var common = new List<TaskRequirement>();
        foreach (var candidate in RequirementEvaluator.Merge(perZone[0]))
        {
            var targets = perZone.Select(list => list
                .Where(r => r.Kind == candidate.Kind && r.Key == candidate.Key && r.Hard == candidate.Hard)
                .Select(r => (int?)r.Target).Max()).ToList();
            if (targets.All(t => t != null))
            {
                var lowest = targets.Min().Value;
                common.Add(lowest == candidate.Target ? candidate.Clone() : SameKindWithTarget(candidate, lowest));
            }
        }
        return common;
    }

    private static TaskRequirement SameKindWithTarget(TaskRequirement template, int target)
    {
        var copy = template.Clone();
        // level like labels end with their target ("Combat 22")
        copy.Label = copy.Label[..(copy.Label.LastIndexOf(' ') + 1)] + target;
        copy.Target = target;
        return copy;
    }
}
