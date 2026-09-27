using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class PestTask : MethodTask
{
    protected override string MethodName => "Pest";
    protected override HashSet<string> Locations => ["The Garden", "Plot 1", "Plot 2", "Plot 3", "Plot 4", "Plot 5", "Plot 6", "Plot 7", "Plot 8", "Plot 9", "Plot 10", "Plot 11", "Plot 12"];
    protected override HashSet<string> DetectionItems => ["PEST_KILL", "PESTERMINATOR"];
    protected override bool ExcludeShardItems => true;
    // Killing/vacuuming Garden pests yields the plot's crop as enchanted drops (not a single "ENCHANTED_CROP").
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("ENCHANTED_MELON", 20), new("ENCHANTED_CARROT", 20),
        new("ENCHANTED_POTATO", 15), new("ENCHANTED_WHEAT", 15),
        new("ENCHANTED_PUMPKIN", 10), new("ENCHANTED_CACTUS", 10),
        new("ENCHANTED_SUGAR_CANE", 10)
    ];
    protected override string Category => "Garden";
    protected override string HowTo => "Go to The Garden and kill pests that spawn on your plots. Use the Pesterminator vacuum or manual pest killing. Higher Farming Fortune increases crop drops.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "PESTERMINATOR", Reason = "Vacuum for killing pests efficiently" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Farming Fortune", Description = "Increases crop drop rates from pest kills", EstimatedMultiplier = 1.3 },
        new() { Name = "Pest Luck", Description = "Increases chance of pest spawns and rare drops", EstimatedMultiplier = 1.2 }
    ];
}
// GardenFarmingTask (discovered from unclassified production revenue 2026-09): the actual Garden
// crop-selling activity. PestTask shares the same location - the classifier tells them apart by
// DetectionItems (crop items here vs. PEST_KILL/PESTERMINATOR there).
public class GardenFarmingTask : MethodTask
{
    protected override string MethodName => "Garden Farming";
    protected override string Category => "Farming";
    protected override string ActionUnit => "crops";
    // Island key - real Garden zones are "The Garden" (with a per-visit pest count suffix) and
    // free-text "Plot - <player's name>" strings, both collapsed by SkyblockZones.Canonical.
    protected override HashSet<string> Locations => ["Garden"];
    protected override string Where => "The Garden";
    protected override HashSet<string> DetectionItems =>
    [
        "WHEAT", "ENCHANTED_WHEAT", "SEEDS", "ENCHANTED_SEEDS",
        "CARROT_ITEM", "ENCHANTED_CARROT", "POTATO_ITEM", "ENCHANTED_POTATO",
        "PUMPKIN", "ENCHANTED_PUMPKIN", "MELON", "ENCHANTED_MELON",
        "SUGAR_CANE", "ENCHANTED_SUGAR", "NETHER_STALK", "ENCHANTED_NETHER_STALK",
        "CACTUS", "ENCHANTED_CACTUS_GREEN", "RED_MUSHROOM", "ENCHANTED_RED_MUSHROOM",
        "BROWN_MUSHROOM", "ENCHANTED_BROWN_MUSHROOM", "INK_SACK:3", "ENCHANTED_COCOA",
        "ENCHANTED_SUNFLOWER", "ENCHANTED_MOONFLOWER", "ENCHANTED_WILD_ROSE"
    ];
    // Wheat is the default/most common crop - real player data (folded once tracked periods exist,
    // see MethodTask.Execute) dominates this flat seed the moment any is available.
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_WHEAT", 6000), new("ENCHANTED_SEEDS", 6000)];
    protected override double ActionsPerHour => 40000;
    public override List<StatFactor> StatFactors => [new("skill:Farming", 1.0, 60)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Garden";
    protected override string HowTo =>
        "Type /warp garden, pick a crop, and farm your plots with the matching farming tool. Sell what you collect on the Bazaar.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Type /warp garden to get close.", OnClick = "/warp garden" },
        new() { Number = 2, Text = "Pick a crop and plant/farm it on one of your plots." },
        new() { Number = 3, Text = "Use the matching farming tool (hoe/axe) for that crop for the best Farming Fortune.", OnClick = WikiUrl },
        new() { Number = 4, Text = "You're doing it right when you start collecting crops like Wheat, Carrots, Potatoes, Pumpkins, Melons, Sugar Cane, Cactus or Mushrooms. That's what we track for your coins/hour." },
        new() { Number = 5, Text = "Sell your crops on the Bazaar." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
