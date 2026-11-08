using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// SYNTHETIC test profiles, one per rate model kind. Item tags (SYNTHETIC_*), stats and sources are made up
/// to exercise the code; they are not game data and must never be copied into profiles/*.json. Bindings use
/// real task names because the binding resolution is part of what is tested.
/// </summary>
internal static class GearFixtures
{
    private const string Source = "\"sources\": [\"https://example.com/synthetic-test-fixture\"]";

    private static string Item(string slot, string tier) =>
        $$"""{ "slot": "{{slot}}", "tag": "SYNTHETIC_{{slot.ToUpperInvariant()}}_{{tier.ToUpperInvariant()}}", "name": "Synthetic {{slot}} {{tier}}", "reason": "test fixture" }""";

    private static string Setup(string id, string stats, string extra = "") =>
        $$"""{ "id": "{{id}}", "tier": "{{id}}", "name": "Synthetic {{id}}", "items": [ {{Item("helmet", id)}}, {{Item("tool", id)}} ], "stats": { {{stats}} }{{extra}} }""";

    private static string Profile(string id, string model, string[] stats, string bindings, string[]? extras = null,
        string profileExtra = "")
    {
        string Extra(int i) => extras == null ? "" : extras[i];
        return $$"""
        {
          "id": "{{id}}", "name": "Synthetic {{id}}", "anchor": "budget", "researchedAt": "2026-11-07", {{Source}}{{profileExtra}},
          "model": {{model}},
          "baseline": {{Setup("baseline", stats[0], Extra(0))}},
          "setups": [ {{Setup("budget", stats[1], Extra(1))}}, {{Setup("mid", stats[2], Extra(2))}}, {{Setup("endgame", stats[3], Extra(3))}} ],
          "bindings": {{bindings}}
        }
        """;
    }

    public static readonly string Fortune = Profile("test-fortune",
        """{ "kind": "fortune", "fortuneStat": "Farming Fortune", "rareStat": "Magic Find", "formulaSource": "https://example.com/synthetic-formula" }""",
        ["\"Farming Fortune\": 50, \"Magic Find\": 0", "\"Farming Fortune\": 100, \"Magic Find\": 0",
         "\"Farming Fortune\": 150, \"Magic Find\": 10", "\"Farming Fortune\": 200, \"Magic Find\": 20"],
        """
        [ { "task": "Wheat Farming", "rareDropTags": ["SYNTHETIC_RARE_DROP"] },
          { "task": "Carrot Farming",
            "itemOverrides": { "tool": { "tag": "SYNTHETIC_CARROT_TOOL", "name": "Synthetic carrot tool", "reason": "test fixture" },
                               "endgame:tool": { "tag": "SYNTHETIC_CARROT_TOOL_ENDGAME", "name": "Synthetic carrot tool endgame", "reason": "test fixture",
                                                 "priceFilters": { "Rarity": "LEGENDARY" } } },
            "statOverrides": { "endgame": { "Farming Fortune": 250 } } } ]
        """);

    public static readonly string Mining = Profile("test-mining",
        """{ "kind": "mining", "speedStat": "Mining Speed", "fortuneStats": ["Mining Fortune", "Gemstone Fortune"], "blockStrength": 500, "instamineFactor": 60, "spreadStat": "Mining Spread", "pristineStat": "Pristine", "rareStat": "Magic Find" }""",
        ["\"Mining Speed\": 500, \"Mining Fortune\": 50, \"Gemstone Fortune\": 0, \"Mining Spread\": 0, \"Pristine\": 0, \"Magic Find\": 0",
         "\"Mining Speed\": 1000, \"Mining Fortune\": 100, \"Gemstone Fortune\": 10, \"Mining Spread\": 0, \"Pristine\": 2, \"Magic Find\": 0",
         "\"Mining Speed\": 1500, \"Mining Fortune\": 200, \"Gemstone Fortune\": 20, \"Mining Spread\": 10, \"Pristine\": 3, \"Magic Find\": 5",
         "\"Mining Speed\": 3000, \"Mining Fortune\": 300, \"Gemstone Fortune\": 30, \"Mining Spread\": 20, \"Pristine\": 4, \"Magic Find\": 10"],
        """[ { "task": "Coal Mining", "modelOverrides": { "blockStrength": 600 } } ]""");

