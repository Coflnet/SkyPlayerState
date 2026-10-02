using Coflnet.Sky.Commands.MC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Base class for slayer tasks that tracks profit using player period data.
/// </summary>
public abstract class IndividualSlayerTask : ProfitTask
{
    protected abstract string SlayerName { get; }
    protected abstract HashSet<string> LocationNames { get; }
    /// <summary>Public accessor for tests to audit LocationNames (see SkyblockZones bare-island-name regression).</summary>
    public HashSet<string> LocationNamesForTest => LocationNames;

    /// <summary>Where a slayer boss is fought - defaults to the first of its LocationNames.</summary>
    protected override string Where => LocationNames.FirstOrDefault();
    /// <summary>
    /// Falls back to the island's wiki page (always resolvable - see SkyblockZones.IslandInfo);
    /// specific bosses (Inferno Demonlord) override this with the boss's own wiki page.
    /// </summary>
    protected override string WikiUrl => WhereWikiUrl;

    /// <summary>
    /// Generic followable steps: start the quest via Maddox, grind the required mobs, kill the
    /// boss. Overridden per-boss (see T3/T4InfernoDemonlordTask) with more specific directions.
    /// </summary>
    protected override List<TaskStep> Steps
    {
        get
        {
            var steps = new List<TaskStep>();
            void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });
            var warp = EffectiveWarpCommand;
            if (warp != null)
                Add($"Type {warp} to get close.", warp);
            Add("The first time, buy a Maddox Batphone from Maddox (at the Hub Tavern) - then you can start any Slayer quest from anywhere.",
                "https://hypixelskyblock.minecraft.wiki/w/Maddox_Batphone");
            Add($"Talk to Maddox (or use your Batphone) and start a {SlayerName} quest.", "https://hypixelskyblock.minecraft.wiki/w/Maddox");
            if (Where != null)
                Add($"Kill the mobs the quest asks for near {Where} until the boss spawns.", WhereWikiUrl);
            Add("Kill the boss to finish the quest and get your Slayer rewards.");
            Add("Sell the shards and rare item drops on the Bazaar or Auction House.");
            Add("Check /cofl task again to see your real coins/hour.");
            return steps;
        }
    }

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        var matched = parameters.LocationProfit
            .Where(lp => SkyblockZones.Matches(LocationNames, lp.Key))
            .SelectMany(lp => lp.Value)
            .ToList();

        if (matched.Count == 0)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"No {SlayerName} activity tracked so far.",
                Details = $"Do some {SlayerName} slayer runs so we can calculate your profitability.",
                OnClick = EffectiveWarpCommand,
                Breakdown = NewGuidanceBreakdown("Slayer", "none")
            });

        var totalProfit = matched.Sum(p => (double)p.Profit);
        var totalHours = matched.Sum(p => (p.EndTime - p.StartTime).TotalHours);
        if (totalHours <= 0)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"{SlayerName} sessions too short.",
                Name = SlayerName,
                OnClick = EffectiveWarpCommand,
                Breakdown = NewGuidanceBreakdown("Slayer", "none")
            });

        var perHour = totalProfit / totalHours;
        var items = matched.SelectMany(p => p.ItemsCollected)
            .GroupBy(i => i.Key).ToDictionary(g => g.Key, g => (long)g.Sum(v => v.Value))
            .OrderByDescending(i => i.Value);
        var fmt = parameters.Formatter;
        var formattedDuration = fmt.FormatTime(TimeSpan.FromHours(totalHours));
        var itemBreakDown = items.Take(15)
            .Select(i => $"{McColorCodes.YELLOW}{i.Key} {McColorCodes.GRAY}x{i.Value}")
            .DefaultIfEmpty("No items tracked").Aggregate((a, b) => a + "\n" + b);

        var breakdown = NewGuidanceBreakdown("Slayer", "player_data");
        breakdown.TrackedHours = totalHours;
        breakdown.Drops = items.Take(15).Select(i => new DropInfo
        {
            ItemTag = i.Key,
            RatePerHour = i.Value / totalHours,
            PriceEach = 0
        }).ToList();

        return Task.FromResult(new TaskResult
        {
            ProfitPerHour = (int)perHour,
            Message = $"{SlayerName} earning {McColorCodes.AQUA}{fmt.FormatPrice((long)totalProfit)} {McColorCodes.GRAY}over {formattedDuration}.",
            Details = $"Time tracked: {formattedDuration}\nItems collected:\n{itemBreakDown}",
            Name = SlayerName,
            OnClick = EffectiveWarpCommand,
            Breakdown = breakdown
        });
    }
}

