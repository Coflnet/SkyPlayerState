using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Real fishing evidence tags, split by water body - see the 2026-09 production log finding that
/// location-only fishing tasks (no DetectionItems) act as catch-alls for whole islands: "Water
/// Fishing" absorbed Hub Auction House/Bazaar Alley purchases, zombie slayer at Crypts/Graveyard and
/// farming at Farm; "Water Worm Fishing" absorbed Crystal Hollows hard-stone/powder mining; "Spooky
/// Fishing" absorbed wolf slayer at Soul Cave; "Crimson Fishing" absorbed spider/mage-outlaw/magma-cube
/// drops; "Oasis Fishing" absorbed rabbit/sheep drops. Every fishing task that isn't tied to its own
/// dedicated sea creature (Lotus Atoll, Magma Core, Flaming Worm, ...) now requires one of these tags
/// instead of matching on location + a generic 5-item floor.
/// </summary>
internal static class FishingEvidence
{
    /// <summary>Overworld/freshwater fishing drops - used by every non-lava, non-Bayou/Squid/Galatea/Lotus fishing task.</summary>
    public static readonly HashSet<string> Water =
    [
        "RAW_FISH", "RAW_FISH:1", "RAW_FISH:2", "RAW_FISH:3",
        "ENCHANTED_RAW_FISH", "ENCHANTED_RAW_SALMON", "ENCHANTED_CLOWNFISH", "ENCHANTED_PUFFERFISH",
        "PRISMARINE_SHARD", "PRISMARINE_CRYSTALS", "ENCHANTED_PRISMARINE_SHARD", "ENCHANTED_PRISMARINE_CRYSTALS",
        "SPONGE", "CLAY_BALL", "ENCHANTED_CLAY_BALL",
        "WATER_LILY", "ENCHANTED_WATER_LILY",
        "INK_SACK", "ENCHANTED_INK_SACK"
    ];

    /// <summary>Trophy Fish species (verified production tags) - crossed with <see cref="Tiers"/> below to build the 72 <c>SPECIES_TIER</c> tags in <see cref="Lava"/>.</summary>
    private static readonly string[] TrophyFishSpecies =
    [
        "BLOBFISH", "FLYFISH", "GOLDEN_FISH", "GUSHER", "KARATE_FISH", "LAVA_HORSE", "MANA_RAY", "MOLDFIN",
        "OBFUSCATED_FISH_1", "OBFUSCATED_FISH_2", "OBFUSCATED_FISH_3", "SKELETON_FISH", "SLUGFISH", "SOUL_FISH",
        "STEAMING_HOT_FLOUNDER", "SULPHUR_SKITTER", "VANILLE", "VOLCANIC_STONEFISH"
    ];
    private static readonly string[] Tiers = ["BRONZE", "SILVER", "GOLD", "DIAMOND"];

    private static readonly string[] LavaBaseDrops =
    [
        "MAGMA_FISH", "MAGMA_FISH_SILVER", "MAGMA_FISH_GOLD", "MAGMA_FISH_DIAMOND",
        "LUMP_OF_MAGMA", "MOOGMA_PELT", "CUP_OF_BLOOD", "PYROCLASTIC_SCALE", "FLAMING_HEART", "HORN_OF_TAURUS"
    ];

    /// <summary>Crimson Isle lava fishing drops - Magma Fish variants, Lava sea creature drops, and every Trophy Fish tier tag.</summary>
    public static readonly HashSet<string> Lava = new(
        LavaBaseDrops.Concat(TrophyFishSpecies.SelectMany(species => Tiers.Select(tier => $"{species}_{tier}"))));
}

