using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Compact view of the parts of a player's Hypixel member profile that task requirements are checked
/// against (see <see cref="TaskRequirement"/>). Built from the raw v2 member JSON served by the profile
/// service at <c>/api/profile/{uuid}/current/data/full</c> via <see cref="FromMemberJson"/>.
/// <para>
/// Missing-data rules, the contract <see cref="RequirementEvaluator"/> relies on: a nullable value is
/// <c>null</c> when the profile does not tell us (e.g. skill API disabled), which never blocks a task.
/// Hypixel omits counters that were never started, so for activity counters (dungeon completions,
/// Kuudra completions and reputation, slayer kills) a missing section means zero, not unknown - that
/// is exactly the "has never unlocked Kuudra" case the requirements exist for.
/// </para>
/// </summary>
public class PlayerProfileSnapshot
{
    /// <summary>Skill levels by lowercase skill name (combat, mining, foraging, fishing). A skill is absent when unknown.</summary>
    public Dictionary<string, int> SkillLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int? SkyBlockLevel { get; set; }
    public int? CatacombsLevel { get; set; }
    /// <summary>Normal mode completions by floor 0 (entrance) to 7.</summary>
    public Dictionary<int, int> NormalFloorCompletions { get; set; } = [];
    /// <summary>Master mode completions by floor 1 to 7.</summary>
    public Dictionary<int, int> MasterFloorCompletions { get; set; } = [];
    /// <summary>Kuudra completions by tier 1 (Basic) to 5 (Infernal).</summary>
    public Dictionary<int, int> KuudraCompletions { get; set; } = [];
    /// <summary>The higher of the Mage and Barbarian reputation.</summary>
    public int KuudraReputation { get; set; }
    /// <summary>Slayer progress by boss family (zombie, spider, wolf, enderman, blaze, vampire).</summary>
    public Dictionary<string, SlayerProgress> Slayers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Heart of the Mountain tier from the profile; the mod's value is preferred when both exist.</summary>
    public int? HotmTier { get; set; }

    public class SlayerProgress
    {
        /// <summary>Highest boss tier (1 to 5) with at least one kill, 0 when none.</summary>
        public int HighestTierKilled { get; set; }
        public double Xp { get; set; }
        public int Level { get; set; }
    }

    // ── Level tables ──

    /// <summary>
    /// Cumulative skill XP to reach level 1..25 (the standard SkyBlock skill table; 5, 10, 12, 15, 20,
    /// 22 and 25 are the values the owner gave, the rest follow from the same per level increments).
    /// Levels above 25 are reported as 25 - no requirement needs more, so we only need to know "at least 25".
    /// </summary>
    internal static readonly long[] SkillXpCumulative =
    [
        50, 175, 375, 675, 1_175, 1_925, 2_925, 4_425, 6_425, 9_925, 14_925, 22_425, 32_425, 47_425, 67_425,
        97_425, 147_425, 222_425, 322_425, 522_425, 822_425, 1_222_425, 1_722_425, 2_322_425, 3_022_425
    ];

    /// <summary>
    /// Cumulative Catacombs XP to reach level 1..50 (levels 1 to 36 checked against the values the owner
    /// gave for 1, 3, 5, 9, 14, 19, 24, 26, 28, 30, 32, 34 and 36; 37 to 50 follow the same standard table
    /// but are only used to display the level, no requirement needs more than 36).
    /// </summary>
    internal static readonly long[] CatacombsXpCumulative = BuildCumulative(
    [
        50, 75, 110, 160, 230, 330, 470, 670, 950, 1_340, 1_890, 2_665, 3_760, 5_260, 7_380, 10_300, 14_400,
        20_000, 27_600, 38_000, 52_500, 71_500, 97_000, 132_000, 180_000, 243_000, 328_000, 445_000, 600_000,
        800_000, 1_065_000, 1_410_000, 1_900_000, 2_500_000, 3_300_000, 4_300_000, 5_600_000, 7_200_000,
        9_200_000, 12_000_000, 15_000_000, 19_000_000, 24_000_000, 30_000_000, 38_000_000, 48_000_000,
        60_000_000, 75_000_000, 93_000_000, 116_250_000
    ]);