// ── Blaze Slayer ──
// Bare island name "Crimson Isle" deliberately excluded from LocationNames below - it is fought
// in the Smoldering Tomb specifically (see InfernoDemonlordGuide), and IndividualSlayerTask.Execute
// matches via SkyblockZones.Matches, so an island-level entry would wrongly count every Crimson
// Isle zone (Mycelium mining in Mystic Marsh, fishing in Scarleton/Oasis, ...) as this slayer.
public class T4InfernoDemonlordTask : IndividualSlayerTask
{
    protected override string SlayerName => "T4 Inferno Demonlord";
    protected override HashSet<string> LocationNames => ["Stronghold", "Smoldering Tomb", "The Bastion"];
    public override string Description => "T4 Inferno Demonlord (Blaze Slayer)";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Inferno_Demonlord";
    protected override List<TaskStep> Steps => InfernoDemonlordGuide.Steps(4);
}
public class T3InfernoDemonlordTask : IndividualSlayerTask
{
    protected override string SlayerName => "T3 Inferno Demonlord";
    protected override HashSet<string> LocationNames => ["Stronghold", "Smoldering Tomb", "The Bastion"];
    public override string Description => "T3 Inferno Demonlord (Blaze Slayer)";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Inferno_Demonlord";
    protected override List<TaskStep> Steps => InfernoDemonlordGuide.Steps(3);
}

/// <summary>
/// Shared step-by-step for both Inferno Demonlord tiers (Blaze Slayer). Facts verified on the
/// community wiki (2026-09): the quest is started via Maddox/a Maddox Batphone like every other
/// slayer (not a special Crimson Isle NPC); regular Blazes for the required Combat XP are ground
/// in the Smoldering Tomb specifically ("meant to be used to obtain Combat XP for Blaze Slayer" -
/// recommended Combat level 30); the boss then spawns there once the XP bar is full.
/// </summary>
internal static class InfernoDemonlordGuide
{
    public static List<TaskStep> Steps(int tier)
    {
        var steps = new List<TaskStep>();
        void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });

        Add("The first time, buy a Maddox Batphone from Maddox (at the Hub Tavern) so you can start Slayer quests from anywhere, including the Crimson Isle.",
            "https://hypixelskyblock.minecraft.wiki/w/Maddox_Batphone");
        Add("Type /warp isle to get close.", "/warp isle");
        Add($"Talk to Maddox (or use your Batphone) and start a Blaze Slayer quest at Tier {tier}.",
            "https://hypixelskyblock.minecraft.wiki/w/Maddox");
        Add("Go to the Smoldering Tomb and kill regular Blazes there - that's the recommended spot for Blaze Slayer Combat XP.",
            "https://hypixelskyblock.minecraft.wiki/w/Smoldering_Tomb");
        Add("Wear gear with high health and Fire Resistance - Blazes and the boss hit hard with fire and true damage. Opal Gemstones in your armor/weapon combat slots help a lot.");
        Add("Once you've killed enough Blazes, the Inferno Demonlord boss spawns - kill it to finish the quest.",
            "https://hypixelskyblock.minecraft.wiki/w/Inferno_Demonlord");
        Add("Sell Derelict Ashe, Blaze Rods, and any rare shard/dye drops on the Bazaar or Auction House.");
        Add("Check /cofl task again to see your real coins/hour.");
        return steps;
    }
}

// ── Spider Slayer ──
// Bare island name "Spider's Den" deliberately excluded from LocationNames below - see the
// Inferno Demonlord comment above. "The Spider's Den" (with the article) is a distinct, specific
// zone string, not the island key, so it stays.
public class T5TarantulaTask : IndividualSlayerTask
{
    protected override string SlayerName => "T5 Tarantula";
    protected override HashSet<string> LocationNames => ["The Spider's Den", "Arachne's Sanctuary", "Spider Mound"];
    public override string Description => "T5 Tarantula Broodfather";
}
public class T4TarantulaTask : IndividualSlayerTask
{
    protected override string SlayerName => "T4 Tarantula";
    protected override HashSet<string> LocationNames => ["The Spider's Den", "Arachne's Sanctuary", "Spider Mound"];
    public override string Description => "T4 Tarantula Broodfather";
}

