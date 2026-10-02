using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Real Garden pest drop tags, shared by <see cref="PestTask"/> and <see cref="PestHuntingTask"/>
/// (HuntingTasks.cs). Replaces the old fabricated PEST_KILL/PESTERMINATOR "detection tags", which
/// never occur in production - see the 2026-09 production log finding. The 13 tags are Pest Vinyls
/// (a rare cosmetic drop, one per pest species); the rest are rarer pest-specific items. Every pest
/// kill also gives the plot's crop as an enchanted item, but that is shared with plain crop farming
/// (BaseGardenCropTask) and is not distinguishing evidence for "this was a pest kill" by itself, so
/// it is deliberately not part of this set - crop farming tasks winning by item value on a mixed
/// window is expected (see the class-level comment on BaseGardenCropTask).
/// </summary>
internal static class PestEvidence
{
    public static readonly HashSet<string> Items =
    [
        "VINYL_BEETLE", "VINYL_BUZZIN_BEATS", "VINYL_CICADA_SYMPHONY", "VINYL_CRICKET_CHOIR", "VINYL_DYNAMITES",
        "VINYL_EARTHWORM_ENSEMBLE", "VINYL_FIREFLY", "VINYL_IMAGINE_DRAGONFLIES", "VINYL_PRAY_FOR_ME",
        "VINYL_PRETTY_FLY", "VINYL_RODENT_REVOLUTION", "VINYL_SLOW_AND_GROOVY", "VINYL_WINGS_OF_HARMONY",
        "LOCUST_LARVA", "BEADY_EYES", "CLIPPED_WINGS", "WRIGGLING_LARVA", "MANTID_CLAW", "CHIRPING_STEREO", "ATMOSPHERIC_FILTER",
        // common drops (10% each; every pest but Field Mouse drops one of them, Field Mouse all six -
        // hypixelskyblock.minecraft.wiki/w/Pests) - tags seen at the Garden in production 2026-10-01.
        // CHEESE_FUEL is Tasty Cheese. Production breaks ties by coin value, so a crop farming period
        // that killed one pest still stays with the crop task.
        "COMPOST", "HONEY_JAR", "DUNG", "PLANT_MATTER", "CHEESE_FUEL", "JELLY",
        // Dragonfly rare drop
        "VERMIN_VAPORIZER_GARDEN_CHIP"
    ];
}

public class PestTask : MethodTask
{
    protected override string MethodName => "Pest";
    // Island key - real Garden zones are "The Garden" (with a per-visit pest count suffix) and
    // free-text "Plot - <player's name>" strings, both collapsed by SkyblockZones.Canonical - same
    // fix as BaseGardenCropTask below.
    protected override HashSet<string> Locations => ["Garden"];
    // Island keys are never zones themselves (see SkyblockZones.cs) - name a real zone so Where/
    // Island/WikiUrl/Warp still resolve (same fix as BaseGardenCropTask below).
    protected override string Where => "The Garden";
    protected override HashSet<string> DetectionItems => PestEvidence.Items;
    protected override bool ExcludeShardItems => true;
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

// GardenFarmingTask (discovered from unclassified production revenue 2026-09) used to lump every
// Garden crop into one bucket. Split 2026-09 into one task per crop: production data shows a
// Garden period is nearly always a single crop (one plot = one crop), so per-crop detection
// classifies cleanly instead of falling back to a "most common crop" flat seed. PestTask shares
// the same location - the classifier tells them apart by DetectionItems (crop items here vs.
// PEST_KILL/PESTERMINATOR there).
public abstract class BaseGardenCropTask : MethodTask
{
    protected override string MethodName => $"{CropName} Farming";
    protected override string Category => "Farming";
    protected override string ActionUnit => "crops";
    // Island key - real Garden zones are "The Garden" (with a per-visit pest count suffix) and
    // free-text "Plot - <player's name>" strings, both collapsed by SkyblockZones.Canonical.
    protected override HashSet<string> Locations => ["Garden"];
    protected override string Where => "The Garden";
    public override List<StatFactor> StatFactors => [new("skill:Farming", 1.0, 60)];
    protected override double ActionsPerHour => 30000;

    /// <summary>Crop display name, e.g. "Wheat" or "Sugar Cane" - MethodName becomes "{CropName} Farming".</summary>
    protected abstract string CropName { get; }
    /// <summary>Community wiki page slug under https://hypixelskyblock.minecraft.wiki/w/.</summary>
    protected abstract string WikiPage { get; }
    /// <summary>Item tag of the crop's dedicated Farming Fortune tool, or null if the crop has no dedicated tool.</summary>
    protected abstract string? ToolTag { get; }
    /// <summary>Display name of <see cref="ToolTag"/>, or null if the crop has no dedicated tool.</summary>
    protected abstract string? ToolName { get; }

