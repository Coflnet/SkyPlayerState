using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base class for all hunting tasks ──
public abstract class BaseHuntingTask : MethodTask
{
    public override List<StatFactor> StatFactors =>
    [
        new("hotf:tier", 0.30, 10),
        new("skill:Hunting", 0.25, 50),
        new("gear:HUNT_WEAPON", 0.30, 5),
        new("skill:Combat", 0.15, 60)
    ];
    protected override string Category => "Hunting";
    protected override string ActionUnit => "kills";
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Magic Find", Description = "Increases chance of rare shard drops", EstimatedMultiplier = 1.3 },
        new() { Name = "Looting", Description = "Higher looting enchantment increases shard drop rate", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level increases kill speed", EstimatedMultiplier = 1.1 }
    ];
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "ASPECT_OF_THE_DRAGON", Reason = "Weapon for mob hunting" }
    ];
}

// ── Hunting methods (dedicated mob hunting, non-fishing) ──

public class RainSlimeHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Rain Slime (Hunting)";
    protected override HashSet<string> Locations => ["Spider's Den", "The Spider's Den"];
    protected override HashSet<string> DetectionItems => ["SHARD_RAIN_SLIME"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_RAIN_SLIME", 200)];
    protected override string HowTo => "Go to Spider's Den and hunt Rain Slimes. They spawn during rain events (first 20 minutes of each hour). Use a weapon with high damage.";
    /// <summary>
    /// Rain Slimes only spawn from :00 to :20 each hour
    /// </summary>
    protected override string CheckAccessibility(TaskParams parameters)
    {
        var minute = parameters.TestTime.Minute;
        if (minute >= 20)
            return $"Rain Slimes only spawn from :00 to :20 each hour. Available again in {60 - minute + 0} minutes.";
        return base.CheckAccessibility(parameters);
    }

    protected override DateTime? GetNextAvailableAt(TaskParams parameters)
    {
        var minute = parameters.TestTime.Minute;
        if (minute < 20)
            return null;
        return new DateTime(parameters.TestTime.Year, parameters.TestTime.Month, parameters.TestTime.Day,
            parameters.TestTime.Hour, 0, 0, parameters.TestTime.Kind).AddHours(1);
    }
}
public class HellwispHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Hellwisp (Hunting)";
    protected override HashSet<string> Locations => ["Magma Chamber", "Matriarch's Lair"];
    protected override HashSet<string> DetectionItems => ["SHARD_HELLWISP"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_HELLWISP", 200)];
    protected override string HowTo => "Go to Blazing Volcano or Burning Desert on the Crimson Isle and hunt Hellwisps. They are fire mobs in lava areas.";
}
public class XyzHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Xyz (Hunting)";
    // SHARD_XYZ drops from the Exe mob on the Crimson Isle (Mystic Marsh), not Crystal Hollows.
    protected override HashSet<string> Locations => ["Crimson Isle", "Mystic Marsh"];
    protected override HashSet<string> DetectionItems => ["SHARD_XYZ"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_XYZ", 250)];
    protected override string HowTo => "Go to Mystic Marsh on the Crimson Isle and hunt the Exe mob for Xyz shards. Use a strong weapon as they have high HP.";
}
public class KadaKnightHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Kada Knight (Hunting)";
    // Kada Knight spawns in caves near the Magma Chamber on the Crimson Isle, not the Galatea underwater zones.
    protected override HashSet<string> Locations => ["Crimson Isle", "Magma Chamber"];
    protected override HashSet<string> DetectionItems => ["SHARD_KADA_KNIGHT"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_KADA_KNIGHT", 230)];
    protected override string HowTo => "Go to the Crimson Isle and hunt Kada Knights in the caves leading to the Magma Chamber.";
}
public class InvisibugHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Invisibug (Hunting)";
    // Island key (2026-09-27 production log finding: SHARD_INVISIBUG shows up all over Moonglade,
    // not just the wetlands/Tangleburg zones this used to list) - the shard tag is the specific
    // evidence, so this is safe against false positives from unrelated Moonglade activity.
    protected override HashSet<string> Locations => ["Moonglade"];
    // Island keys are never zones themselves (see SkyblockZones.cs) - name a real zone so Where/
    // Island/WikiUrl/Warp still resolve (same fix as HelixForagingTask/FigForagingTask).
    protected override string Where => "Moonglade Marsh";
    protected override HashSet<string> DetectionItems => ["SHARD_INVISIBUG"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_INVISIBUG", 220)];
    protected override string HowTo => "Go to the marsh/wetland areas on Galatea and hunt Invisibugs. They are invisible until attacked, use AoE weapons.";
}
public class YogHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Yog (Hunting)";
    protected override HashSet<string> Locations => ["Magma Fields", "Khazad-dûm", "Crystal Hollows"];
    protected override HashSet<string> DetectionItems => ["SHARD_YOG"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_YOG", 250)];
    protected override string HowTo => "Go to Magma Fields or Blazing Volcano and hunt Yogs. They are lava mobs that drop valuable shards.";
}
public class FlareHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Flare (Hunting)";
    protected override HashSet<string> Locations => ["Blazing Volcano", "Burning Desert", "Crimson Isle"];
    protected override HashSet<string> DetectionItems => ["SHARD_FLARE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_FLARE", 200)];
    protected override string HowTo => "Go to Blazing Volcano or Burning Desert and hunt Flares. They are fire-based mobs on the Crimson Isle.";
}
public class BezalHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Bezal (Hunting)";
    protected override HashSet<string> Locations => ["Crimson Isle", "Stronghold"];
    protected override HashSet<string> DetectionItems => ["SHARD_BEZAL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BEZAL", 220)];
    protected override string HowTo => "Go to the Stronghold on the Crimson Isle and hunt Bezals there.";
}
public class GhostHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Ghost (Hunting)";
    // GHOST_COIN is not an item (Ghosts have a 0.01% chance to drop 1M raw coins); the sellable drop is the shard.
    protected override HashSet<string> Locations => ["Dwarven Mines", "The Mist"];
    protected override HashSet<string> DetectionItems => ["SHARD_GHOST"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_GHOST", 50)];
    protected override string HowTo => "Go to The Mist in the Dwarven Mines and hunt Ghosts. They drop Ghost Coins. Use a weapon with high damage and Magic Find gear.";
}
public class FlamingSpiderHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Flaming Spider (Hunting)";
    protected override HashSet<string> Locations => ["Crimson Isle", "Blazing Volcano", "Burning Desert"];
    protected override HashSet<string> DetectionItems => ["SHARD_FLAMING_SPIDER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_FLAMING_SPIDER", 200)];
    protected override string HowTo => "Go to Blazing Volcano or Crimson Isle and hunt Flaming Spiders. They are fire-type spider mobs.";
}
public class ObsidianDefenderHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Obsidian Defender (Hunting)";
    // Obsidian Defenders spawn in Dragon's Nest (The End).
    protected override HashSet<string> Locations => ["Dragon's Nest", "The End"];
    protected override HashSet<string> DetectionItems => ["SHARD_OBSIDIAN_DEFENDER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_OBSIDIAN_DEFENDER", 180)];
    protected override string HowTo => "Go to Dragon's Nest in The End and hunt Obsidian Defenders. They are tanky mobs that drop valuable shards.";
}
public class WitherSpecterHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Wither Specter (Hunting)";
    // Wither Spectre spawns in the Stronghold on the Crimson Isle, not The End.
    protected override HashSet<string> Locations => ["Crimson Isle", "Stronghold"];
    protected override HashSet<string> DetectionItems => ["SHARD_WITHER_SPECTER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_WITHER_SPECTER", 200)];
    protected override string HowTo => "Go to the Stronghold on the Crimson Isle and hunt Wither Spectres (and Wither Skeletons) there.";
}
public class ZealotHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Zealot (Hunting)";
    protected override HashSet<string> Locations => ["The End", "Dragon's Nest", "Void Sepulture"];
    protected override HashSet<string> DetectionItems => ["SHARD_ZEALOT", "SUMMONING_EYE"];
    // Same veto as ZealotsFdTask: its Summoning Eye is pricier than a Null Sphere, so without it a
    // Voidgloom fight at the Hideout would stay here.
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems =>
        new() { ["Zealot Bruiser Hideout"] = ["NULL_SPHERE"] };
    protected override List<MethodDrop> FormulaDrops => [new("SUMMONING_EYE", 3)];
    protected override string HowTo => "Go to The End and hunt Zealots for Summoning Eyes. Kill Special Zealots for guaranteed eye drops. Fast kill speed is key.";
}
public class BruiserHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Bruiser (Hunting)";
    protected override HashSet<string> Locations => ["The End", "Zealot Bruiser Hideout"];
    protected override HashSet<string> DetectionItems => ["SHARD_BRUISER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BRUISER", 200)];
    protected override string HowTo => "Go to The End and hunt Bruisers. They are tank-type mobs in the Dragon's Nest area.";
}
public class PestHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Pest (Hunting)";
    // Island key - real Garden zones are "The Garden" (with a per-visit pest count suffix) and
    // free-text "Plot - <player's name>" strings, both collapsed by SkyblockZones.Canonical (same
    // fix as PestTask/BaseGardenCropTask - see GardenTasks.cs).
    protected override HashSet<string> Locations => ["Garden"];
    // Island keys are never zones themselves (see SkyblockZones.cs) - name a real zone so Where/
    // Island/WikiUrl/Warp still resolve (same fix as PestTask/BaseGardenCropTask).
    protected override string Where => "The Garden";
    // PEST_KILL/PESTERMINATOR never occur in production (fabricated) - see PestEvidence for the
    // real pest-drop tags (vinyls + rare pest items), shared with PestTask.
    protected override HashSet<string> DetectionItems => PestEvidence.Items;
    protected override string HowTo => "Go to The Garden and hunt Pests that spawn on your plots. Use the Pesterminator vacuum or manual combat.";
    protected override string Category => "Garden";
}