// ── Crimson Isle bosses ──
public class AshfangTask : IndividualSlayerTask
{
    protected override string SlayerName => "Ashfang";
    protected override HashSet<string> LocationNames => ["Ruins of Ashfang", "Smoldering Tomb", "Blazing Volcano"];
    public override string Description => "Ashfang on Crimson Isle";
}
/// <summary>
/// Barbarian Duke X on the Crimson Isle. A MethodTask (not an IndividualSlayerTask, which has no detection
/// signature and so was never offered to the TaskClassifier): its shard periods at The Dukedom used to land
/// in the hidden "Crimson Isle Mobs" fallback or stay unclassified.
/// </summary>
public class BarbarianDukeXTask : MethodTask
{
    protected override string MethodName => "Barbarian Duke X";
    protected override HashSet<string> Locations => ["The Dukedom", "Stronghold", "Dragontail", "Mage Outpost"];
    protected override HashSet<string> DetectionItems => ["SHARD_BARBARIAN_DUKE_X"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BARBARIAN_DUKE_X", 20)];
    protected override string Category => "Slayer";
    protected override string ActionUnit => "kills";
    protected override string Where => "The Dukedom";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Barbarian_Duke_X";
    protected override string HowTo => "Go to The Dukedom on the Crimson Isle and kill Barbarian Duke X for its shard.";
    public override string Description => "Barbarian Duke X on Crimson Isle";
}

// ── Enderman Slayer ──
// Bare island name "The End" deliberately excluded from Locations below - see the Inferno
// Demonlord comment above.
public class T4VoidgloomsTask : MethodTask
{
    protected override string MethodName => "T4 Voidglooms";
    protected override HashSet<string> Locations => ["Dragon's Nest", "Void Sepulture"];
    protected override HashSet<string> DetectionItems => ["NULL_SPHERE", "SUMMONING_EYE"];
    protected override List<MethodDrop> FormulaDrops => [new("NULL_SPHERE", 15), new("SUMMONING_EYE", 2)];
    protected override string Category => "Slayer";
    protected override string HowTo => "Go to The End and spawn T4 Voidgloom Seraphs. Use Terminator or Juju bow for high DPS. Requires Combat 24+ and Enderman Slayer 7.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "TERMINATOR", Reason = "High DPS weapon for Voidgloom kills" },
        new() { ItemTag = "VOID_SWORD", Reason = "Alternative weapon for Voidgloom kills" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance from slayer bosses", EstimatedMultiplier = 1.2 },
        new() { Name = "Ender Slayer", Description = "Increased damage against Endermen", EstimatedMultiplier = 1.3 }
    ];
}
// ── New slayer tasks (2026-09 production log analysis) - these use MethodTask (not
// IndividualSlayerTask) since they need DetectionItems for TaskClassifier, same as T4Voidglooms* above. ──

/// <summary>
/// Revenant Horror, the Zombie Slayer boss - started at Maddox, spawned by killing Crypt Ghouls
/// [Lv30] and Golden Ghouls [Lv60] in the Hub Crypts below the Graveyard. Tier gates by Combat
/// level. Revenant Flesh is the guaranteed drop.
/// </summary>
public class RevenantSlayerTask : MethodTask
{
    protected override string MethodName => "Revenant Slayer";
    protected override HashSet<string> Locations => ["Crypts", "Graveyard"];
    protected override HashSet<string> DetectionItems =>
        ["REVENANT_FLESH", "FOUL_FLESH", "REVENANT_VISCERA", "REVENANT_CATALYST", "UNDEAD_CATALYST", "ROTTEN_FLESH"];
    // Production medians (2026-09) per 10-min period * 6: REVENANT_FLESH 126, ROTTEN_FLESH 43.
    protected override List<MethodDrop> FormulaDrops => [new("REVENANT_FLESH", 756), new("ROTTEN_FLESH", 258)];
    protected override string Category => "Slayer";
    // Golden Ghoul (a mob you kill along the way, sharing this zone) also drops Gold Ingots and
    // would otherwise tie on matched item count with no priced tie-break in tests - Revenant Flesh
    // is the actual slayer quest reward and should win.
    protected override int Priority => 1;
    protected override string HowTo =>
        "Buy a Maddox Batphone, start a Zombie Slayer quest, then kill Crypt Ghouls and Golden Ghouls "
        + "in the Hub Crypts (below the Graveyard) until the Revenant Horror boss spawns. Kill it for "
        + "guaranteed Revenant Flesh.";
    // no RequiredItems: no specific weapon is verified for this slayer, a generic one would mislead
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance from slayer bosses", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level unlocks higher slayer tiers", EstimatedMultiplier = 1.1 }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Revenant_Horror";
}

