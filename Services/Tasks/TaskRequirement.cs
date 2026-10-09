using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// One thing a player needs before a task is doable: a skill level, a cleared dungeon floor, a slayer tier...
/// Declared per task through <see cref="ProfitTask.Requirements"/> (plus the island table in
/// <see cref="AreaRequirements"/>), checked against the profile by <see cref="RequirementEvaluator"/> and
/// returned on <see cref="MethodBreakdown.Requirements"/> so a client can show "Needs Catacombs 36 (you: 31)".
/// <para>
/// Hard requirements are enforced by the game: a known unmet one makes the task inaccessible. Recommended
/// ones (<see cref="Hard"/> false) are informational and never block.
/// </para>
/// </summary>
public class TaskRequirement
{
    /// <summary>One of <see cref="RequirementKinds"/>.</summary>
    public string Kind { get; set; }
    /// <summary>Sub selector of the kind: skill name, slayer boss family or dungeon mode ("normal"/"master"). Null when the kind has none.</summary>
    public string Key { get; set; }
    /// <summary>The value to reach: a level, a floor number, a tier or a reputation amount.</summary>
    public int Target { get; set; }
    /// <summary>Human readable name ("Catacombs 36", "Clear Master Floor 6", "Combat 22").</summary>
    public string Label { get; set; }
    /// <summary>True when the game blocks the player without it, false when it is only recommended.</summary>
    public bool Hard { get; set; } = true;
    /// <summary>True/false once evaluated against the profile, null when unknown (no profile, field missing).</summary>
    public bool? Met { get; set; }
    /// <summary>The player's current value for this kind (null when unknown).</summary>
    public double? Current { get; set; }
    /// <summary>The player's current value as text ("31", "0 clears", "tier 2 cleared"), used in the inaccessible reason.</summary>
    public string CurrentText { get; set; }

    public TaskRequirement Clone() => (TaskRequirement)MemberwiseClone();

    // ── Factories: one per kind, so a task declares a requirement in one readable line ──

    private static string Title(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    public static TaskRequirement Skill(string skill, int level, bool hard = true) =>
        new() { Kind = RequirementKinds.SkillLevel, Key = skill.ToLowerInvariant(), Target = level, Label = $"{Title(skill)} {level}", Hard = hard };

    public static TaskRequirement SkyBlockLevel(int level, bool hard = true) =>
        new() { Kind = RequirementKinds.SkyBlockLevel, Target = level, Label = $"SkyBlock level {level}", Hard = hard };

    public static TaskRequirement CatacombsLevel(int level, bool hard = true) =>
        new() { Kind = RequirementKinds.CatacombsLevel, Target = level, Label = $"Catacombs {level}", Hard = hard };

    /// <summary>The player has completed the given floor at least once.</summary>
    public static TaskRequirement DungeonFloor(bool master, int floor, bool hard = true) =>
        new()
        {
            Kind = RequirementKinds.DungeonFloor, Key = master ? "master" : "normal", Target = floor, Hard = hard,
            Label = master ? $"Clear Master Floor {floor}" : $"Clear Floor {floor}"
        };

    /// <summary>The player has completed the given Kuudra tier (1 Basic to 5 Infernal) at least once.</summary>
    public static TaskRequirement KuudraTier(int tier, bool hard = true) =>
        new() { Kind = RequirementKinds.KuudraTier, Target = tier, Label = $"Clear Kuudra {KuudraTierNames[tier - 1]}", Hard = hard };

    public static readonly string[] KuudraTierNames = ["Basic", "Hot", "Burning", "Fiery", "Infernal"];

    /// <summary>Reputation with either Crimson Isle faction (the higher of the two counts).</summary>
    public static TaskRequirement KuudraReputation(int reputation, bool hard = true) =>
        new() { Kind = RequirementKinds.KuudraReputation, Target = reputation, Label = $"{reputation:N0} reputation with a Crimson Isle faction", Hard = hard };

    /// <summary>The player has killed the given boss tier (1 to 5) of a slayer at least once.</summary>
    public static TaskRequirement SlayerTier(string slayer, int tier, bool hard = true) =>
        new()
        {
            Kind = RequirementKinds.SlayerTier, Key = slayer.ToLowerInvariant(), Target = tier, Hard = hard,
            Label = $"Kill {SlayerBossName(slayer)} tier {tier}"
        };

    public static TaskRequirement SlayerLevel(string slayer, int level, bool hard = true) =>
        new() { Kind = RequirementKinds.SlayerLevel, Key = slayer.ToLowerInvariant(), Target = level, Label = $"{SlayerFamilyName(slayer)} Slayer {level}", Hard = hard };

    public static TaskRequirement HeartOfTheMountain(int tier, bool hard = true) =>
        new() { Kind = RequirementKinds.HeartOfTheMountain, Target = tier, Label = $"Heart of the Mountain {tier}", Hard = hard };

    public static TaskRequirement HeartOfTheForest(int tier, bool hard = true) =>
        new() { Kind = RequirementKinds.HeartOfTheForest, Target = tier, Label = $"Heart of the Forest {tier}", Hard = hard };

    private static string SlayerBossName(string slayer) => slayer.ToLowerInvariant() switch
    {
        "zombie" => "Revenant Horror",
        "spider" => "Tarantula Broodfather",
        "wolf" => "Sven Packmaster",
        "enderman" => "Voidgloom Seraph",
        "blaze" => "Inferno Demonlord",
        "vampire" => "Riftstalker Bloodfiend",
        _ => slayer
    };

    private static string SlayerFamilyName(string slayer) => Title(slayer.ToLowerInvariant());
}

/// <summary>The requirement kinds. To add one: add the constant here, a factory on <see cref="TaskRequirement"/> and an entry in <see cref="RequirementEvaluator"/>.</summary>
public static class RequirementKinds
{
    public const string SkillLevel = "skill_level";
    public const string SkyBlockLevel = "skyblock_level";
    public const string CatacombsLevel = "catacombs_level";
    public const string DungeonFloor = "dungeon_floor";
    public const string KuudraTier = "kuudra_tier";
    public const string KuudraReputation = "kuudra_reputation";
    public const string SlayerTier = "slayer_tier";
    public const string SlayerLevel = "slayer_level";
    public const string HeartOfTheMountain = "hotm_tier";
    public const string HeartOfTheForest = "hotf_tier";
}

/// <summary>What the evaluators read the player's progress from.</summary>
public record RequirementContext(PlayerProfileSnapshot Snapshot, ExtractedInfo Info);

/// <summary>
/// Checks <see cref="TaskRequirement"/>s against the player's progress and applies the outcome to a
/// <see cref="TaskResult"/>. Pure and synchronous, the profile snapshot is loaded once per request elsewhere.
/// </summary>
public static class RequirementEvaluator
{
    /// <summary>Outcome of one check: Met null means unknown.</summary>
    private readonly record struct Outcome(bool? Met, double? Current, string Text);