// ── New/corrected hunting tasks (2026-09 production log analysis) ──

public class TikiHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Tiki (Hunting)";
    // not a weapon hunt: the base's weapon ladder, Looting and Combat level do not apply
    public override List<StatFactor> StatFactors => [new("skill:Hunting", 1, 50)];
    protected override List<RequiredItem> RequiredItems => [];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Hunting Fortune", Description = "Increases the amount of shards per capture", EstimatedMultiplier = 1.2 }
    ];
    // Island key - Tiki mobs are reported around Torrhus Heights specifically, but the shard tags
    // are the real evidence so the island key is safe against false positives elsewhere on Torrhus.
    protected override HashSet<string> Locations => ["Torrhus"];
    protected override string Where => "Torrhus Heights";
    protected override HashSet<string> DetectionItems => ["SHARD_SHRIEKY_TIKI", "SHARD_SNEAKY_TIKI", "SHARD_CHEEKY_TIKI"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SHRIEKY_TIKI", 18), new("SHARD_SNEAKY_TIKI", 18), new("SHARD_CHEEKY_TIKI", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Canyon";
    protected override string HowTo =>
        "Go to Torrhus Heights on Torrhus and find the Tiki mobs. Each one requires solving a puzzle "
        + "before it can be captured with a Pocket Black Hole.";
}