    /// <summary>Cumulative XP to reach slayer level 1..9 (level 7 = 100,000 for every boss, which is the one the requirements use).</summary>
    private static readonly Dictionary<string, long[]> SlayerXpCumulative = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zombie"] = [5, 15, 200, 1_000, 5_000, 20_000, 100_000, 400_000, 1_000_000],
        ["spider"] = [5, 25, 200, 1_000, 5_000, 20_000, 100_000, 400_000, 1_000_000],
        ["wolf"] = [10, 30, 250, 1_500, 5_000, 20_000, 100_000, 400_000, 1_000_000],
        ["enderman"] = [10, 30, 250, 1_500, 5_000, 20_000, 100_000, 400_000, 1_000_000],
        ["blaze"] = [10, 30, 250, 1_500, 5_000, 20_000, 100_000, 400_000, 1_000_000],
        ["vampire"] = [20, 75, 240, 840, 2_400],
    };

    /// <summary>
    /// Cumulative Heart of the Mountain XP to reach tier 2..10 (running sum of the per tier costs
    /// 3,000 / 9,000 / 25,000 / 60,000 / 100,000 / 150,000 / 210,000 / 290,000 / 400,000, same as SkyPlayerInfo).
    /// </summary>
    private static readonly long[] HotmXpCumulative = BuildCumulative([3_000, 9_000, 25_000, 60_000, 100_000, 150_000, 210_000, 290_000, 400_000]);

    private static long[] BuildCumulative(long[] perLevel)
    {
        var result = new long[perLevel.Length];
        long sum = 0;
        for (var i = 0; i < perLevel.Length; i++)
            result[i] = sum += perLevel[i];
        return result;
    }

    internal static int LevelFromXp(double xp, long[] cumulative)
    {
        var level = 0;
        while (level < cumulative.Length && xp >= cumulative[level])
            level++;
        return level;
    }

    internal static int HotmTierFromXp(double xp) => 1 + LevelFromXp(xp, HotmXpCumulative);

    internal static int SlayerLevelFromXp(string type, double xp) =>
        SlayerXpCumulative.TryGetValue(type, out var table) ? LevelFromXp(xp, table) : 0;

    // ── Parsing ──

    private static readonly string[] SkillNames = ["combat", "mining", "foraging", "fishing"];
    private static readonly string[] SlayerNames = ["zombie", "spider", "wolf", "enderman", "blaze", "vampire"];
    /// <summary>Hypixel keys of the Kuudra tiers 1 to 5.</summary>
    private static readonly string[] KuudraKeys = ["none", "hot", "burning", "fiery", "infernal"];

    /// <summary>
    /// Maps the raw member JSON. Returns null when the text is not a member object (no <c>player_data</c>),
    /// so a wrong or error response degrades to "unknown" instead of being read as an empty profile.
    /// Every field is read defensively: a missing or mistyped field leaves the value unknown/zero as
    /// described on the class.
    /// </summary>
    public static PlayerProfileSnapshot FromMemberJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Get(root, "player_data") is not { ValueKind: JsonValueKind.Object } playerData)
                return null;
            var snapshot = new PlayerProfileSnapshot();

            if (Get(playerData, "experience") is { ValueKind: JsonValueKind.Object } experience)
                foreach (var skill in SkillNames)
                    snapshot.SkillLevels[skill] = LevelFromXp(Number(Get(experience, "SKILL_" + skill.ToUpperInvariant())) ?? 0, SkillXpCumulative);

            if (Number(Get(root, "leveling", "experience")) is { } sbXp)
                snapshot.SkyBlockLevel = (int)(sbXp / 100);

            var dungeonTypes = Get(root, "dungeons", "dungeon_types");
            var catacombs = Get(dungeonTypes, "catacombs");
            snapshot.CatacombsLevel = LevelFromXp(Number(Get(catacombs, "experience")) ?? 0, CatacombsXpCumulative);
            ReadCompletions(Get(catacombs, "tier_completions"), snapshot.NormalFloorCompletions, name => name);
            ReadCompletions(Get(dungeonTypes, "master_catacombs", "tier_completions"), snapshot.MasterFloorCompletions, name => name);

            var nether = Get(root, "nether_island_player_data");
            var kuudra = Get(nether, "kuudra_completed_tiers");
            for (var i = 0; i < KuudraKeys.Length; i++)
                if (Number(Get(kuudra, KuudraKeys[i])) is { } count && count > 0)
                    snapshot.KuudraCompletions[i + 1] = (int)count;
            snapshot.KuudraReputation = (int)Math.Max(
                Number(Get(nether, "mages_reputation")) ?? 0, Number(Get(nether, "barbarians_reputation")) ?? 0);

            var bosses = Get(root, "slayer", "slayer_bosses");
            foreach (var type in SlayerNames)
            {
                var boss = Get(bosses, type);
                var progress = new SlayerProgress { Xp = Number(Get(boss, "xp")) ?? 0 };
                for (var tier = 5; tier >= 1; tier--)
                    if (Number(Get(boss, "boss_kills_tier_" + (tier - 1))) is > 0)
                    {
                        progress.HighestTierKilled = tier;
                        break;
                    }
                progress.Level = SlayerLevelFromXp(type, progress.Xp);
                snapshot.Slayers[type] = progress;
            }

            // Hypixel moved the HotM experience from mining_core to skill_tree, the old field is the fallback
            var hotmXp = Number(Get(root, "skill_tree", "experience", "mining")) ?? Number(Get(root, "mining_core", "experience"));
            if (hotmXp != null)
                snapshot.HotmTier = HotmTierFromXp(hotmXp.Value);
            // Heart of the Forest is deliberately not derived from skill_tree.experience.foraging: the per tier
            // costs are unverified, so it stays unknown (the mod's tier is the only source).
            return snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ReadCompletions(JsonElement? completions, Dictionary<int, int> target, Func<string, string> key)
    {
        if (completions is not { ValueKind: JsonValueKind.Object } obj)
            return;
        foreach (var entry in obj.EnumerateObject())
            if (int.TryParse(key(entry.Name), out var floor) && Number(entry.Value) is { } count)
                target[floor] = (int)count;
    }

    private static JsonElement? Get(JsonElement? element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(part, out var next))
                return null;
            current = next;
        }
        return current;
    }

    private static double? Number(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var value) ? value : null;
}
