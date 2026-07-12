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
public class FigTask : MethodTask
{
    protected override string MethodName => "Fig";
    protected override HashSet<string> Locations => ["The Garden", "Plot 1", "Plot 2", "Plot 3", "Plot 4", "Plot 5", "Plot 6", "Plot 7", "Plot 8", "Plot 9", "Plot 10", "Plot 11", "Plot 12"];
    protected override HashSet<string> DetectionItems => ["FIG", "ENCHANTED_FIG"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_FIG", 200)];
    protected override string Category => "Garden";
    protected override string HowTo => "Farm figs on your Garden plots. Requires fig seeds planted on dedicated plots. Higher Farming Fortune increases yield.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "THEORETICAL_HOE_FIG", Reason = "Best hoe for fig farming" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Farming Fortune", Description = "Directly multiplies fig drop rate", EstimatedMultiplier = 1.5 },
        new() { Name = "Farming Speed", Description = "Break crops faster for more harvests per hour", EstimatedMultiplier = 1.2 }
    ];
}