public class TreasureHoarderHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Treasure Hoarder (Hunting)";
    protected override HashSet<string> Locations => ["Upper Mines", "Rampart's Quarry", "Dwarven Mines"];
    protected override HashSet<string> DetectionItems => ["SHARD_TREASURE_HOARDER", "STARFALL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_TREASURE_HOARDER", 24), new("STARFALL", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Treasure_Hoarder";
    protected override string HowTo =>
        "Go to the Upper Mines in the Dwarven Mines and hunt Treasure Hoarders - hostile mobs that "
        + "drop 1-2 Starfall each; capture one with a Pocket Black Hole for the shard.";
}

public class GlaciteWalkerHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Glacite Walker (Hunting)";
    protected override HashSet<string> Locations => ["Great Ice Wall", "Divan's Gateway"];
    protected override HashSet<string> DetectionItems => ["SHARD_GLACITE_WALKER", "GLACITE_JEWEL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_GLACITE_WALKER", 156)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Great_Ice_Wall";
    protected override string HowTo =>
        "Go to the Great Ice Wall in the Dwarven Mines and hunt Glacite Walkers - they spawn throughout "
        + "the area, drop Glacite armor and Glacite Jewels, and give their shard when captured.";
}

public class MinerZombieHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Miner Zombie (Hunting)";
    // Miner Zombie [Lv20] is in the Obsidian Sanctuary, the last floor of the Deep Caverns - not to
    // be confused with ObsidianMiningTask, which shares this zone but keys off OBSIDIAN drops.
    protected override HashSet<string> Locations => ["Obsidian Sanctuary"];
    protected override HashSet<string> DetectionItems =>
        ["SHARD_MINER_ZOMBIE", "TANK_MINER_HELMET", "TANK_MINER_CHESTPLATE", "TANK_MINER_LEGGINGS", "TANK_MINER_BOOTS"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MINER_ZOMBIE", 486)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Miner_Zombie";
    protected override string HowTo => "Go to the Obsidian Sanctuary (last floor of the Deep Caverns) and hunt Miner Zombies for their shard and Tank Miner armor.";
}

