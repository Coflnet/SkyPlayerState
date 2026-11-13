using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base class for foraging tasks (discovered from unclassified production revenue 2026-09) ──
public abstract class BaseForagingTask : MethodTask
{
    protected override string Category => "Foraging";
    protected override string ActionUnit => "logs";
    // Galatea foraging earns contest coupons on the side (a Galatea contest is won by foraging)
    protected override LateRewardSpec LateReward => GalateaContestRewards.ForLocations(Locations);
    public override List<StatFactor> StatFactors =>
    [
        new("skill:Foraging", 0.5, 50),
        new("hotf:tier", 0.5, 10)
    ];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Foraging Fortune", Description = "More logs per tree", EstimatedMultiplier = 1.5 },
        new() { Name = "Sweep", Description = "Increases the number of logs broken per cut", EstimatedMultiplier = 1.3 },
        new() { Name = "Foraging Wisdom", Description = "Faster Foraging XP, indirectly more logs per hour", EstimatedMultiplier = 1.1 }
    ];
}

/// <summary>The Torrhus zones without the Critter Safari minigame, shared by Helix Foraging and the Helix lathering hunt.</summary>
internal static class TorrhusLandZones
{
    public static readonly string[] WithoutSafari =
    [
        "Torrhus Canyon", "Miria's Hut", "Pangolin Hideaway", "Torrhus Heights", "Spring Path", "Torrhus Springs",
        "Spring Shallows", "Spring Depths", "Ant's Cave", "Hotspot Haven", "Desert Temple"
    ];
}

public class HelixForagingTask : BaseForagingTask
{
    protected override string MethodName => "Helix Foraging";
    // The Torrhus zones (see SkyblockZones) without the Critter Safari: no Helix tree grows in that
    // minigame instance, and logs showing up in a Safari period took it away from the Critter Safari
    // task (production 2026-10-03). Where below names the actual spot to stand in.
    protected override HashSet<string> Locations => [.. TorrhusLandZones.WithoutSafari];
    protected override string Where => "Torrhus Canyon";
    protected override HashSet<string> DetectionItems =>
    [
        "HELIX_LOG", "ENCHANTED_HELIX_LOG",
        // Helix Tree Gifts critters (Puck drops Bottled Mischief); 48-71 periods of each shard were Helix periods 2026-10-11
        "SHARD_GROUNDHOG", "SHARD_FIREFOX", "SHARD_PUCK", "SHARD_DRYBARK", "BOTTLED_MISCHIEF",
        // also Helix Tree Gifts / a 1% side drop of breaking a Torrhus tree (wiki Grizzly_Bear, Pangolin)
        "SHARD_GRIZZLY_BEAR", "HELIXIS"
    ];
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("HELIX_LOG", 15000), new("ENCHANTED_HELIX_LOG", 300), new("HONEYCOMB", 400)
    ];
    protected override double ActionsPerHour => 15000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Helix_Log";
    protected override string HowTo =>
        "Warp to Torrhus (needs Foraging XII and Heart of the Forest tier 4), then cut Helix Trees in "
        + "Torrhus Canyon or Torrhus Heights - the beige logs first, then the red ones. Collect Helix Logs and sell them on the Bazaar.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Level Foraging to XII and Heart of the Forest to tier 4 - both are needed to warp to Torrhus." },
        new() { Number = 2, Text = "Type /warp torrhus to get close.", OnClick = "/warp torrhus" },
        new() { Number = 3, Text = "Go to Torrhus Canyon (or Torrhus Heights) and look for Helix Trees.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Cut the beige logs first, then the red logs, to fell the whole tree.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you start collecting: HELIX_LOG, ENCHANTED_HELIX_LOG. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell your Helix Logs on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}

public class FigForagingTask : BaseForagingTask
{
    protected override string MethodName => "Fig Foraging";
    // Island key (Moonglade's own zones are Moonglade Marsh, Tangleburg, ... - see SkyblockZones);
    // Where below names the actual spot to stand in.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Moonglade Marsh";
    // TENDER_WOOD and the Chameleon/Hummingbird shards: Fig Tree Gifts (a chance bonus drop from felling Fig trees).
    protected override HashSet<string> DetectionItems =>
        ["FIG_LOG", "ENCHANTED_FIG_LOG", "TENDER_WOOD", "SHARD_CHAMELEON", "SHARD_HUMMINGBIRD"];
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("FIG_LOG", 20000), new("ENCHANTED_FIG_LOG", 150), new("MANGROVE_LOG", 500), new("TENDER_WOOD", 200)
    ];
    protected override double ActionsPerHour => 20000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Fig_Log";
    protected override string HowTo =>
        "Type /warp galatea, then find Fig Trees on the Evergreen Plateau, the Reaches or Moonglade Marsh. "
        + "Cut the trunk first, then the branches, to fell the whole tree.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Type /warp galatea to get close.", OnClick = "/warp galatea" },
        new() { Number = 2, Text = "Look for Fig Trees on the Evergreen Plateau, the Reaches or Moonglade Marsh.", OnClick = WhereWikiUrl },
        new() { Number = 3, Text = "Cut the trunk first, then the branches, to fell the whole tree.", OnClick = WikiUrl },
        new() { Number = 4, Text = "You're doing it right when you start collecting: FIG_LOG, ENCHANTED_FIG_LOG. That's what we track for your coins/hour." },
        new() { Number = 5, Text = "Sell your Fig Logs on the Bazaar." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}

