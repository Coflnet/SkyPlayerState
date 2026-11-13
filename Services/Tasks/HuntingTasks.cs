using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Joins the numbered how-to sentences of a task; the default step builder splits them again at the full stops.</summary>
internal static class HuntingText
{
    public static string Steps(params string[] sentences) => string.Join(" ", sentences);
}

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
    // hunting on Galatea fills contests on the side - rewards on the island's own tasks only
    protected override LateRewardSpec LateReward => GalateaContestRewards.ForLocations(Locations);
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Hunting Fortune", Description = "Increases the number of shards per capture", EstimatedMultiplier = 1.3 },
        new() { Name = "Charm Chance", Description = "Increases the chance to obtain a shard when killing a charmable mob", EstimatedMultiplier = 1.2 },
        new() { Name = "Combat Level", Description = "Higher combat level increases kill speed", EstimatedMultiplier = 1.1 }
    ];
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "VENATOR_GENESIS", Reason = "Huntaxe to weaken the mob" },
        new() { ItemTag = "SMALL_POCKET_BLACK_HOLE", Reason = "Capture the weakened mob" }
    ];
}

// ── Hunting methods (dedicated mob hunting, non-fishing) ──

public class RainSlimeHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Rain_Slime";
    protected override string MethodName => "Rain Slime (Hunting)";
    protected override HashSet<string> Locations => ["Spider's Den", "The Spider's Den"];
    protected override HashSet<string> DetectionItems => ["SHARD_RAIN_SLIME"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_RAIN_SLIME", 200)];
    /// <summary>
    /// Rain Slimes spawn during weather from :35 to :55 each hour
    /// </summary>
    protected override string CheckAccessibility(TaskParams parameters)
    {
        var minute = parameters.TestTime.Minute;
        if (minute < 35 || minute >= 55)
            return $"Rain Slimes spawn during weather from :35 to :55 each hour. Available again in {(35 - minute + 60) % 60} minutes.";
        return base.CheckAccessibility(parameters);
    }

    protected override DateTime? GetNextAvailableAt(TaskParams parameters)
    {
        var minute = parameters.TestTime.Minute;
        if (minute >= 35 && minute < 55)
            return null;
        return new DateTime(parameters.TestTime.Year, parameters.TestTime.Month, parameters.TestTime.Day,
            parameters.TestTime.Hour, 35, 0, parameters.TestTime.Kind).AddHours(minute >= 55 ? 1 : 0);
    }
    protected override string HowTo => HuntingText.Steps(
        "Go to the Spider's Den while it is raining.",
        "Rain Slimes spawn at the base of the hill with Grandma's House and between Michael's Crib and the Flint Skeleton hill.",
        "Capture them with a Pocket Black Hole.");
    public override string Description =>
        "Capture Rain Slimes in the Spider's Den during rain or a thunderstorm for their shard. In a thunderstorm some of them are Toxic Rain Slimes, which can also drop a Poison Sample. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class HellwispHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Hellwisp";
    protected override string MethodName => "Hellwisp (Hunting)";
    protected override HashSet<string> Locations => ["Magma Chamber", "Matriarch's Lair"];
    protected override HashSet<string> DetectionItems => ["SHARD_HELLWISP"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_HELLWISP", 200)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the lava pools northwest of the Magma Chamber, outside the Matriarch's Lair.",
        "Stay out of tentacle range; a caught player gets pulled into the mouth.",
        "Bring a Hellwisp to low health and capture it with a Pocket Black Hole.",
        "Leave a Huntrap in the Matriarch's Lair for passive shards.");
    public override string Description =>
        "Capture Hellwisps, packs of upside-down Ghasts in the lava pools next to the Matriarch's Lair on the Crimson Isle, for their legendary shard. Two of them sit in the pools northwest of the Magma Chamber, and they pull you in with their tentacles. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class XyzHuntingTask : CritterHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Exe";
    protected override string MethodName => "Xyz (Hunting)";
    // SHARD_XYZ drops from the Exe mob on the Crimson Isle (Mystic Marsh), not Crystal Hollows.
    protected override HashSet<string> Locations => ["Crimson Isle", "Mystic Marsh"];
    protected override HashSet<string> DetectionItems => ["SHARD_XYZ"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_XYZ", 250)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Mystic Marsh near the Mage Outpost.",
        "Throw your Lasso at an Exe, Wai or Zee and reel it in.",
        "Leave a Huntrap in the marsh for passive shards.");
    public override string Description =>
        "Lasso the Exes, Wais and Zees floating in the Mystic Marsh on the Crimson Isle for the XYZ shard. They cannot be damaged, so a Lasso or a Salt is the only way to get the shard. Huntraps in the Mystic Marsh also catch it.";
}
public class KadaKnightHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Kada_Knight";
    protected override string MethodName => "Kada Knight (Hunting)";
    // Kada Knight spawns in caves near the Magma Chamber on the Crimson Isle, not the Galatea underwater zones.
    protected override HashSet<string> Locations => ["Crimson Isle", "Magma Chamber"];
    protected override HashSet<string> DetectionItems => ["SHARD_KADA_KNIGHT"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_KADA_KNIGHT", 230)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the north or south cave leading into the Magma Chamber.",
        "Bring a Kada Knight to low health with a Huntaxe.",
        "Capture it with a Pocket Black Hole and sell the shards and Kada Leads.");
    public override string Description =>
        "Capture Kada Knights in the caves that lead to the Magma Chamber on the Crimson Isle for their shard. They also drop Kada Leads. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class InvisibugHuntingTask : CritterHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Invisibug";
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
    protected override string HowTo => HuntingText.Steps(
        "Go to Moonglade and look for small groups of crit particles in the air.",
        "Throw your Lasso at the particles and reel it in.",
        "Leave a Huntrap on land for passive shards.");
    public override string Description =>
        "Invisibugs are nearly invisible critters, seen only as a few crit particles, above the ground and trees of Moonglade. Lasso them for their shard. They are also found as floor drops on Moonglade, and Huntraps on land on Galatea catch them.";
}
public class YogHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Yog";
    protected override string MethodName => "Yog (Hunting)";
    protected override HashSet<string> Locations => ["Magma Fields", "Khazad-dûm", "Crystal Hollows"];
    protected override HashSet<string> DetectionItems => ["SHARD_YOG"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_YOG", 250)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Magma Fields in the Crystal Hollows.",
        "Bring a Yog to low health and capture it with a Pocket Black Hole.",
        "Step away from any Yog you kill before it explodes.");
    public override string Description =>
        "Capture Yogs in the Magma Fields and Khazad-dûm of the Crystal Hollows for their shard. Yogs hop at you with explosions and blow up about four seconds after dying; they drop Rough Topaz and sometimes a Yoggie. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class FlareHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Flare";
    protected override string MethodName => "Flare (Hunting)";
    protected override HashSet<string> Locations => ["Blazing Volcano", "Burning Desert", "Crimson Isle"];
    // Flares only stand on the 12 stone pillars around the Magma Chamber platform
    protected override string Where => "Magma Chamber";
    protected override HashSet<string> DetectionItems => ["SHARD_FLARE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_FLARE", 200)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Magma Chamber on the Crimson Isle.",
        "Flares stand on the 12 stone pillars around the platform and respawn about every 10 seconds.",
        "Attack from a distance, bring one to low health with a Huntaxe, and capture it with a Pocket Black Hole.");
    public override string Description =>
        "Capture Flares in the Magma Chamber on the Crimson Isle for their shard. A Flare is three blazes stacked on a stone pillar with one shared health pool; they never move, but their fireballs hit hard. They also drop Blaze Rods and sometimes a Gazing Pearl.";
}
public class BezalHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Bezal";
    protected override string MethodName => "Bezal (Hunting)";
    protected override HashSet<string> Locations => ["Crimson Isle", "Stronghold"];
    protected override HashSet<string> DetectionItems => ["SHARD_BEZAL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BEZAL", 220)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Stronghold on the Crimson Isle.",
        "Bezals fly and shoot fireballs; a Moody Grappleshot stops them flying.",
        "Bring one to low health with your Huntaxe and place a Pocket Black Hole to capture it.",
        "A dying Bezal damages you and sets you on fire, so keep your health up.");
    public override string Description =>
        "Capture Bezals, the blazes that float in the rooms of the Stronghold on the Crimson Isle, for their shard. They also drop Blaze Rods and sometimes Bezos. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class GhostHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Ghost";
    protected override string MethodName => "Ghost (Hunting)";
    // GHOST_COIN is not an item (Ghosts have a 0.01% chance to drop 1M raw coins); the sellable drop is the shard.
    protected override HashSet<string> Locations => ["Dwarven Mines", "The Mist"];
    protected override HashSet<string> DetectionItems => ["SHARD_GHOST"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_GHOST", 50)];
    protected override string HowTo => HuntingText.Steps(
        "Go to The Mist in the Dwarven Mines.",
        "Keep your distance: their damage doubles within one block.",
        "Bring one to low health and capture it with a Pocket Black Hole.",
        "Leave a Huntrap in The Mist for passive shards.");
    public override string Description =>
        "Capture Ghosts in The Mist in the Dwarven Mines for their shard. Ghosts are invisible charged creepers with a strong area attack, and they rarely drop Volta, Sorrow or Plasma. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class FlamingSpiderHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Flaming_Spider";
    protected override string MethodName => "Flaming Spider (Hunting)";
    protected override HashSet<string> Locations => ["Crimson Isle", "Blazing Volcano", "Burning Desert"];
    protected override HashSet<string> DetectionItems => ["SHARD_FLAMING_SPIDER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_FLAMING_SPIDER", 200)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Burning Desert on the Crimson Isle.",
        "Bring a Flaming Spider to low health with a Huntaxe.",
        "Place a Pocket Black Hole to capture it.");
    public override string Description =>
        "Capture Flaming Spiders in the Burning Desert on the Crimson Isle for their shard. They also drop String and sometimes a Burning Eye. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class ObsidianDefenderHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Obsidian_Defender";
    protected override string MethodName => "Obsidian Defender (Hunting)";
    // Obsidian Defenders spawn in Dragon's Nest (The End).
    protected override HashSet<string> Locations => ["Dragon's Nest", "The End"];
    protected override HashSet<string> DetectionItems => ["SHARD_OBSIDIAN_DEFENDER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_OBSIDIAN_DEFENDER", 180)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Dragon's Nest in The End.",
        "Bring an Obsidian Defender to low health.",
        "Capture it with a Pocket Black Hole.");
    public override string Description =>
        "Capture Obsidian Defenders in the Dragon's Nest in The End for their shard. They are fast Wither Skeletons in obsidian armour that inflict Wither and ignore knockback unless your weapon has Knockback. They also drop Obsidian. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class WitherSpecterHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Wither_Spectre";
    protected override string MethodName => "Wither Specter (Hunting)";
    // Wither Spectre spawns in the Stronghold on the Crimson Isle, not The End.
    protected override HashSet<string> Locations => ["Crimson Isle", "Stronghold"];
    protected override HashSet<string> DetectionItems => ["SHARD_WITHER_SPECTER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_WITHER_SPECTER", 200)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the left side of the Stronghold on the Crimson Isle.",
        "Bring a Wither Spectre to low health.",
        "Capture it with a Pocket Black Hole.");
    public override string Description =>
        "Capture Wither Spectres on the left side of the Stronghold on the Crimson Isle for their shard. They run at you and inflict Wither but are easy to defeat; they also drop Gunpowder and Spectre Dust. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class ZealotHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Zealot";
    protected override string MethodName => "Zealot (Hunting)";
    protected override HashSet<string> Locations => ["The End", "Dragon's Nest", "Void Sepulture"];
    protected override HashSet<string> DetectionItems => ["SHARD_ZEALOT", "SHARD_SPECIAL_ZEALOT"];
    protected override HashSet<string> EvidenceItems => ["SUMMONING_EYE"];
    // Same veto as ZealotsFdTask: its Summoning Eye is pricier than a Null Sphere, so without it a
    // Voidgloom fight at the Hideout would stay here.
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems =>
        new() { ["Zealot Bruiser Hideout"] = ["NULL_SPHERE"] };
    protected override List<MethodDrop> FormulaDrops => [new("SUMMONING_EYE", 3)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Dragon's Nest in The End.",
        "Zealots are neutral until hit, so pick them off one at a time.",
        "Capture them with a Pocket Black Hole and keep an eye out for Special Zealots.");
    public override string Description =>
        "Capture Zealots in the Dragon's Nest in The End for their shard. Waves of 16 spawn every 11 seconds, and killing a Zealot sometimes spawns a Special Zealot, which drops a Summoning Eye. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class BruiserHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Zealot_Bruiser";
    protected override string MethodName => "Bruiser (Hunting)";
    protected override HashSet<string> Locations => ["The End", "Zealot Bruiser Hideout"];
    protected override HashSet<string> DetectionItems => ["SHARD_BRUISER"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_BRUISER", 200)];
    protected override string HowTo => HuntingText.Steps(
        "Go to the Zealot Bruiser Hideout in The End.",
        "Bruisers stay neutral until you hit them or look at their head.",
        "Bring one to low health with a Huntaxe and capture it with a Pocket Black Hole.",
        "Special Zealots that spawn here drop Summoning Eyes.");
    public override string Description =>
        "Capture Zealot Bruisers in the Zealot Bruiser Hideout in The End for their shard. They are tougher Zealots that only live in the hideout and spawn Special Zealots more often. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}
public class PestHuntingTask : BaseHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Pests";
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
    protected override HashSet<string> DetectionItems => [.. PestEvidence.Items, .. PestEvidence.Shards];
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => new() { ["Garden"] = PestEvidence.Crops };
    protected override string HowTo => "Go to The Garden and hunt Pests that spawn on your plots. Use a Vacuum to kill them; ordinary weapons cannot damage Pests.";
    protected override List<RequiredItem> RequiredItems =>
        [new() { ItemTag = "SKYMART_VACUUM", Reason = "A Vacuum is required to damage Pests" }];
    protected override string Category => "Garden";
}