    public static readonly string Fishing = Profile("test-fishing",
        """{ "kind": "fishing", "fishingSpeedStat": "Fishing Speed", "seaCreatureChanceStat": "Sea Creature Chance", "baseWaitSeconds": 20, "reelSeconds": 1, "killSecondsPerCreature": 2, "fishingSpeedCap": 300, "sccCap": 100, "rareStat": "Magic Find", "formulaSource": "https://example.com/synthetic-formula" }""",
        ["\"Fishing Speed\": 0, \"Sea Creature Chance\": 10, \"Magic Find\": 0",
         "\"Fishing Speed\": 50, \"Sea Creature Chance\": 20, \"Magic Find\": 0",
         "\"Fishing Speed\": 100, \"Sea Creature Chance\": 30, \"Magic Find\": 10",
         "\"Fishing Speed\": 200, \"Sea Creature Chance\": 40, \"Magic Find\": 20"],
        """
        [ { "task": "Water Fishing",
            "itemOverrides": { "pet": { "tag": "SYNTHETIC_SOULBOUND_PET", "name": "Synthetic soulbound pet", "reason": "test fixture", "tradeable": false } } } ]
        """,
        profileExtra: ", \"modelConfidence\": \"medium\", \"statCaps\": { \"Fishing Speed\": 150 }");

    public static readonly string RunTime = Profile("test-runtime",
        """{ "kind": "runTime", "overheadSeconds": 30, "mfAffected": true, "rareStat": "Magic Find" }""",
        ["\"Magic Find\": 0", "\"Magic Find\": 10", "\"Magic Find\": 20", "\"Magic Find\": 40"],
        """[ { "task": "F7" } ]""",
        [", \"runSeconds\": 600", ", \"runSeconds\": 480", ", \"runSeconds\": 360", ", \"runSeconds\": 240"]);

    public static readonly string[] KuudraTiers = ["Kuudra T1", "Kuudra T2", "Kuudra T3", "Kuudra T4", "Kuudra T5"];

    public static readonly string TierUnlock = Profile("test-tierunlock",
        """{ "kind": "tierUnlock" }""",
        ["", "", "", ""],
        """[ { "task": "Kuudra T1" }, { "task": "Kuudra T4" }, { "task": "Kuudra T5" } ]""",
        ["", ", \"unlocksTask\": \"Kuudra T3\"", ", \"unlocksTask\": \"Kuudra T4\"", ", \"unlocksTask\": \"Kuudra T5\""],
        profileExtra: ", \"tiers\": [" + string.Join(", ", KuudraTiers.Select(t => $"\"{t}\"")) + "]");

    public static readonly string Fixed = Profile("test-fixed",
        """{ "kind": "fixed", "multipliers": { "baseline": 0.8, "budget": 1, "mid": 1.3, "endgame": 1.8 } }""",
        ["", "", "", ""],
        """[ { "task": "Diana" } ]""");

    public static readonly IReadOnlyDictionary<string, string> ByKind = new Dictionary<string, string>
    {
        [GearModelKinds.Fortune] = Fortune,
        [GearModelKinds.Mining] = Mining,
        [GearModelKinds.Fishing] = Fishing,
        [GearModelKinds.RunTime] = RunTime,
        [GearModelKinds.TierUnlock] = TierUnlock,
        [GearModelKinds.Fixed] = Fixed
    };

    public static GearProfileCatalog Catalog(params string[] profiles) =>
        GearProfileCatalog.FromJson(profiles.Select((json, i) => ($"fixture{i}.json", json)));

    public static GearProfileCatalog All() => Catalog(ByKind.Values.ToArray());

    /// <summary>A fixture parsed for mutation into an invalid variant.</summary>
    public static JsonObject Mutable(string json) => JsonNode.Parse(json)!.AsObject();
}
