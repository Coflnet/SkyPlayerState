using System.Linq;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── End island methods ──
public class ZealotsFdTask : MethodTask
{
    protected override string MethodName => "Zealots (FD)";
    protected override HashSet<string> Locations => ["The End", "Dragon's Nest", "Void Sepulture"];
    protected override HashSet<string> DetectionItems => ["SUMMONING_EYE", "ENDER_PEARL", "ENCHANTED_ENDER_PEARL"]; // pearls get auto-compacted
    // A Null Sphere is a Voidgloom boss drop, see T4VoidgloomsTask - without the veto the pricey
    // pearls/eyes of the same period could outvalue it and keep the fight here.
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems =>
        new() { ["Zealot Bruiser Hideout"] = ["NULL_SPHERE"] };
    protected override List<MethodDrop> FormulaDrops => [new("SUMMONING_EYE", 4), new("ENDER_PEARL", 300)];
    protected override string Category => "Combat";
    protected override string HowTo => "Go to The End and grind Zealots for Summoning Eyes. Kill Zealots rapidly; Special Zealots have a guaranteed eye drop. Formula-based estimate.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "One-shot zealots for fast farming" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases Summoning Eye drop rate", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level increases damage", EstimatedMultiplier = 1.1 }
    ];
}

// Dragon loot is booked at the Dragon's Nest mainly; "The End" is listed so loot booked one zone later still counts.
// Dragon fragments also drop in dungeons, which the End-only locations keep out.
public class EnderDragonTask : MethodTask
{
    private static readonly string[] DragonTypes = ["OLD", "UNSTABLE", "YOUNG", "WISE", "STRONG", "PROTECTOR", "SUPERIOR", "HOLY"];
    protected override string MethodName => "Ender Dragon";
    protected override HashSet<string> Locations => ["Dragon's Nest", "The End"];
    protected override HashSet<string> DetectionItems => [
        "OLD_FRAGMENT", "UNSTABLE_FRAGMENT", "YOUNG_FRAGMENT", "WISE_FRAGMENT", "STRONG_FRAGMENT", "PROTECTOR_FRAGMENT",
        "SUPERIOR_FRAGMENT", "HOLY_FRAGMENT", "DRAGON_HORN", "DRAGON_CLAW", "DRAGON_SCALE", "ASPECT_OF_THE_DRAGON",
        "RITUAL_RESIDUE", "SHARD_DRACONIC",
        .. DragonTypes.SelectMany(type => new[] { "HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS" }.Select(piece => $"{type}_DRAGON_{piece}"))
    ];
    // A period of only placed eyes (negative count, see CollectionListener.BookPlacedSummoningEyes) is the cost of a
    // fight whose loot was booked elsewhere. The classifier lets a negative evidence count stand in for a detection
    // item; positive eyes (Zealot drops) stay Zealots (FD)'s.
    protected override HashSet<string> EvidenceItems => ["SUMMONING_EYE"];
    // Zealots (FD) also lists the eyes, so a placed-eyes-only period ties with it on value
    protected override int Priority => 1;
    // Seeds, derived from 10 hours of production loot periods and divided by 4 because only periods holding loot
    // were measured (fights without drops are missing). Real tracked data folds in later.
    protected override List<MethodDrop> FormulaDrops => [
        new("YOUNG_FRAGMENT", 12), new("OLD_FRAGMENT", 10), new("UNSTABLE_FRAGMENT", 9), new("PROTECTOR_FRAGMENT", 7),
        new("STRONG_FRAGMENT", 6), new("SUPERIOR_FRAGMENT", 5), new("WISE_FRAGMENT", 4), new("SHARD_DRACONIC", 2.5)
    ];
    // about 6 fights an hour with 2 own eyes each
    protected override List<MethodDrop> FormulaCosts => [new("SUMMONING_EYE", 12)];
    protected override string Category => "Combat";
    protected override string HowTo => "Place Summoning Eyes (from Zealots or the Bazaar) on the altar in the Dragon's Nest to summon the Ender Dragon and deal damage for loot quality. More own eyes placed means better drops. Formula-based estimate.";
}

// ── Mushroom Desert farming ──
public class RedMushroomTask : MethodTask
{
    protected override string MethodName => "Red Mushroom";
    protected override HashSet<string> Locations => ["Mushroom Desert", "Glowing Mushroom Cave", "Mushroom Gorge"];
    protected override HashSet<string> DetectionItems => ["RED_MUSHROOM", "ENCHANTED_RED_MUSHROOM"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_RED_MUSHROOM", 300)];
    protected override string Category => "Farming";
    protected override string HowTo => "Go to the Mushroom Desert and farm Red Mushrooms. Break mushroom blocks in the Glowing Mushroom Cave for fast collection.";
    protected override List<DropEffect> Effects => [
        new() { Name = "Farming Fortune", Description = "Increases mushroom drop amount", EstimatedMultiplier = 1.3 },
        new() { Name = "Farming Speed", Description = "Break blocks faster for more per hour", EstimatedMultiplier = 1.2 }
    ];
}
public class BrownMushroomTask : MethodTask
{
    protected override string MethodName => "Brown Mushroom";
    protected override HashSet<string> Locations => ["Mushroom Desert", "Glowing Mushroom Cave", "Mushroom Gorge"];
    protected override HashSet<string> DetectionItems => ["BROWN_MUSHROOM", "ENCHANTED_BROWN_MUSHROOM"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_BROWN_MUSHROOM", 300)];
    protected override string Category => "Farming";
    protected override string HowTo => "Go to the Mushroom Desert and farm Brown Mushrooms. Same areas as Red Mushrooms but collect the brown variant.";
    protected override List<DropEffect> Effects => [
        new() { Name = "Farming Fortune", Description = "Increases mushroom drop amount", EstimatedMultiplier = 1.3 },
        new() { Name = "Farming Speed", Description = "Break blocks faster for more per hour", EstimatedMultiplier = 1.2 }
    ];
}
public class MyceliumTask : MethodTask
{
    protected override string MethodName => "Mycelium";
    // Mycelium is mined on the Crimson Isle (Mystic Marsh/Mage Outpost/Scarleton) or via Private Island minions.
    // Verified on the community wiki 2026-09: https://hypixelskyblock.minecraft.wiki/w/Mycelium
    protected override HashSet<string> Locations => ["Mystic Marsh", "Mage Outpost", "Scarleton", "Private Island"];
    protected override HashSet<string> DetectionItems => ["MYCEL", "ENCHANTED_MYCELIUM"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MYCELIUM", 250)];
    protected override string Category => "Farming";
    protected override string HowTo => "Go to the Crimson Isle (Mystic Marsh, Mage Outpost, or Scarleton) and mine Mycelium blocks with a shovel.";
    protected override List<DropEffect> Effects => [
        new() { Name = "Mining Fortune", Description = "Increases mycelium drop rate", EstimatedMultiplier = 1.2 },
        new() { Name = "Mining Speed", Description = "Break mycelium blocks faster", EstimatedMultiplier = 1.2 }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Mycelium";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Type /warp isle to get close.", OnClick = "/warp isle" },
        new() { Number = 2, Text = "Go to Mystic Marsh, Mage Outpost, or Scarleton on the Crimson Isle.", OnClick = WikiUrl },
        new() { Number = 3, Text = "Use a shovel to break the grey-green Mycelium blocks on the ground." },
        new() { Number = 4, Text = "You're doing it right when you collect: Mycelium. That's what we track for your coins/hour." },
        new() { Number = 5, Text = "Sell it as Enchanted Mycelium on the Bazaar, or turn it into a Mycelium Minion for passive income later." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