/// <summary>
/// Lunar Moths spawn only in Pest/Mouse/Vermin Traps baited with Moondew. Production 2026-10-11: 85 Sunset books in 18
/// periods were unclassified and the spent Moondew sat in crop and pest tasks.
/// </summary>
public class LunarMothTrappingTask : BaseHuntingTask
{
    protected override string MethodName => "Lunar Moth Trapping";
    protected override HashSet<string> Locations => ["Garden"];
    protected override string Where => "The Garden";
    protected override HashSet<string> DetectionItems => ["ENCHANTMENT_ULTIMATE_SUNSET_1", "SHARD_LUNAR_MOTH"];
    // Spent bait is a negative count: it classifies a period whose loot was booked elsewhere. Crafted Moondew alone is not loot.
    protected override HashSet<string> EvidenceItems => ["MOONDEW"];
    protected override List<MethodDrop> FormulaCosts => [new("MOONDEW", 60)];
    // Sunset I is 35% of 60 baits; the shards per moth are a rough seed.
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTMENT_ULTIMATE_SUNSET_1", 21), new("SHARD_LUNAR_MOTH", 60)];
    // Trapping happens next to farming, and a period of 6,000+/h of one enchanted crop (production: ~13k enchanted crops/h) or 30,000+/h
    // of a raw one is a farming period that merely holds a stray moth drop or the spent bait; a baiting period holds far fewer.
    internal static readonly BulkItemRule CropBulk =
        new(PestEvidence.Crops.ToDictionary(crop => crop, crop => IsCompactedCrop(crop) ? 6000 : 30000));
    private static bool IsCompactedCrop(string crop) =>
        crop.StartsWith("ENCHANTED_") || crop.StartsWith("COMPACTED_") || crop.StartsWith("POLISHED_")
        || crop.StartsWith("MUTANT_") || crop.StartsWith("TIGHTLY_");
    protected override BulkItemRule BulkYield => CropBulk;
    protected override int Priority => 1;
    protected override string Category => "Garden";
    protected override string ActionUnit => "baits";
    protected override List<RequiredItem> RequiredItems => [];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Lunar_Moth";
    protected override string HowTo =>
        "Bait Pest, Mouse or Vermin Traps in the Garden with Moondew; every bait spawns a Lunar Moth that drops Sunset I "
        + "35% of the time. Lasso it for Lunar Moth shards.";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Place a Pest, Mouse or Vermin Trap in your Garden and bait it with Moondew." },
        new() { Number = 2, Text = "Every bait spawns a Lunar Moth that drops Sunset I 35% of the time; lasso it for Lunar Moth shards.", OnClick = WikiUrl },
        new() { Number = 3, Text = "You're doing it right when you collect: Sunset I books, Lunar Moth shards. That's what we track for your coins/hour." },
        new() { Number = 4, Text = "Check /cofl task again to see your real coins/hour." },
    ];
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
    protected override string HowTo => HuntingText.Steps(
        "Warp to Torrhus and find a Tiki totem in Torrhus Canyon or Torrhus Heights.",
        "Left-click a head to turn it and the one above clockwise, right-click to turn them the other way.",
        "When all three heads face the same direction the Tiki wakes up and attacks.",
        "Capture it with a Pocket Black Hole.");
    public override string Description =>
        "Wake the Tiki totems in Torrhus Canyon and Torrhus Heights and capture them for the Shrieky, Sneaky and Cheeky Tiki shards. Each totem is a puzzle of three heads; once they all face the same way the Tiki wakes up and chases you. Captured Tikis also drop Helix Logs.";
}

