using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base for Galatea mob farm tasks ──
public abstract class BaseGalateaMobTask : MethodTask
{
    public override List<StatFactor> StatFactors =>
    [
        new("skill:Combat", 0.4, 60),
        new("hotf:tier", 0.3, 10),
        new("skill:Hunting", 0.3, 50)
    ];
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Magic Find", Description = "Increases chance of rare drops", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level increases damage and XP", EstimatedMultiplier = 1.1 },
        new() { Name = "Pet Luck", Description = "Increases pet drop chance from mobs", EstimatedMultiplier = 1.15 }
    ];
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon for mob farming" }
    ];
}

// ── Galatea mobs ──
public class CinderbatTask : BaseGalateaMobTask
{
    protected override string MethodName => "Cinderbat";
    protected override HashSet<string> Locations => ["Dive-Ember Pass", "Stride-Ember Fissure", "Side-Ember Way"];
    protected override HashSet<string> DetectionItems => ["SHARD_CINDER_BAT"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_CINDER_BAT", 300)];
    protected override string HowTo => "Go to the Ember areas on Galatea and kill Cinderbats. They spawn in lava biome areas.";
}
public class BurningsoulTask : BaseGalateaMobTask
{
    // Hypixel renamed the shard to "Inferno Demonlord Shard" (item tag stays SHARD_BURNINGSOUL) and,
    // per the community wiki (hypixelskyblock.minecraft.wiki/w/Burningsoul_Shard) and the
    // 2026-04-30 changelog, it is Epic and drops ONLY from the Inferno Demonlord boss (Blaze Slayer,
    // Crimson Isle) - T2 1.54%, T3 2.90%, T4 5.27% per kill, 1 per drop. It no longer drops from
    // Galatea ember zone mobs or the old Burningsoul Demon miniboss.
    protected override string MethodName => "Inferno Demonlord";
    // Bare island name "Crimson Isle" deliberately excluded - IndividualSlayerTask/MethodTask both
    // match via SkyblockZones.Matches, so an island-level entry would wrongly count every Crimson
    // Isle zone (Mycelium mining in Mystic Marsh, fishing in Scarleton/Oasis, ...) as this drop.
    protected override HashSet<string> Locations => ["Smoldering Tomb", "Stronghold"];
    protected override HashSet<string> DetectionItems => ["SHARD_BURNINGSOUL"];
    // ~20 T4 Inferno Demonlord kills/h (typical Blaze Slayer speed-farm) * 5.27% T4 drop chance.
    // Only the shard is modeled - the other T4 Blaze Slayer boss drops (Derelict Ashe, Blaze Rods,
    // dye chances) have no verified rates/tags elsewhere in this codebase to reuse here.
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BURNINGSOUL", 20 * 0.0527)];
    protected override string HowTo => "Buy a Maddox Batphone, start a Blaze Slayer quest at Tier 4, grind Blazes in the Smoldering Tomb until the Inferno Demonlord boss spawns, then kill it for a chance at an Inferno Demonlord Shard.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Burningsoul_Shard";
    protected override List<TaskStep> Steps => InfernoDemonlordGuide.Steps(4);
}
public class LumisquidTask : BaseGalateaMobTask
{
    protected override string MethodName => "Lumisquid";
    protected override HashSet<string> Locations => ["Murkwater Depths", "Murkwater Shallows", "Squid Cave"];
    protected override HashSet<string> DetectionItems => ["SHARD_LUMISQUID"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_LUMISQUID", 280)];
    protected override string HowTo => "Go to the Murkwater areas on Galatea and kill Lumisquids in the underwater caves.";
}
public class ShellwiseTask : BaseGalateaMobTask
{
    protected override string MethodName => "Shellwise";
    protected override HashSet<string> Locations => ["Reefguard Pass", "Murkwater Depths", "South Reaches"];
    protected override HashSet<string> DetectionItems => ["SHARD_SHELLWISE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SHELLWISE", 270)];
    protected override string HowTo => "Go to Reefguard Pass or South Reaches on Galatea and kill Shellwise mobs.";
}
public class MatchoTask : BaseGalateaMobTask
{
    protected override string MethodName => "Matcho";
    // Matcho is launched from the Blazing Volcano eruption on the Crimson Isle, not Galatea.
    // Verified on the community wiki 2026-09: every 120-150 minutes the volcano erupts and
    // launches coal blocks across the whole island; a Matcho spawns wherever one lands. Unlike the
    // slayer bosses (see SlayerTasks.cs), the bare island name "Crimson Isle" here is intentional,
    // not the bug it would be for a slayer - this drop really is island-wide by game design.
    protected override HashSet<string> Locations => ["Crimson Isle", "Blazing Volcano"];
    protected override HashSet<string> DetectionItems => ["SHARD_MATCHO"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MATCHO", 260)];
    protected override string HowTo => "Wait for the Blazing Volcano on the Crimson Isle to erupt (every 120-150 minutes), then look for Matchos anywhere a coal block landed and kill them.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Matcho";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get your gear: a good sword or bow for fighting a level 100 mob." },
        new() { Number = 2, Text = "Type /warp isle to get close.", OnClick = "/warp isle" },
        new() { Number = 3, Text = "Watch the Blazing Volcano - every 120-150 minutes it erupts with a loud explosion.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Blazing_Volcano" },
        new() { Number = 4, Text = "After it erupts, blocks (including coal) land all over the Crimson Isle. Wherever a coal block lands, a Matcho appears.", OnClick = WikiUrl },
        new() { Number = 5, Text = "Find and kill the Matchos before someone else does." },
        new() { Number = 6, Text = "You're doing it right when you collect: Matcho Shard. That's what we track for your coins/hour." },
        new() { Number = 7, Text = "Sell the shards on the Bazaar." },
        new() { Number = 8, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class StridersurferTask : BaseGalateaMobTask
{
    protected override string MethodName => "Stridersurfer";
    protected override HashSet<string> Locations => ["Stride-Ember Fissure", "Side-Ember Way", "Dive-Ember Pass"];
    protected override HashSet<string> DetectionItems => ["SHARD_STRIDER_SURFER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_STRIDER_SURFER", 320)];
    protected override string HowTo => "Go to the Ember areas on Galatea and kill Stridersurfers. Higher drop rate than other Ember mobs.";
}
public class SporeTask : BaseGalateaMobTask
{
    protected override string MethodName => "Spore";
    protected override HashSet<string> Locations => ["Moonglade Marsh", "Wyrmgrove Tomb", "Tomb Floodway"];
    protected override HashSet<string> DetectionItems => ["SHARD_SPORE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SPORE", 300)];
    protected override string HowTo => "Go to Moonglade Marsh or Wyrmgrove Tomb on Galatea and kill Spore mobs.";
}
public class BladesoulTask : BaseGalateaMobTask
{
    protected override string MethodName => "Bladesoul";
    // Bladesoul is a respawning boss in the Stronghold (Crimson Isle); it drops no shard.
    protected override HashSet<string> Locations => ["Stronghold"];
    protected override HashSet<string> DetectionItems => ["HALLOWED_SKULL"];
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("HALLOWED_SKULL", 12), new("COAL", 300), new("ENCHANTED_COAL", 12),
        new("MAGMA_URCHIN", 0.24), new("RAGNAROCK_AXE", 0.06)
    ];
    protected override string HowTo => "Go to the Stronghold on the Crimson Isle and kill the Bladesoul boss (respawns ~5 min; needs 1M+ damage to qualify for loot).";
}
public class JoydiveTask : BaseGalateaMobTask
{
    protected override string MethodName => "Joydive";
    protected override HashSet<string> Locations => ["Murkwater Shallows", "Murkwater Depths"];
    protected override HashSet<string> DetectionItems => ["SHARD_JOYDIVE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_JOYDIVE", 280)];
    protected override string HowTo => "Go to Tranquil Pass or Verdant Summit on Galatea and kill Joydive mobs.";
}
public class DrownedTask : BaseGalateaMobTask
{
    protected override string MethodName => "Drowned";
    // Kelpwoven Tunnels is for Spikes; Drowned (Tidetot/Hydrospear/Seacurse) is in the Drowned Reliquary.
    protected override HashSet<string> Locations => ["Drowned Reliquary", "Murkwater Depths"];
    protected override HashSet<string> DetectionItems => ["SHARD_DROWNED"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_DROWNED", 300)];
    protected override string HowTo => "Go to Drowned Reliquary or Kelpwoven Tunnels on Galatea and kill Drowned mobs.";
}
public class CoralotTask : BaseGalateaMobTask
{
    protected override string MethodName => "Coralot";
    // Island key (2026-09-27 production log finding: the shard shows up all over Moonglade, not
    // just the zones this used to list) - the shard tag is the specific evidence.
    protected override HashSet<string> Locations => ["Moonglade"];
    // Island keys are never zones themselves (see SkyblockZones.cs) - name a real zone so Where/
    // Island/WikiUrl/Warp still resolve (same fix as HelixForagingTask/FigForagingTask).
    protected override string Where => "Murkwater Loch";
    protected override HashSet<string> DetectionItems => ["SHARD_CORALOT"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_CORALOT", 260)];
    protected override string HowTo => "Go to the reef areas on Galatea and kill Coralot mobs.";
}
public class BambuleafTask : BaseGalateaMobTask
{
    protected override string MethodName => "Bambuleaf";
    // Island key - see CoralotTask's comment.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "North Wetlands";
    protected override HashSet<string> DetectionItems => ["SHARD_BAMBULEAF"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BAMBULEAF", 260)];
    protected override string HowTo => "Go to the Wetlands on Galatea and kill Bambuleaf mobs in the marshy areas.";
}
public class HideonleafTask : BaseGalateaMobTask
{
    protected override string MethodName => "Hideonleaf";
    // Island key - see CoralotTask's comment.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "North Wetlands";
    protected override HashSet<string> DetectionItems => ["SHARD_HIDEONLEAF"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_HIDEONLEAF", 250)];
    protected override string HowTo => "Go to the Wetlands or Evergreen Plateau on Galatea and kill Hideonleaf mobs.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Hideonleaf";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get your gear: a good sword or bow." },
        new() { Number = 2, Text = "Type /warp galatea to get close.", OnClick = "/warp galatea" },
        new() { Number = 3, Text = "Go to the Wetlands (North or South) or Evergreen Plateau on Galatea.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Look for and kill Hideonleaf mobs - they like to hide, so look closely.", OnClick = WikiUrl },
        new() { Number = 5, Text = "You're doing it right when you collect: Hideonleaf Shard. That's what we track for your coins/hour." },
        new() { Number = 6, Text = "Sell the shards on the Bazaar." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class DreadwingTask : BaseGalateaMobTask
{
    protected override string MethodName => "Dreadwing";
    // Island key - see CoralotTask's comment (Dreadwing spawns from breaking trees, reported all
    // over Moonglade, not just Moonglade Marsh).
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Moonglade Marsh";
    protected override HashSet<string> DetectionItems => ["SHARD_DREADWING"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_DREADWING", 240)];
    protected override string HowTo => "Go to Wyrmgrove Tomb or Ancient Ruins on Galatea and kill Dreadwing mobs.";
}
public class SpikeTask : BaseGalateaMobTask
{
    protected override string MethodName => "Spike";
    protected override HashSet<string> Locations => ["Kelpwoven Tunnels", "Murkwater Shallows"];
    protected override HashSet<string> DetectionItems => ["SHARD_SPIKE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SPIKE", 270)];
    protected override string HowTo => "Go to the underwater areas on Galatea and kill Spike mobs.";
}
public class SeerTask : BaseGalateaMobTask
{
    protected override string MethodName => "Seer";
    // The Seer is in Dragon's Nest (bottom of The End), not on Galatea.
    protected override HashSet<string> Locations => ["Dragon's Nest"];
    protected override HashSet<string> DetectionItems => ["SHARD_SEER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SEER", 240)];
    protected override string HowTo => "Go to Ancient Ruins or Tranquility Sanctum on Galatea and kill Seer mobs.";
}
public class MochibearkTask : BaseGalateaMobTask
{
    protected override string MethodName => "Mochibear";
    // Island key - see CoralotTask's comment.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "North Wetlands";
    protected override HashSet<string> DetectionItems => ["SHARD_MOCHIBEAR"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MOCHIBEAR", 250)];
    protected override string HowTo => "Go to the Wetlands or Evergreen Plateau on Galatea and kill Mochibear mobs.";
}
public class MossybitTask : BaseGalateaMobTask
{
    protected override string MethodName => "Mossybit";
    // Island key - see CoralotTask's comment.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "North Wetlands";
    protected override HashSet<string> DetectionItems => ["SHARD_MOSSYBIT"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MOSSYBIT", 250)];
    protected override string HowTo => "Go to the Wetlands or Evergreen Plateau on Galatea and kill Mossybit mobs.";
}

// ── Non-Galatea mobs ──
public class VoraciousSpiderTask : MethodTask
{
    protected override string MethodName => "Voracious Spider";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Spider's Den", "The Spider's Den", "Arachne's Sanctuary", "Spider Mound"];
    // Voracious Spiders drop String (100%) + Spider Eye; Tarantula Web is a Broodfather-boss drop, not this mob.
    protected override HashSet<string> DetectionItems => ["SHARD_VORACIOUS_SPIDER", "STRING"];
    protected override List<MethodDrop> FormulaDrops => [new("STRING", 4000), new("SPIDER_EYE", 300)];
    protected override string HowTo => "Go to the Spider's Den and kill Voracious Spiders. Good for Tarantula Web drops.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 },
        new() { Name = "Looting enchantment", Description = "Increases drop quantity", EstimatedMultiplier = 1.15 }
    ];
}
public class GoldenGhoulTask : MethodTask
{
    protected override string MethodName => "Golden Ghoul";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    // Golden Ghouls spawn in the Hub Crypts (via Graveyard/Coal Mine); Golden Powder is a rare 0.05% drop.
    // GOLD_INGOT: Golden Ghouls also drop 1-10 Gold Ingots per kill - see RevenantSlayerTask
    // (SlayerTasks.cs), which shares this zone (Revenant Horror is also farmed in the Hub Crypts)
    // and takes priority whenever its own Revenant Flesh is present.
    protected override HashSet<string> Locations => ["Crypts", "Graveyard", "Coal Mine"];
    protected override HashSet<string> DetectionItems => ["SHARD_GOLDEN_GHOUL", "GOLDEN_POWDER", "GOLD_INGOT"];
    protected override List<MethodDrop> FormulaDrops => [new("GOLDEN_POWDER", 0.5)];
    protected override string HowTo => "Go to the Ruins or Graveyard and kill Golden Ghouls for Golden Powder.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 }
    ];
}
public class StarSentryTask : MethodTask
{
    protected override string MethodName => "Star Sentry";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    // Star Sentry spawns in the Dwarven Mines during the Fallen Star event, not in The End.
    protected override HashSet<string> Locations => ["Dwarven Mines", "Royal Mines", "Rampart's Quarry"];
    protected override HashSet<string> DetectionItems => ["SHARD_STAR_SENTRY"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_STAR_SENTRY", 200)];
    protected override string HowTo => "Go to The End and kill Star Sentries in the Dragon's Nest area.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 }
    ];
}
public class AutomatonTask : MethodTask
{
    protected override string MethodName => "Automaton";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Precursor Remnants", "Lost Precursor City", "Crystal Hollows"];
    protected override HashSet<string> DetectionItems => ["CONTROL_SWITCH", "ELECTRON_TRANSMITTER", "FTX_3070", "ROBOTRON_REFLECTOR", "SUPERLITE_MOTOR", "SYNTHETIC_HEART"];
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("CONTROL_SWITCH", 3), new("ELECTRON_TRANSMITTER", 3), new("FTX_3070", 3),
        new("ROBOTRON_REFLECTOR", 3), new("SUPERLITE_MOTOR", 3), new("SYNTHETIC_HEART", 3)
    ];
    protected override string HowTo => "Go to the Precursor Remnants in the Crystal Hollows and kill Automatons for robot parts.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 }
    ];
}
public class XyzMobTask : MethodTask
{
    protected override string MethodName => "Xyz";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Precursor Remnants", "Lost Precursor City"];
    protected override HashSet<string> DetectionItems => ["SHARD_XYZ"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_XYZ", 200)];
    protected override string HowTo => "Go to Crystal Hollows and kill Xyz mobs in the Precursor area.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 }
    ];
}
public class GhostMobTask : MethodTask
{
    protected override string MethodName => "Ghost";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Dwarven Mines", "The Mist", "Goblin Holdout"];
    protected override HashSet<string> DetectionItems => ["SHARD_GHOST"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_GHOST", 50)];
    protected override string HowTo => "Go to The Mist in the Dwarven Mines and kill Ghosts. They drop Ghost shards used in the Hunting system.";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 },
        new() { Name = "Kill speed", Description = "Faster killing means more coins per hour", EstimatedMultiplier = 1.3 }
    ];
}

/// <summary>Goblins in the Goblin Burrows (Dwarven Mines) - drop colored Goblin Eggs and Goblin armor pieces.</summary>
public class GoblinFarmingTask : MethodTask
{
    protected override string MethodName => "Goblin Farming";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Goblin Burrows"];
    protected override HashSet<string> DetectionItems =>
        ["GOBLIN_EGG", "GOBLIN_EGG_RED", "GOBLIN_EGG_YELLOW", "GOBLIN_EGG_GREEN", "GOBLIN_HELMET", "GOBLIN_CHESTPLATE", "GOBLIN_LEGGINGS", "GOBLIN_BOOTS"];
    // Production median (2026-09) per 10-min period * 6: GOBLIN_EGG 3.
    protected override List<MethodDrop> FormulaDrops => [new("GOBLIN_EGG", 18)];
    protected override string HowTo => "Go to the Goblin Burrows in the Dwarven Mines and kill Goblins for their eggs and armor pieces.";
    // no RequiredItems/Effects: Goblins are low level mobs, no specific gear or stat is verified to matter
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Goblin_Burrows";
}

// ── Combat farm tasks (discovered from unclassified production revenue 2026-09) ──
// The shared BaseCombatFarmTask (skill:Combat 0.7 + gear:HUNT_WEAPON 0.3, a generic Looting effect)
// was removed 2026-09: HUNT_WEAPON is the Galatea Hunting-axe ladder and has nothing to do with
// Ghosts or Blazes, so it showed players irrelevant "how to replicate" info. Each task below now
// carries its own accurate StatFactors/RequiredItems/Effects instead.

public class GhostMistTask : MethodTask
{
    // Distinct from GhostHuntingTask (SHARD_GHOST, "Ghost (Hunting)") - both share The Mist, the
    // classifier's matched-value tie-break decides when a window has evidence for both.
    protected override string MethodName => "Ghost (The Mist)";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["The Mist"];
    protected override HashSet<string> DetectionItems => ["SORROW", "VOLTA", "PLASMA"];
    // Production medians (2026-09).
    protected override List<MethodDrop> FormulaDrops => [new("VOLTA", 25), new("SORROW", 8), new("PLASMA", 10)];
    protected override double ActionsPerHour => 2000;
    // Verified on the community wiki 2026-09: Ghosts have 1,000,000 HP and are immune to bows/arrows,
    // magic and ability damage - only melee weapon hits work (Fire Aspect/Venomous enchant procs
    // still apply on a melee hit). No gear:HUNT_WEAPON factor - that's the unrelated Galatea
    // Hunting-axe ladder. Mining XII gates access to The Mist, so it matters as much as Combat here.
    public override List<StatFactor> StatFactors => [new("skill:Combat", 0.6, 60), new("skill:Mining", 0.4, 60)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Ghost";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "GIANTS_SWORD", Reason = "High-damage melee weapon - Ghosts are immune to bows, magic and abilities" }
    ];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Magic Find", Description = "More Sorrow/Plasma drops", EstimatedMultiplier = 1.2 },
        new() { Name = "Strength & Crit Damage", Description = "Faster kills - Ghosts have 1M HP", EstimatedMultiplier = 1.15 }
    ];
    protected override string HowTo =>
        "Type /warp mines and go down into The Mist (needs Mining XII). Ghosts have 1M HP and can only be "
        + "damaged by melee weapon hits - bows, magic and abilities do nothing. Target the ones standing on "
        + "stone walls, and sell the Sorrow, Volta and Plasma they drop on the Bazaar.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Level Mining to XII - needed to reach The Mist." },
        new() { Number = 2, Text = "Get a high-damage melee weapon: Giant's Sword, Emerald Blade or Dark Claymore." },
        new() { Number = 3, Text = "Type /warp mines to get close.", OnClick = "/warp mines" },
        new() { Number = 4, Text = "Go down into The Mist, in the Dwarven Mines.", OnClick = WhereWikiUrl },
        new() { Number = 5, Text = "Ghosts have 1M HP and are immune to bows, magic and abilities - only melee weapon hits work.", OnClick = WikiUrl },
        new() { Number = 6, Text = "Target the Ghosts standing on stone walls - they're the easiest to hit." },
        new() { Number = 7, Text = "You're doing it right when you start collecting: SORROW, VOLTA, PLASMA. That's what we track for your coins/hour." },
        new() { Number = 8, Text = "Sell Sorrow, Volta and Plasma on the Bazaar." },
        new() { Number = 9, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}

// Renamed from BlazeFarmingTask 2026-09 ("Blaze Farming (Smoldering Tomb)" -> "Blaze Slayer",
// Category "Mob Farming" -> "Slayer"): Derelict Ashe is only dropped by the Inferno Demonlord
// (Blaze Slayer boss), never by regular Blazes, so periods collecting DERELICT_ASHE are Blaze
// Slayer sessions, not passive mob farming - production data (2026-09 median 4 Derelict Ashe + 25
// Blaze Rods per period) shows this spread over Smoldering Tomb, The Wasteland, Stronghold, Magma
// Chamber and Mystic Marsh, not just the Tomb.
public class BlazeSlayerTask : MethodTask
{
    protected override string MethodName => "Blaze Slayer";
    protected override string Category => "Slayer";
    protected override string ActionUnit => "kills";
    // Bare island key (not just "Smoldering Tomb") - the boss/its Blazes are fought across the
    // whole Crimson Isle. This is safe against false positives from unrelated Crimson Isle
    // activity (Mycelium mining, fishing, ...) because the required DetectionItems below
    // (DERELICT_ASHE) is exclusive to this boss - see BurningsoulTask's own comment for why a bare
    // island name is normally risky for a slayer boss.
    protected override HashSet<string> Locations => ["Crimson Isle"];
    protected override string Where => "Smoldering Tomb";
    // Must not steal from BurningsoulTask (SHARD_BURNINGSOUL, "Inferno Demonlord"): that task
    // requires its own shard, this one requires only the boss-exclusive Derelict Ashe, so a window
    // with only one of the two only ever matches the corresponding task.
    protected override HashSet<string> DetectionItems => ["DERELICT_ASHE"];
    // Production medians (2026-09): 4 Derelict Ashe + 25 Blaze Rods per period, per hour below.
    protected override List<MethodDrop> FormulaDrops => [new("DERELICT_ASHE", 60), new("BLAZE_ROD", 400), new("BLAZE_ASHES", 20)];
    protected override double ActionsPerHour => 4; // bosses, not regular kills
    public override List<StatFactor> StatFactors => [new("skill:Combat", 1.0, 60)];
    // No RequiredItems - the weapon choice is covered in the Steps below instead.
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Inferno_Demonlord";
    protected override string HowTo =>
        "Buy a Maddox Batphone, start a Blaze Slayer quest, and kill Blazes in the Smoldering Tomb for Combat "
        + "XP until the Inferno Demonlord boss spawns. Kill the boss - it's the only source of Derelict Ashe. "
        + "Sell Derelict Ashe and Blaze Rods on the Bazaar.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "The first time, buy a Maddox Batphone from Maddox (at the Hub Tavern) so you can start Slayer quests from anywhere, including the Crimson Isle.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Maddox_Batphone" },
        new() { Number = 2, Text = "Type /warp isle to get close.", OnClick = "/warp isle" },
        new() { Number = 3, Text = "Talk to Maddox (or use your Batphone) and start a Blaze Slayer quest.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Maddox" },
        new() { Number = 4, Text = "Go to the Smoldering Tomb and kill regular Blazes there - that's the recommended spot for Blaze Slayer Combat XP.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Smoldering_Tomb" },
        new() { Number = 5, Text = "Bring a Juju Shortbow or Terminator and Fire Resistance - Blazes and the boss hit hard with fire damage." },
        new() { Number = 6, Text = "Once you've killed enough Blazes, the Inferno Demonlord boss spawns - kill it to finish the quest.", OnClick = WikiUrl },
        new() { Number = 7, Text = "You're doing it right when you start collecting: DERELICT_ASHE, BLAZE_ROD, BLAZE_ASHES. That's what we track for your coins/hour." },
        new() { Number = 8, Text = "Sell Derelict Ashe and Blaze Rods on the Bazaar." },
        new() { Number = 9, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
