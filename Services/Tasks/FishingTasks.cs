using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base for all fishing tasks with shared metadata ──
public abstract class BaseFishingTask : MethodTask
{
    public override List<StatFactor> StatFactors =>
    [
        new("skill:Fishing", 0.35, 50),
        new("attr:Fishing Speed", 0.30, 10),
        new("gear:FISHING_ROD", 0.25, 9),
        new("pet:FLYING_FISH", 0.10, 100)
    ];
    protected override string Category => "Fishing";
    protected override string ActionUnit => "catches";
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Fishing Speed", Description = "Higher fishing speed reduces time between catches", EstimatedMultiplier = 1.3 },
        new() { Name = "Sea Creature Chance", Description = "Increases rare sea creature spawn rate (hunting)", EstimatedMultiplier = 1.2 },
        new() { Name = "Lure enchantment", Description = "Reduces time between bites", EstimatedMultiplier = 1.15 }
    ];
}

// ── Regular Fishing (no sea creature hunting) ──
public class PiscaryFishingTask : BaseFishingTask
{
    protected override string MethodName => "Piscary Fishing";
    protected override HashSet<string> Locations => ["Piscary"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 250), new("ENCHANTED_RAW_FISH", 25)];
    protected override double ActionsPerHour => 275;
    protected override string HowTo => "Go to Piscary on Galatea and fish. Use a fishing rod with Lure and Blessing enchantments for best results.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ROD_OF_THE_SEA", Reason = "Fishing rod" }];
}
public class BayouFishingTask : BaseFishingTask
{
    protected override string MethodName => "Bayou Fishing";
    protected override HashSet<string> Locations => ["Backwater Bayou"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200), new("WATER_LILY", 80)];
}
public class BayouHotspotFishingTask : BaseFishingTask
{
    protected override string MethodName => "Bayou Hotspot Fishing";
    protected override HashSet<string> Locations => ["Backwater Bayou"];
    protected override HashSet<string> DetectionItems => ["HOTSPOT_CATCH"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 300), new("WATER_LILY", 120)];
}
public class SpookyFishingTask : BaseFishingTask
{
    protected override string MethodName => "Spooky Fishing";
    protected override HashSet<string> Locations => ["Spooky Festival", "The Park"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200), new("PUMPKIN", 50)];
}
public class WinterFishingTask : BaseFishingTask
{
    protected override string MethodName => "Winter Fishing";
    protected override HashSet<string> Locations => ["Jerry's Workshop", "Jerry Pond", "Hot Springs"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 220), new("ICE", 100)];
}
public class WaterWormFishingTask : BaseFishingTask
{
    protected override string MethodName => "Water Worm Fishing";
    // Water Worm only spawns in the Goblin Holdout (Crystal Hollows); drops gems + membrane, not raw fish.
    protected override HashSet<string> Locations => ["Crystal Hollows", "Goblin Holdout"];
    protected override string Where => "Goblin Holdout";
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("ROUGH_AMBER_GEM", 60), new("WORM_MEMBRANE", 9)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Water_Worm";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Equip a good fishing rod." },
        new() { Number = 2, Text = "Type /warp crystals to get close.", OnClick = "/warp crystals" },
        new() { Number = 3, Text = "Go into the Crystal Hollows and find the Goblin Holdout area.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Fish in the water there - Water Worm sea creatures show up and drop gems.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you collect: Amber Gemstones and Worm Membrane. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell your gemstones and Worm Membrane on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class QuarryFishingTask : BaseFishingTask
{
    protected override string MethodName => "Quarry Fishing";
    protected override HashSet<string> Locations => ["The Quarry", "Quarry"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class CrimsonFishingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Fishing";
    protected override HashSet<string> Locations => ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 150)];
}
public class CrimsonHotspotFishingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Hotspot Fishing";
    protected override HashSet<string> Locations => ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber"];
    protected override HashSet<string> DetectionItems => ["HOTSPOT_CATCH"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 200)];
}
public class FestivalFishingTask : BaseFishingTask
{
    protected override string MethodName => "Festival Fishing";
    protected override HashSet<string> Locations => ["Festival Plaza", "Jerry's Workshop"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class SquidFishingTask : BaseFishingTask
{
    protected override string MethodName => "Squid Fishing";
    protected override HashSet<string> Locations => ["Squid Cave", "Murkwater Depths", "Murkwater Shallows", "Driptoad Delve"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("INK_SACK", 200), new("ENCHANTED_INK_SACK", 20)];
}
public class GalateaFishingMethodTask : BaseFishingTask
{
    protected override string MethodName => "Galatea Fishing";
    protected override HashSet<string> Locations => ["Driptoad Delve", "Murkwater Depths", "Murkwater Shallows", "Squid Cave", "Reefguard Pass"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("SEA_LUMIES", 150), new("RAW_FISH", 180)];
}
public class LotusAtollTask : BaseFishingTask
{
    protected override string MethodName => "Lotus Atoll";
    protected override HashSet<string> Locations => ["Lotus Atoll"];
    // The lily pads pushed together are WATER_LILY/LOTUS; the pile explodes into sea
    // creatures and fish that are caught for SHARD_LOTUS_FISH, which is the main value.
    // Shards are deliberately not excluded so their value counts toward this activity.
    protected override HashSet<string> DetectionItems => ["LOTUS", "WATER_LILY"];
    // Lotus Atoll is its own separate Fishing island (reached via the Ship Navigator NPC on
    // Backwater Bayou) - NOT part of Galatea, despite the name similarity. Corrected 2026-09,
    // verified hypixelskyblock.minecraft.wiki/w/Lotus_Atoll (see SkyblockZones.cs).
    protected override string HowTo => "Go to Lotus Atoll (via the Ship Navigator on Backwater Bayou) and push lily pads across the pond into one big pile. It explodes into sea creatures and fish you catch for Lotusfish shards.";
    // Reference sample (Ekwav, ~5 min at Lotus Atoll): LOTUS 57, WATER_LILY 64, SHARD_LOTUS_FISH 22.
    protected override List<MethodDrop> FormulaDrops => [new("WATER_LILY", 700), new("LOTUS", 650), new("SHARD_LOTUS_FISH", 250)];
}
public class OasisFishingTask : BaseFishingTask
{
    protected override string MethodName => "Oasis Fishing";
    protected override HashSet<string> Locations => ["Oasis", "Mushroom Desert"];
    protected override bool ExcludeShardItems => true;
    // Oasis Sheep sea creature drops mutton; raw fish is negligible here.
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MUTTON", 30), new("ENCHANTED_COOKED_MUTTON", 0.2)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Oasis";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Equip a good fishing rod." },
        new() { Number = 2, Text = "Type /warp desert to get close.", OnClick = "/warp desert" },
        new() { Number = 3, Text = "Go to the Oasis area in the Mushroom Desert (The Farming Islands) - it's the water pond in the middle of the desert.", OnClick = WikiUrl },
        new() { Number = 4, Text = "Fish there. Oasis Sheep sea creatures show up and drop mutton." },
        new() { Number = 5, Text = "You're doing it right when you collect: Enchanted Mutton. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell your mutton on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class WaterFishingTask : BaseFishingTask
{
    protected override string MethodName => "Water Fishing";
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class MagmaCoreFishingTask : BaseFishingTask
{
    protected override string MethodName => "Magma Core Fishing";
    protected override HashSet<string> Locations => ["Magma Fields", "Crystal Hollows"];
    protected override HashSet<string> DetectionItems => ["MAGMA_CORE"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_CORE", 8)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Magma_Core";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Equip a good fishing rod." },
        new() { Number = 2, Text = "Type /warp crystals to get close.", OnClick = "/warp crystals" },
        new() { Number = 3, Text = "Go into the Crystal Hollows and find the lava in Magma Fields.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Fish in the lava there with a Lava fishing rod to catch Magma Core.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you collect: Magma Core. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell Magma Core on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class FlamingWormFishingTask : BaseFishingTask
{
    protected override string MethodName => "Flaming Worm Fishing";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Precursor Remnants"];
    protected override HashSet<string> DetectionItems => ["WORM_MEMBRANE"];
    protected override List<MethodDrop> FormulaDrops => [new("ROUGH_SAPPHIRE_GEM", 250), new("WORM_MEMBRANE", 2.5), new("ETERNAL_FLAME_RING", 0.05)];
}

// ── Fishing with Sea Creature Hunting ──
public class PiscaryFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Piscary Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Piscary"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 250), new("ENCHANTED_RAW_FISH", 25)];
}
public class BayouFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Bayou Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Backwater Bayou"];
    protected override bool RequireShardItems => true;
    // Alligator/Titanoboa sea creatures drop attribute shards; that is the value, not raw fish.
    protected override List<MethodDrop> FormulaDrops => [new("WATER_LILY", 80), new("SHARD_ALLIGATOR", 6), new("SHARD_TITANOBOA", 0.5)];
}
public class BayouHotspotFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Bayou Hotspot Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Backwater Bayou"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 300), new("WATER_LILY", 120)];
}
public class SpookyFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Spooky Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Spooky Festival", "The Park"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200), new("PUMPKIN", 50)];
}
public class WinterFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Winter Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Jerry's Workshop", "Jerry Pond", "Hot Springs"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 220), new("ICE", 100)];
}
public class WaterWormFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Water Worm Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Goblin Holdout"];
    protected override string Where => "Goblin Holdout";
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("WORM_MEMBRANE", 50)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Water_Worm";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Equip a good fishing rod and a weapon for the fight after." },
        new() { Number = 2, Text = "Type /warp crystals to get close.", OnClick = "/warp crystals" },
        new() { Number = 3, Text = "Go into the Crystal Hollows and find the Goblin Holdout area.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Fish there until a Water Worm bites, then kill it for its shard.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you collect: Water Worm shards. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell the shards on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class QuarryFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Quarry Fishing (Hunting)";
    protected override HashSet<string> Locations => ["The Quarry", "Quarry"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class CrimsonFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 150)];
}
public class FestivalFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Festival Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Festival Plaza", "Jerry's Workshop"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class SquidFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Squid Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Squid Cave", "Murkwater Depths", "Murkwater Shallows", "Driptoad Delve"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("INK_SACK", 200), new("ENCHANTED_INK_SACK", 20)];
}
public class GalateaFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Galatea Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Driptoad Delve", "Murkwater Depths", "Murkwater Shallows", "Squid Cave", "Reefguard Pass"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("SEA_LUMIES", 150), new("RAW_FISH", 180)];
}
public class OasisFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Oasis Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Oasis", "Mushroom Desert"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class WaterFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Water Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park"];
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