public class TreasureHoarderHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Treasure Hoarder (Hunting)";
    protected override HashSet<string> Locations => ["Upper Mines", "Rampart's Quarry", "Dwarven Mines"];
    protected override HashSet<string> DetectionItems => ["SHARD_TREASURE_HOARDER", "STARFALL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_TREASURE_HOARDER", 24), new("STARFALL", 18)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Treasure_Hoarder";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Upper Mines in the Dwarven Mines.",
        "Bring a Treasure Hoarder to low health; it gets its pickaxe back 12 seconds after throwing it.",
        "Capture it with a Pocket Black Hole and sell the shards and Starfall.");
    public override string Description =>
        "Capture Treasure Hoarders in the Upper Mines of the Dwarven Mines for their shard. They drop one or two Starfall each, and rarely a Kaleidoscopic Mineral or Treasurite. They throw their pickaxes at you. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}

public class GlaciteWalkerHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Glacite Walker (Hunting)";
    protected override HashSet<string> Locations => ["Great Ice Wall", "Divan's Gateway"];
    protected override HashSet<string> DetectionItems => ["SHARD_GLACITE_WALKER", "GLACITE_JEWEL"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_GLACITE_WALKER", 156)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Great_Ice_Wall";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Great Ice Wall in the Dwarven Mines.",
        "Pull a group and bring one to low health.",
        "Capture it with a Pocket Black Hole and sell the shards and any Glacite Jewels.");
    public override string Description =>
        "Capture Glacite Walkers at the Great Ice Wall in the Dwarven Mines for their shard. Sixteen spawn every 11 seconds, and they rarely drop Glacite Jewels and Glacite armour. They take more damage the higher your tool's breaking power. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}