    protected override string WikiUrl => $"https://hypixelskyblock.minecraft.wiki/w/{WikiPage}";

    protected override List<RequiredItem> RequiredItems => ToolTag == null ? [] :
        [new() { ItemTag = ToolTag, Name = ToolName!, Reason = "Farming tool with the crop's Farming Fortune" }];

    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Farming Fortune", Description = "More crops per block broken", EstimatedMultiplier = 1.5 },
        new() { Name = "Farming Speed", Description = "Faster tool use means more blocks broken per hour", EstimatedMultiplier = 1.2 }
    ];

    protected override string HowTo => ToolTag == null
        ? $"Type /warp garden, and plant/farm {CropName} on a plot. Sell what you collect on the Bazaar."
        : $"Type /warp garden, plant/farm {CropName} on a plot, and use your {ToolName} for the best Farming Fortune. Sell what you collect on the Bazaar.";

    protected override List<TaskStep> Steps
    {
        get
        {
            var steps = new List<TaskStep>();
            void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });
            Add("Type /warp garden to get close.", "/warp garden");
            Add($"Plant/farm {CropName} on one of your plots.");
            if (ToolTag != null)
                Add($"Use your {ToolName} for the best Farming Fortune.", WikiUrl);
            Add($"You're doing it right when you start collecting: {string.Join(", ", DetectionItems.Take(4))}. That's what we track for your coins/hour.");
            Add("Sell on the Bazaar.");
            Add("Check /cofl task again to see your real coins/hour.");
            return steps;
        }
    }
}

public class WheatFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Wheat";
    protected override string WikiPage => "Wheat";
    protected override string ToolTag => "THEORETICAL_HOE_WHEAT_1";
    protected override string ToolName => "Euclid's Wheat Hoe";
    protected override HashSet<string> DetectionItems => ["WHEAT", "ENCHANTED_WHEAT", "SEEDS", "ENCHANTED_SEEDS", "ENCHANTED_HAY_BLOCK"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_WHEAT", 1700), new("ENCHANTED_SEEDS", 2300)];
}

public class CarrotFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Carrot";
    protected override string WikiPage => "Carrot";
    protected override string ToolTag => "THEORETICAL_HOE_CARROT_1";
    protected override string ToolName => "Gauss Carrot Hoe";
    protected override HashSet<string> DetectionItems => ["CARROT_ITEM", "ENCHANTED_CARROT", "ENCHANTED_GOLDEN_CARROT"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_CARROT", 2300)];
}

public class PotatoFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Potato";
    protected override string WikiPage => "Potato";
    protected override string ToolTag => "THEORETICAL_HOE_POTATO_1";
    protected override string ToolName => "Pythagorean Potato Hoe";
    protected override HashSet<string> DetectionItems => ["POTATO_ITEM", "ENCHANTED_POTATO", "ENCHANTED_BAKED_POTATO"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_POTATO", 3200)];
}

public class PumpkinFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Pumpkin";
    protected override string WikiPage => "Pumpkin";
    protected override string ToolTag => "PUMPKIN_DICER";
    protected override string ToolName => "Pumpkin Dicer";
    protected override HashSet<string> DetectionItems => ["PUMPKIN", "ENCHANTED_PUMPKIN"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_PUMPKIN", 1200)];
}

public class MelonFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Melon";
    protected override string WikiPage => "Melon";
    protected override string ToolTag => "MELON_DICER";
    protected override string ToolName => "Melon Dicer";
    protected override HashSet<string> DetectionItems => ["MELON", "ENCHANTED_MELON", "ENCHANTED_MELON_BLOCK"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MELON", 1900)];
}

public class SugarCaneFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Sugar Cane";
    protected override string WikiPage => "Sugar_Cane";
    protected override string ToolTag => "THEORETICAL_HOE_CANE_1";
    protected override string ToolName => "Turing Sugar Cane Hoe";
    protected override HashSet<string> DetectionItems => ["SUGAR_CANE", "ENCHANTED_SUGAR", "ENCHANTED_SUGAR_CANE"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_SUGAR", 1900)];
}