/// <summary>
/// Sven Packmaster, the Wolf Slayer boss - farmed in the Howling Cave complex in The Park (behind
/// the Dark Thicket). Wolf Tooth is the guaranteed drop. The boss is immune to ability and ranged
/// damage (melee only) and deals true damage.
/// </summary>
public class SvenSlayerTask : MethodTask
{
    protected override string MethodName => "Sven Slayer";
    protected override HashSet<string> Locations => ["Howling Cave", "Soul Cave", "Spirit Cave", "Ruins"];
    protected override HashSet<string> DetectionItems => ["WOLF_TOOTH", "HAMSTER_WHEEL", "RED_CLAW_EGG", "FURBALL"];
    // Production medians (2026-09) per 10-min period * 6: WOLF_TOOTH 257, HAMSTER_WHEEL 12.
    protected override List<MethodDrop> FormulaDrops => [new("WOLF_TOOTH", 1542), new("HAMSTER_WHEEL", 72)];
    protected override string Category => "Slayer";
    protected override string HowTo =>
        "Buy a Maddox Batphone, start a Wolf Slayer quest, then kill wolves in the Howling Cave complex "
        + "(behind the Dark Thicket in The Park) until Sven Packmaster spawns. He is immune to ability "
        + "and ranged damage (melee only) and deals true damage - bring high health/defense gear.";
    // no RequiredItems: no specific weapon is verified for this slayer, a generic one would mislead
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance from slayer bosses", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level unlocks higher slayer tiers", EstimatedMultiplier = 1.1 }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Sven_Packmaster";
}

/// <summary>
/// Tarantula Broodfather (Spider Slayer boss), farmed on the Crimson Isle: Flaming Spiders [Lv80]
/// in the Burning Desert are killed to spawn the boss quickly (they also drop Burning Eye, 20%
/// chance); the boss itself drops Tarantula Web. Distinct from the Spider's Den variant
/// (T4/T5TarantulaTask above), which is fought in the original Spider's Den location instead.
/// </summary>
public class TarantulaSlayerCrimsonIsleTask : MethodTask
{
    protected override string MethodName => "Tarantula Slayer (Crimson Isle)";
    protected override HashSet<string> Locations => ["Burning Desert", "Dragontail", "Crimson Isle"];
    protected override HashSet<string> DetectionItems => ["TARANTULA_WEB", "TARANTULA_SILK", "TOXIC_ARROW_POISON", "BURNING_EYE"];
    // Production medians (2026-09) per 10-min period * 6: TARANTULA_WEB 71, BURNING_EYE 4.
    protected override List<MethodDrop> FormulaDrops => [new("TARANTULA_WEB", 426), new("BURNING_EYE", 24)];
    protected override string Category => "Slayer";
    protected override string HowTo =>
        "Buy a Maddox Batphone, start a Spider Slayer quest, then kill Flaming Spiders [Lv80] in the "
        + "Burning Desert on the Crimson Isle to spawn Tarantula Broodfather quickly (they also drop "
        + "Burning Eye, 20% chance). Kill the boss for Tarantula Web.";
    // no RequiredItems: no specific weapon is verified for this slayer, a generic one would mislead
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance from slayer bosses", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level unlocks higher slayer tiers", EstimatedMultiplier = 1.1 }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Tarantula_Broodfather";
}

public class T4VoidgloomsFdTask : MethodTask
{
    protected override string MethodName => "T4 Voidglooms (FD)";
    protected override HashSet<string> Locations => ["Dragon's Nest", "Void Sepulture"];
    protected override HashSet<string> DetectionItems => ["NULL_SPHERE", "SUMMONING_EYE"];
    protected override List<MethodDrop> FormulaDrops => [new("NULL_SPHERE", 20), new("SUMMONING_EYE", 3)];
    protected override string Category => "Slayer";
    protected override string HowTo => "Go to The End and spawn T4 Voidgloom Seraphs (formula drop estimate). Uses estimated rates for players without tracked data.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "TERMINATOR", Reason = "High DPS weapon for Voidgloom kills" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance from slayer bosses", EstimatedMultiplier = 1.2 },
        new() { Name = "Ender Slayer", Description = "Increased damage against Endermen", EstimatedMultiplier = 1.3 }
    ];
}