public class MinerZombieHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Miner Zombie (Hunting)";
    // Miner Zombie [Lv20] is in the Obsidian Sanctuary, the last floor of the Deep Caverns - not to
    // be confused with ObsidianMiningTask, which shares this zone but keys off OBSIDIAN drops.
    // weaker Miner Zombies also spawn in the Diamond Reserve (wiki)
    protected override HashSet<string> Locations => ["Obsidian Sanctuary", "Diamond Reserve"];
    protected override string Where => "Obsidian Sanctuary";
    protected override HashSet<string> DetectionItems =>
        ["SHARD_MINER_ZOMBIE", "TANK_MINER_HELMET", "TANK_MINER_CHESTPLATE", "TANK_MINER_LEGGINGS", "TANK_MINER_BOOTS"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_MINER_ZOMBIE", 486)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Miner_Zombie";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Obsidian Sanctuary (or the Diamond Reserve) in the Deep Caverns.",
        "Bring a Miner Zombie to low health; fire does not hurt it.",
        "Capture it with a Pocket Black Hole.");
    public override string Description =>
        "Capture Miner Zombies in the Deep Caverns for their shard: weaker ones live in the Diamond Reserve, stronger ones in the Obsidian Sanctuary. They are fireproof and sometimes drop Miner armour. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
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
    protected override string HowTo => HuntingText.Steps(
        "Go to Moonglade and pick up the floor drops you find.",
        "Place a Huntrap on land and come back after half a day.");
    public override string Description =>
        "Mudworm shards are floor drops on Moonglade, and Huntraps placed on land on Galatea catch them. Pick them up while foraging or hunting there, or leave traps out.";
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
    protected override string HowTo => HuntingText.Steps(
        "Go to Moonglade and harvest Lushlilac bushes, or collect Sea Lumies underwater.",
        "Watch for a Birries; it hops around on land or swims in water.",
        "Catch it with a Lasso or a Fishing Net and keep gathering.");
    public override string Description =>
        "Birries have a 10% chance to appear when you collect Lushlilac or Sea Lumies on Moonglade. Catch them with a Lasso or a Fishing Net for their shard while you gather.";
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
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => new() { ["Critter Safari"] = [PseudoItems.SAFARI_LOOT_SHARE] };
    // The four biome tasks (CritterSafariBiomeTask) own every period holding one of their shards; what is left (floor
    // drops, shards of no listed critter) stays here. Location-only on purpose: the zone is a dedicated minigame
    // instance, so even a single item is unambiguous and nothing else claims it.
    protected override bool Fallback => true;
    protected override int MinLocationOnlyItems => 1;
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("SHARD_FOXTROT", 3), new("SHARD_BLUEBIRD", 3), new("SHARD_WOODCHUCKER", 2),
        new("SHARD_PARAKEET", 2), new("SHARD_TREEFROG", 2)
    ];
    // No RequiredItems: the Safari Ticket/Critter Capsule item tag ids are not verified anywhere in
    // this codebase, so they are named in the HowTo text below instead of guessed here.
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Critter_Safari";
    protected override string HowTo => HuntingText.Steps(
        "Win a Safari Ticket in Miria's contest; better brackets give 40-56 capsules instead of 32.",
        "Talk to the Safari Manager in Torrhus Canyon to enter.",
        "Spend capsules on the most valuable critters: legendaries are caught 5% of the time, but the 20th throw is guaranteed.",
        "Pick up floor drops for Safari Essence, Hunting XP, shards or quest items.");
    public override string Description =>
        "A ticketed hunting minigame entered through the Safari Manager in Torrhus Canyon. You throw Critter Capsules at critters in four biomes (Cavern, Forest, Haunted, Icy) and keep every shard you catch; rarer critters are harder to catch. You need Hunting 15 and a Safari Ticket from Miria's contests.";
}