public class NetherWartFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Nether Wart";
    protected override string WikiPage => "Nether_Wart";
    protected override string ToolTag => "THEORETICAL_HOE_WARTS_1";
    protected override string ToolName => "Newton Nether Warts Hoe";
    protected override HashSet<string> DetectionItems => ["NETHER_STALK", "ENCHANTED_NETHER_STALK"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_NETHER_STALK", 2900)];
}

public class CactusFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Cactus";
    protected override string WikiPage => "Cactus";
    protected override string ToolTag => "CACTUS_KNIFE";
    protected override string ToolName => "Cactus Knife";
    // Production tags the raw cactus drop as BUILDER_CACTUS, not the vanilla CACTUS item id.
    protected override HashSet<string> DetectionItems => ["CACTUS", "BUILDER_CACTUS", "ENCHANTED_CACTUS_GREEN", "ENCHANTED_CACTUS"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_CACTUS_GREEN", 1100)];
}

public class MushroomFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Mushroom";
    protected override string WikiPage => "Mushroom";
    protected override string ToolTag => "FUNGI_CUTTER";
    protected override string ToolName => "Fungi Cutter";
    protected override HashSet<string> DetectionItems =>
        ["RED_MUSHROOM", "BROWN_MUSHROOM", "BUILDER_BROWN_MUSHROOM", "BUILDER_RED_MUSHROOM", "ENCHANTED_RED_MUSHROOM", "ENCHANTED_BROWN_MUSHROOM"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_RED_MUSHROOM", 700), new("ENCHANTED_BROWN_MUSHROOM", 400)];
}

public class CocoaBeansFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Cocoa Beans";
    protected override string WikiPage => "Cocoa_Beans";
    protected override string ToolTag => "COCO_CHOPPER";
    protected override string ToolName => "Cocoa Chopper";
    protected override HashSet<string> DetectionItems => ["INK_SACK:3", "ENCHANTED_COCOA"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_COCOA", 1900)];
}

public class SunflowerFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Sunflower";
    protected override string WikiPage => "Sunflower";
    // No dedicated farming tool for Sunflower.
    protected override string ToolTag => null;
    protected override string ToolName => null;
    protected override HashSet<string> DetectionItems => ["DOUBLE_PLANT", "ENCHANTED_SUNFLOWER", "COMPACTED_SUNFLOWER"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_SUNFLOWER", 1900)];
}

public class MoonflowerFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Moonflower";
    protected override string WikiPage => "Moonflower";
    // No dedicated farming tool for Moonflower.
    protected override string ToolTag => null;
    protected override string ToolName => null;
    protected override HashSet<string> DetectionItems => ["MOONFLOWER", "ENCHANTED_MOONFLOWER"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MOONFLOWER", 3300)];
}

public class WildRoseFarmingTask : BaseGardenCropTask
{
    protected override string CropName => "Wild Rose";
    protected override string WikiPage => "Wild_Rose";
    // No dedicated farming tool for Wild Rose.
    protected override string ToolTag => null;
    protected override string ToolName => null;
    protected override HashSet<string> DetectionItems => ["WILD_ROSE", "ENCHANTED_WILD_ROSE"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_WILD_ROSE", 2300)];
}

/// <summary>
/// Garden visitor rewards (Overclocker 3000, tickets, Garden chips, ...) collected on the Garden
/// island without crops. Low priority so a crop or Pest task with the same matched value wins.
/// </summary>
public class GardenVisitorsTask : MethodTask
{
    protected override string MethodName => "Garden Visitors";
    protected override HashSet<string> Locations => ["Garden"];
    protected override string Where => "The Garden";
    protected override HashSet<string> DetectionItems =>
    [
        "OVERCLOCKER_3000", "SQUEAKY_TOY", "JACOBS_TICKET", "CARNIVAL_TICKET", "FEAST_FLASK", "BOOKWORM_BOOK",
        "QUICKDRAW_GARDEN_CHIP", "SYNTHESIS_GARDEN_CHIP", "EVERGREEN_GARDEN_CHIP", "FLOWERING_BOUQUET",
        "OVERGROWN_GRASS", "GREEN_BANDANA", "SPACE_HELMET", "FRUIT_BOWL"
    ];
    protected override bool ExcludeShardItems => true;
    protected override int Priority => -1;
    protected override string Category => "Garden";
    protected override string ActionUnit => "visitors";
    protected override List<MethodDrop> FormulaDrops => [new("JACOBS_TICKET", 5)];
    protected override string HowTo => "Serve the visitors that arrive at your Garden and accept their rewards.";
    public override string Description =>
        "Rewards from serving Garden visitors (Garden chips, tickets, Overclocker 3000, ...).";
}