    private static Outcome Unknown => new(null, null, null);

    /// <summary>Compares a known level-like value with the target.</summary>
    private static Outcome AtLeast(double? current, int target, string text = null) =>
        current == null ? Unknown : new(current >= target, current, text ?? current.Value.ToString("0"));

    private static readonly Dictionary<string, Func<TaskRequirement, RequirementContext, Outcome>> Evaluators = new()
    {
        [RequirementKinds.SkillLevel] = (r, c) =>
            AtLeast(c.Snapshot != null && c.Snapshot.SkillLevels.TryGetValue(r.Key, out var level) ? level : null, r.Target),
        [RequirementKinds.SkyBlockLevel] = (r, c) => AtLeast(c.Snapshot?.SkyBlockLevel, r.Target),
        [RequirementKinds.CatacombsLevel] = (r, c) => AtLeast(c.Snapshot?.CatacombsLevel, r.Target),
        [RequirementKinds.DungeonFloor] = (r, c) =>
        {
            if (c.Snapshot == null)
                return Unknown;
            var floors = r.Key == "master" ? c.Snapshot.MasterFloorCompletions : c.Snapshot.NormalFloorCompletions;
            var clears = floors.GetValueOrDefault(r.Target);
            return new(clears > 0, clears, clears == 1 ? "1 clear" : $"{clears} clears");
        },
        [RequirementKinds.KuudraTier] = (r, c) =>
        {
            if (c.Snapshot == null)
                return Unknown;
            var highest = c.Snapshot.KuudraCompletions.Where(k => k.Value > 0).Select(k => k.Key).DefaultIfEmpty(0).Max();
            return new(highest >= r.Target, highest, highest == 0 ? "none cleared" : $"{TaskRequirement.KuudraTierNames[highest - 1]} cleared");
        },
        [RequirementKinds.KuudraReputation] = (r, c) =>
            c.Snapshot == null ? Unknown : AtLeast(c.Snapshot.KuudraReputation, r.Target, c.Snapshot.KuudraReputation.ToString("N0")),
        [RequirementKinds.SlayerTier] = (r, c) =>
        {
            if (c.Snapshot == null)
                return Unknown;
            // a boss with no counters in a readable profile was never killed
            var highest = c.Snapshot.Slayers.GetValueOrDefault(r.Key)?.HighestTierKilled ?? 0;
            return new(highest >= r.Target, highest, highest == 0 ? "no kills" : $"tier {highest} killed");
        },
        [RequirementKinds.SlayerLevel] = (r, c) =>
            c.Snapshot == null ? Unknown : AtLeast(c.Snapshot.Slayers.GetValueOrDefault(r.Key)?.Level ?? 0, r.Target),
        // the mod reads the tier from the game itself, so it wins over the profile (cached up to 3 hours)
        [RequirementKinds.HeartOfTheMountain] = (r, c) =>
            AtLeast(c.Info?.HeartOfTheMountain?.Tier is > 0 and var modTier ? modTier : c.Snapshot?.HotmTier, r.Target),
        [RequirementKinds.HeartOfTheForest] = (r, c) =>
            AtLeast(c.Info?.HeartOfTheForest?.Tier is > 0 and var modTier ? modTier : null, r.Target),
    };