// ── Torrhus shards (production 2026-10-02/03, per-hour medians) ──

public class DungBeetleHuntingTask : CritterHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Dung_Beetle_Shard";
    protected override string MethodName => "Dung Beetle (Hunting)";
    protected override HashSet<string> Locations => ["Torrhus Canyon", "Torrhus Heights", "Spring Path", "Torrhus Springs"];
    protected override HashSet<string> DetectionItems => ["SHARD_DUNG_BEETLE"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_DUNG_BEETLE", 24)];
    protected override string HowTo => HuntingText.Steps(
        "Warp to Torrhus Canyon.",
        "Pick up the floor drops you see on the ground.",
        "Place a Huntrap on land in Torrhus Canyon or on the Spring Path and come back after half a day.");
    public override string Description =>
        "Dung Beetle shards are found on Torrhus: they lie on the ground in Torrhus Canyon as floor drops, and Huntraps placed in Torrhus Canyon or on the Spring Path catch them. Pick them up while you do other things on Torrhus, or leave traps out.";
}
public class HideonsunHuntingTask : CritterHuntingTask
{
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Hideonsun";
    protected override string MethodName => "Hideonsun (Hunting)";
    protected override HashSet<string> Locations => ["Torrhus Canyon", "Torrhus Heights", "Spring Path"];
    protected override HashSet<string> DetectionItems => ["SHARD_HIDEONSUN"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_HIDEONSUN", 34)];
    protected override string HowTo => HuntingText.Steps(
        "Warp to Torrhus Canyon and look for Hideonsuns.",
        "Stand on one; it starts shooting bullets at you.",
        "Hit the bullet back three times to tire it out and collect the shard.");
    public override string Description =>
        "Hideonsuns hide around Torrhus Canyon and fight back when you stand on them. Hit their bullet back three times and they tire out, dropping their shard and Helix Logs. No tool is needed.";
}
public class TorrhusCanyonHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Torrhus Canyon (Hunting)";
    // Listed zone by zone: the island key "Torrhus" would also cover the Critter Safari, which has its own task.
    protected override HashSet<string> Locations => ["Torrhus Canyon", "Torrhus Heights", "Pangolin Hideaway", "Spring Path", "Critter Safari Entrance"];
    protected override string Where => "Torrhus Canyon";
    protected override HashSet<string> DetectionItems =>
        ["SHARD_BLUE_JAY", "SHARD_PANGOLIN", "SHARD_BUNBUN", "SHARD_MOUNTAIN_GOAT", "SHARD_TIMIL"];
    // Foragers lasso these critters on the way: a few shards outvalue the logs or honeycomb of a short period and took
    // 108 Helix Foraging and Honeycomb Gathering periods in the replay (2026-10-11). Only periods without another task
    // are the hunt.
    protected override bool Fallback => true;
    // Seeds from the per-period medians (3 shards, Timil 15) of 13-42 unclassified periods each, 2026-10-11.
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("SHARD_BUNBUN", 25), new("SHARD_BLUE_JAY", 18), new("SHARD_PANGOLIN", 15), new("SHARD_MOUNTAIN_GOAT", 15), new("SHARD_TIMIL", 30)
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Canyon";
    protected override string HowTo => HuntingText.Steps(
        "Warp to Torrhus and pick a critter.",
        "Blue Jay: a warning sign appears when you get within about a dozen blocks, so stand completely still; after about 10 seconds the sign clears and you can throw, but if anyone nearby moves it flies to another tree.",
        "Pangolin: it curls into a ball when you get close and in sight, so lasso it from further away.",
        "Mountain Goat: it charges you with heavy knockback once lassoed.",
        "Timil: net them in the water, often in groups.");
    public override string Description =>
        "Lasso the critters of Torrhus: Blue Jays on the trees of Torrhus Heights, Pangolins in the Pangolin Hideaway, Bunbuns across the canyon and Mountain Goats on the Spring Path; Timils swim in the water and need a Fishing Net. Most of them also drop Helix Logs.";
}