public class MudwormHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Mudworm (Hunting)";
    // not a weapon hunt: the base's weapon ladder, Looting and Combat level do not apply
    public override List<StatFactor> StatFactors => [new("skill:Hunting", 1, 50)];
    protected override List<RequiredItem> RequiredItems => [];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Hunting Fortune", Description = "Increases the amount of shards per capture", EstimatedMultiplier = 1.2 }
    ];
    // Island key - shard tag is the specific evidence, see the Galatea Moonglade shard task comment above HideonleafTask.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "West Reaches";
    protected override HashSet<string> DetectionItems => ["SHARD_MUDWORM"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MUDWORM", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Moonglade_Marsh";
    protected override string HowTo => "Go to Moonglade and catch Mudworms with a Huntrap.";
}

public class BirriesHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Birries (Hunting)";
    // not a weapon hunt: the base's weapon ladder, Looting and Combat level do not apply
    public override List<StatFactor> StatFactors => [new("skill:Hunting", 1, 50)];
    protected override List<RequiredItem> RequiredItems => [];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Hunting Fortune", Description = "Increases the amount of shards per capture", EstimatedMultiplier = 1.2 }
    ];
    // Island key, same as MudwormHuntingTask.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "North Reaches";
    // Birries spawn with a 10% chance when harvesting Lushlilac berry bushes, so Lushlilac itself is
    // also evidence of this activity, not just its own shard.
    protected override HashSet<string> DetectionItems => ["SHARD_BIRRIES", "LUSHLILAC"];
    protected override List<MethodDrop> FormulaDrops => [new("LUSHLILAC", 120), new("SHARD_BIRRIES", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Moonglade_Marsh";
    protected override string HowTo => "Go to Moonglade and harvest Lushlilac berry bushes - Birries spawn with a 10% chance per harvest, catch them with a Huntrap.";
}

/// <summary>
/// Torrhus Canyon minigame started at the Safari Manager - distinct from every other hunting task
/// here: critters are caught with Critter Capsules, not fought/captured with a weapon or trap, so
/// this does not extend <see cref="BaseHuntingTask"/> (its RequiredItems/StatFactors describe a
/// weapon ladder that has nothing to do with this). No verified runs/hour rate exists, so
/// FormulaDrops uses the raw production medians per run as-is rather than inventing an hourly rate.
/// </summary>
public class CritterSafariTask : MethodTask
{
    protected override string MethodName => "Critter Safari";
    protected override string Category => "Hunting";
    protected override string ActionUnit => "runs";
    protected override HashSet<string> Locations => ["Critter Safari"];
    protected override bool RequireShardItems => true;
    // Location-only (RequireShardItems is the real evidence) - the zone is a dedicated minigame
    // instance, so even a single shard is unambiguous.
    protected override int MinLocationOnlyItems => 1;
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("SHARD_FOXTROT", 3), new("SHARD_BLUEBIRD", 3), new("SHARD_WOODCHUCKER", 2),
        new("SHARD_PARAKEET", 2), new("SHARD_TREEFROG", 2)
    ];
    // No RequiredItems: the Safari Ticket/Critter Capsule item tag ids are not verified anywhere in
    // this codebase, so they are named in the HowTo text below instead of guessed here.
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Critter_Safari";
    protected override string HowTo =>
        "Go to the Safari Manager in Torrhus Canyon (needs Hunting 15 and a Safari Ticket from Miria's "
        + "Contests) and catch critters with Critter Capsules during a run.";
}

// ── Torrhus shards (production 2026-10-02/03, per-hour medians) ──

public class DungBeetleHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Dung Beetle (Hunting)";
    protected override HashSet<string> Locations => ["Torrhus Canyon", "Torrhus Heights", "Spring Path", "Torrhus Springs"];
    protected override HashSet<string> DetectionItems => ["SHARD_DUNG_BEETLE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_DUNG_BEETLE", 24)];
    protected override string HowTo => "Go to Torrhus Canyon or Torrhus Heights on Torrhus and hunt Dung Beetles for their shard.";
}
public class HideonsunHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Hideonsun (Hunting)";
    protected override HashSet<string> Locations => ["Torrhus Canyon", "Torrhus Heights", "Spring Path"];
    protected override HashSet<string> DetectionItems => ["SHARD_HIDEONSUN"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_HIDEONSUN", 34)];
    protected override string HowTo => "Go to Torrhus Canyon or Torrhus Heights on Torrhus and hunt Hideonsuns for their shard.";
}