    /// <summary>
    /// Kinds where two requirements with the same key collapse into the one with the higher target
    /// (Combat 12 and Combat 22 on the same task need Combat 22). Floors and tiers are distinct steps and stay.
    /// </summary>
    private static readonly HashSet<string> Cumulative =
    [
        RequirementKinds.SkillLevel, RequirementKinds.SkyBlockLevel, RequirementKinds.CatacombsLevel,
        RequirementKinds.KuudraReputation, RequirementKinds.SlayerLevel,
        RequirementKinds.HeartOfTheMountain, RequirementKinds.HeartOfTheForest
    ];

    /// <summary>Drops exact duplicates and, for level-like kinds, keeps the highest target per kind/key/hardness.</summary>
    public static List<TaskRequirement> Merge(IEnumerable<TaskRequirement> requirements)
    {
        var result = new List<TaskRequirement>();
        foreach (var requirement in requirements)
        {
            var same = result.FindIndex(r => r.Kind == requirement.Kind && r.Key == requirement.Key && r.Hard == requirement.Hard
                && (r.Target == requirement.Target || Cumulative.Contains(r.Kind)));
            if (same < 0)
                result.Add(requirement);
            else if (requirement.Target > result[same].Target)
                result[same] = requirement;
        }
        return result;
    }

    /// <summary>Evaluates copies of the requirements (the declared ones are shared, never mutated).</summary>
    public static List<TaskRequirement> Evaluate(IEnumerable<TaskRequirement> requirements, RequirementContext context)
    {
        var evaluated = new List<TaskRequirement>();
        foreach (var declared in requirements)
        {
            var requirement = declared.Clone();
            if (Evaluators.TryGetValue(requirement.Kind ?? "", out var evaluator))
            {
                var outcome = evaluator(requirement, context);
                requirement.Met = outcome.Met;
                requirement.Current = outcome.Current;
                requirement.CurrentText = outcome.Text;
            }
            evaluated.Add(requirement);
        }
        return evaluated;
    }

    /// <summary>
    /// Evaluates the requirements and writes the outcome onto the result: the evaluated list and the
    /// tri-state <see cref="MethodBreakdown.RequirementsMet"/> on the breakdown, and a known unmet hard
    /// requirement makes the task inaccessible with the reason naming the first one (in declaration order).
    /// A progression lock wins over the other gates (mayor, time window, daily): it is the permanent and
    /// more fundamental reason, and the time based hints would be misleading for a task the player cannot
    /// reach at all, so <see cref="TaskResult.NextAvailableAt"/> is cleared too. When nothing is known
    /// unmet the result's existing accessibility is left exactly as the task set it.
    /// </summary>
    public static void Apply(TaskResult result, IReadOnlyCollection<TaskRequirement> requirements, TaskParams parameters)
    {
        if (result == null || requirements == null || requirements.Count == 0)
            return;
        var evaluated = Evaluate(requirements, new RequirementContext(parameters?.ProfileSnapshot, parameters?.ExtractedInfo));
        var hard = evaluated.Where(r => r.Hard).ToList();
        var firstUnmet = hard.FirstOrDefault(r => r.Met == false);
        if (result.Breakdown != null)
        {
            result.Breakdown.Requirements = evaluated;
            result.Breakdown.RequirementsMet = hard.Count == 0 ? null
                : firstUnmet != null ? false
                : hard.All(r => r.Met == true) ? true
                : null;
        }
        if (firstUnmet == null)
            return;
        result.IsAccessible = false;
        result.InaccessibleReason = string.IsNullOrEmpty(firstUnmet.CurrentText)
            ? $"Needs {firstUnmet.Label}"
            : $"Needs {firstUnmet.Label} (you: {firstUnmet.CurrentText})";
        result.NextAvailableAt = null;
    }
}