internal static class SpookyFishingEvidence
{
    /// <summary>Drops only spooky sea creatures give (mandatory for Spooky Fishing).</summary>
    public static readonly HashSet<string> Exclusive = ["WEREWOLF_SKIN", "SOUL_FRAGMENT", "DEEP_SEA_ORB"];
    /// <summary>Optional extras that raise the matched value over plain Water Fishing.</summary>
    public static readonly HashSet<string> Evidence = new(FishingEvidence.Water)
        { "GREEN_CANDY", "PURPLE_CANDY", "PUMPKIN", "ENCHANTED_PUMPKIN" };
}

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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    // Spooky sea creatures spawn wherever water fishing works (Spooky Festival is not a zone).
    // Backwater Bayou is deliberately excluded - Bayou Fishing keeps its periods.
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park", "Spider's Den", "Crystal Hollows"];
    // Spooky-sea-creature exclusive drops are mandatory; the normal fish evidence and festival
    // items only add matched value so a spooky period outranks plain Water Fishing.
    protected override HashSet<string> DetectionItems => SpookyFishingEvidence.Exclusive;
    protected override HashSet<string> EvidenceItems => SpookyFishingEvidence.Evidence;
    protected override bool ExcludeShardItems => true;
    // default priority: the mandatory exclusive drops already make this strictly more specific than Water Fishing
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200), new("PUMPKIN", 50)];
}
public class WinterFishingTask : BaseFishingTask
{
    protected override string MethodName => "Winter Fishing";
    protected override HashSet<string> Locations => ["Jerry's Workshop", "Jerry Pond", "Hot Springs"];
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 220), new("ICE", 100)];
}
public class WaterWormFishingTask : BaseFishingTask
{
    protected override string MethodName => "Water Worm Fishing";
    // Water Worm only spawns in the pond at the Goblin Holdout (Crystal Hollows) - not island-wide,
    // and not any other Crystal Hollows sub-zone (2026-09 production log finding: the old island-wide
    // "Crystal Hollows" Location swallowed 143 hard-stone/powder mining periods).
    protected override HashSet<string> Locations => ["Goblin Holdout"];
    protected override string Where => "Goblin Holdout";
    protected override HashSet<string> DetectionItems => new(FishingEvidence.Water) { "WORM_MEMBRANE" };
    protected override bool ExcludeShardItems => true;
    // Amber/Jade/Thyst Mining and Goblin Holdout Powder Mining also list "Goblin Holdout" - without
    // priced tie-breaking WORM_MEMBRANE is the more specific evidence for this exact activity (gems
    // are an incidental byproduct of the sea creature fight, not the defining drop).
    protected override int Priority => 1;
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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class CrimsonFishingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Fishing";
    // "Stronghold", "Crimson Fields", "Dragontail" and the bare "Crimson Isle" island key are
    // data-derived (production log analysis 2026-09-27 showed 110 Magma Fish periods at Stronghold
    // alone) - the wiki only documents Blazing Volcano as the lava fishing hotspot, but the
    // scoreboard reports just "Crimson Isle" at spawn/other sub-zones, so it's included too.
    protected override HashSet<string> Locations =>
        ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber", "Stronghold", "Crimson Fields", "Dragontail", "Crimson Isle"];
    // Lava fishing evidence (2026-09 finding: 810 periods matched here, 445 of them with no fish at
    // all - spiders, Mage Outlaw, magma cubes from unrelated Crimson Isle activity).
    protected override HashSet<string> DetectionItems => FishingEvidence.Lava;
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 150)];
}
public class CrimsonHotspotFishingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Hotspot Fishing";
    // See CrimsonFishingTask - the extra zones are data-derived, not wiki-documented.
    protected override HashSet<string> Locations =>
        ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber", "Stronghold", "Crimson Fields", "Dragontail", "Crimson Isle"];
    protected override HashSet<string> DetectionItems => ["HOTSPOT_CATCH"];
    protected override bool ExcludeShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 200)];
}
public class FestivalFishingTask : BaseFishingTask
{
    protected override string MethodName => "Festival Fishing";
    protected override HashSet<string> Locations => ["Festival Plaza", "Jerry's Workshop"];
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park", "Spider's Den", "Crystal Hollows"];
    // Water evidence (2026-09 finding: 288 periods matched here, only ~10 with any fish - it was
    // swallowing Hub Auction House/Bazaar Alley purchases, zombie slayer at Crypts/Graveyard, and
    // farming at Farm).
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    // See SpookyFishingTask.
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park", "Spider's Den", "Crystal Hollows"];
    protected override HashSet<string> DetectionItems => SpookyFishingEvidence.Exclusive;
    protected override HashSet<string> EvidenceItems => SpookyFishingEvidence.Evidence;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200), new("PUMPKIN", 50)];
}
public class WinterFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Winter Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Jerry's Workshop", "Jerry Pond", "Hot Springs"];
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 220), new("ICE", 100)];
}
public class WaterWormFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Water Worm Fishing (Hunting)";
    // See WaterWormFishingTask - Water Worm only spawns in the pond at the Goblin Holdout.
    protected override HashSet<string> Locations => ["Goblin Holdout"];
    protected override string Where => "Goblin Holdout";
    protected override HashSet<string> DetectionItems => new(FishingEvidence.Water) { "WORM_MEMBRANE" };
    protected override bool RequireShardItems => true;
    // See WaterWormFishingTask's comment.
    protected override int Priority => 1;
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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class CrimsonFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Crimson Fishing (Hunting)";
    // Same Locations as CrimsonFishingTask - see its own comment for the data-derived zones.
    protected override HashSet<string> Locations =>
        ["Blazing Volcano", "Burning Desert", "Mystic Marsh", "Magma Chamber", "Stronghold", "Crimson Fields", "Dragontail", "Crimson Isle"];
    protected override HashSet<string> DetectionItems => FishingEvidence.Lava;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("MAGMA_FISH", 150)];
}
public class FestivalFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Festival Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Festival Plaza", "Jerry's Workshop"];
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
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
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
public class WaterFishingHuntingTask : BaseFishingTask
{
    protected override string MethodName => "Water Fishing (Hunting)";
    protected override HashSet<string> Locations => ["Hub", "Village", "Forest", "Birch Park", "The Park", "Spider's Den", "Crystal Hollows"];
    // Water evidence (2026-09 finding: this task absorbed Bazaar Alley shard purchases).
    protected override HashSet<string> DetectionItems => FishingEvidence.Water;
    protected override bool RequireShardItems => true;
    protected override List<MethodDrop> FormulaDrops => [new("RAW_FISH", 200)];
}