// Discovered from unclassified production revenue 2026-09-27.
public class MangroveForagingTask : BaseForagingTask
{
    protected override string MethodName => "Mangrove Foraging";
    // Island key (Moonglade's own zones are Moonglade Marsh, Tangleburg, ... - see SkyblockZones);
    // Where below names the actual spot to stand in.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Murkwater Loch";
    // VINESAP/DEEP_ROOT and the Chameleon/Hummingbird shards: Mangrove Tree Gifts (a chance bonus drop from felling Mangrove trees).
    protected override HashSet<string> DetectionItems =>
        ["MANGROVE_LOG", "ENCHANTED_MANGROVE_LOG", "VINESAP", "DEEP_ROOT", "SHARD_CHAMELEON", "SHARD_HUMMINGBIRD"];
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("MANGROVE_LOG", 3000), new("ENCHANTED_MANGROVE_LOG", 20), new("VINESAP", 40)
    ];
    protected override double ActionsPerHour => 3000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Galatea";
    protected override string HowTo =>
        "Type /warp galatea (needs Foraging XII), then find Mangrove trees standing in the wetlands - South/North Wetlands, "
        + "Murkwater Loch, or Moonglade Marsh. Cut the branches first, then the trunk, then the roots.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Level Foraging to XII to enter Galatea. Speak to Hina in Tangleburg to unlock Heart of the Forest and its foraging perks." },
        new() { Number = 2, Text = "Type /warp galatea to get close.", OnClick = "/warp galatea" },
        new() { Number = 3, Text = "Look for Mangrove trees standing in the wetlands - South Wetlands, North Wetlands, Murkwater Loch, or Moonglade Marsh.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Cut the branches first, then the trunk, then the roots, to fell the whole tree. A Fig Hew (Foraging XII) or Figstone Splitter (XV) axe makes this much faster.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you start collecting: MANGROVE_LOG, ENCHANTED_MANGROVE_LOG. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell your Mangrove Logs on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}

/// <summary>
/// Honeycomb is collected from Honeyhives scattered across Torrhus Canyon (Galatea) - the island
/// requires Foraging 12 and Heart of the Forest tier 4, same as Helix Foraging.
/// </summary>
public class HoneycombGatheringTask : BaseForagingTask
{
    protected override string MethodName => "Honeycomb Gathering";
    // Island key (Torrhus zones are Torrhus Canyon, Torrhus Heights, Miria's Hut, ... - see SkyblockZones).
    protected override HashSet<string> Locations => ["Torrhus"];
    protected override string Where => "Torrhus Canyon";
    protected override HashSet<string> DetectionItems =>
    [
        "HONEYCOMB", "ENCHANTED_HONEYCOMB",
        // the Queen Bee shard is found in Honeyhives. Pollendart (lathered trees) is "Helix Honey Lathering".
        "SHARD_QUEEN_BEE"
    ];
    // Production medians (2026-09) per 10-min period * 6: HONEYCOMB 81, ENCHANTED_HONEYCOMB 3.
    protected override List<MethodDrop> FormulaDrops => [new("HONEYCOMB", 486), new("ENCHANTED_HONEYCOMB", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Canyon";
    protected override string HowTo =>
        "Warp to Torrhus (needs Foraging XII and Heart of the Forest tier 4), then collect Honeycomb "
        + "from Honeyhives scattered around Torrhus Canyon.";
}

// ── Hub and Park wood (production 2026-10-02/03, per-hour medians of real periods) ──
// One task per tree type, each in the zone that grows it. "Forest" is also the main Diana zone: a Diana
// period holding a few logs stays Diana because its Ancient Claws/Enchanted Gold outvalue the logs.
public class OakForagingTask : BaseForagingTask
{
    protected override string MethodName => "Oak Foraging";
    protected override HashSet<string> Locations => ["Forest"];
    protected override HashSet<string> DetectionItems => ["LOG", "ENCHANTED_OAK_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG", 1700)];
    protected override double ActionsPerHour => 1700;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Oak_Log";
    protected override string HowTo => "Go to the Forest and chop Oak trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
public class BirchForagingTask : BaseForagingTask
{
    protected override string MethodName => "Birch Foraging";
    protected override HashSet<string> Locations => ["Birch Park"];
    protected override HashSet<string> DetectionItems => ["LOG:2", "ENCHANTED_BIRCH_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG:2", 15000)];
    protected override double ActionsPerHour => 15000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Birch_Log";
    protected override string HowTo => "Go to the Birch Park and chop Birch trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
public class SpruceForagingTask : BaseForagingTask
{
    protected override string MethodName => "Spruce Foraging";
    protected override HashSet<string> Locations => ["Spruce Woods"];
    protected override HashSet<string> DetectionItems => ["LOG:1", "ENCHANTED_SPRUCE_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG:1", 2100)];
    protected override double ActionsPerHour => 2100;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Spruce_Log";
    protected override string HowTo => "Go to the Spruce Woods and chop Spruce trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
public class DarkOakForagingTask : BaseForagingTask
{
    protected override string MethodName => "Dark Oak Foraging";
    protected override HashSet<string> Locations => ["Dark Thicket"];
    protected override HashSet<string> DetectionItems => ["LOG_2:1", "ENCHANTED_DARK_OAK_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG_2:1", 2300)];
    protected override double ActionsPerHour => 2300;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Dark_Oak_Log";
    protected override string HowTo => "Go to the Dark Thicket and chop Dark Oak trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
public class AcaciaForagingTask : BaseForagingTask
{
    protected override string MethodName => "Acacia Foraging";
    protected override HashSet<string> Locations => ["Savanna Woodland"];
    protected override HashSet<string> DetectionItems => ["LOG_2", "ENCHANTED_ACACIA_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG_2", 5300)];
    protected override double ActionsPerHour => 5300;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Acacia_Log";
    protected override string HowTo => "Go to the Savanna Woodland and chop Acacia trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
public class JungleForagingTask : BaseForagingTask
{
    protected override string MethodName => "Jungle Foraging";
    protected override HashSet<string> Locations => ["Jungle Island"];
    protected override HashSet<string> DetectionItems => ["LOG:3", "ENCHANTED_JUNGLE_LOG"];
    protected override List<MethodDrop> FormulaDrops => [new("LOG:3", 3200)];
    protected override double ActionsPerHour => 3200;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Jungle_Log";
    protected override string HowTo => "Go to the Jungle Island and chop Jungle trees with a Foraging axe. Collect the logs and sell them on the Bazaar.";
}
