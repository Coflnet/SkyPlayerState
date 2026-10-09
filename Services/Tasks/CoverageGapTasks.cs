using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Catch-all tasks for frequent activity that stayed unclassified (production 2026-10, two samples
// of ~110k periods, 6.8k unclassified) ──
// Design rule for everything in this file: a new task must only ever classify periods no other task
// claims, so every task here sets Fallback - the classifier ranks a fallback task's candidates below
// every regular task's (see MethodTask.Fallback), whatever the item values, because production
// classifies with real prices and compares the matched value before Priority. Each task is also
// verified with a before/after run over real production periods (no period may move from one task to
// another) and pinned in TaskClassifier.CoverageGaps.Tests.cs.

/// <summary>
/// Mob grinding on the Crimson Isle outside every dedicated task (Stronghold, Dragontail, Burning
/// Desert, The Dukedom, Crimson Fields, The Wasteland, Magma Chamber, Smoldering Tomb, Mystic Marsh,
/// ...): rotten flesh, string, magma cream, blaze rods, ghast tears, Kada leads, Rampart armour, ...
/// Location-only on purpose - the mobs drop dozens of generic items that other tasks (slayers,
/// fishing, Mycelium) also list, so item detection would fight them.
/// </summary>
public class CrimsonIsleMobsTask : MethodTask
{
    // location-only on a whole island: as a public task its personal view (FindMatchingPeriods) would
    // show every Crimson Isle period of the player - fishing, slayers, Kuudra claims - as "mob" profit
    protected override bool Hidden => true;
    protected override string MethodName => "Crimson Isle Mobs";
    protected override string Category => "Passive";
    protected override TaskType TaskType => TaskType.Passive;
    protected override string ActionUnit => "kills";
    // island key: every Crimson Isle zone resolves to it (the zone map), "Crimson Isle" itself included
    protected override HashSet<string> Locations => ["Crimson Isle"];
    protected override string Where => "Stronghold";
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Crimson_Isle";
    protected override string HowTo =>
        "Go to the Crimson Isle and kill the mobs around the Stronghold, Dragontail, Burning Desert, The Dukedom or the Wasteland. "
        + "Anything not covered by a more specific task (slayers, fishing, shard hunting) is tracked here.";
    public override string Description =>
        "General mob grinding on the Crimson Isle (blazes, magma cubes, pigmen, wither skeletons, ...) that no more specific task covers.";
}

/// <summary>
/// Hidden sink for Galatea contest rewards (Agatha's/Miria's coupons and the Miria/Starlyn prizes) that no
/// earning activity could be identified for. A contest cannot be done on its own - it is won while
/// foraging, hunting or fishing, and those tasks declare the rewards as <see cref="MethodTask.LateReward"/>
/// and receive them when they follow their work (see <see cref="LateRewardAttribution"/>). What is left
/// lands here, counted for the full 20 minute contest instead of the seconds the hand-in takes, and is never
/// shown as a task.
/// </summary>
public class GalateaContestsTask : MethodTask
{
    protected override bool Hidden => true;
    protected override TimeSpan MinimumPeriodDuration => GalateaContestRewards.ContestDuration;
    protected override string MethodName => "Galatea Contests";
    protected override string Category => "Event";
    protected override string ActionUnit => "contests";
    protected override HashSet<string> Locations => ["Galatea"];
    protected override string Where => "Murkwater Loch";
    protected override HashSet<string> DetectionItems => ["AGATHA_COUPON", "MIRIA_COUPON", "MIRIA_PRIZE", "STARLYN_PRIZE"];
    // a contest period is usually also foraging/farming - that task keeps it
    protected override bool Fallback => true;
    protected override string HowTo =>
        "Take part in the Galatea contests: hand in your harvest to Agatha at Murkwater Loch, or to Miria on Torrhus, and collect the coupons and prizes.";
    public override string Description =>
        "Rewards of the Galatea contests (Agatha's and Miria's coupons, Miria and Starlyn prizes).";
}

/// <summary>
/// Glacite Walker armour drops on the Great Ice Wall. The shard/Glacite Jewel task
/// ("Glacite Walker (Hunting)") keeps every period that has one of those; this one only picks up the
/// periods holding nothing but the armour pieces.
/// </summary>
public class GlaciteWalkerTask : MethodTask
{
    protected override string MethodName => "Glacite Walker";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Great Ice Wall", "Divan's Gateway"];
    protected override HashSet<string> DetectionItems => ["GLACITE_HELMET", "GLACITE_CHESTPLATE", "GLACITE_LEGGINGS", "GLACITE_BOOTS"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Great_Ice_Wall";
    protected override string HowTo =>
        "Go to the Great Ice Wall in the Dwarven Mines and kill Glacite Walkers - they drop Glacite armor pieces.";
    public override string Description =>
        "Killing Glacite Walkers on the Great Ice Wall for their Glacite armor pieces.";
}

/// <summary>Chill shard on Moonglade (Wyrmgrove Tomb) - same pattern as the other "(Hunting)" shard tasks.</summary>
public class ChillHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Chill (Hunting)";
    // Island key - the shard tag is the specific evidence (see Mudworm/Invisibug), Chills were also seen
    // around Tangleburg's Path and Moonglade Marsh.
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Wyrmgrove Tomb";
    protected override HashSet<string> DetectionItems => ["SHARD_CHILL"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Moonglade_Marsh";
    protected override string HowTo => "Go to the Wyrmgrove Tomb on Moonglade and hunt Chills for their shard.";
}

/// <summary>Honeybuzz shard on Torrhus - same pattern as the other "(Hunting)" shard tasks.</summary>
public class HoneybuzzHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Honeybuzz (Hunting)";
    // Island key, same as TikiHuntingTask - the shard tag is the specific evidence.
    protected override HashSet<string> Locations => ["Torrhus"];
    protected override string Where => "Torrhus Canyon";
    protected override HashSet<string> DetectionItems => ["SHARD_HONEYBUZZ"];
    // players often forage/gather honeycomb in the same window - that task keeps the period
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Canyon";
    protected override string HowTo => "Go to Torrhus Canyon and hunt Honeybuzz for their shard.";
}

/// <summary>
/// Solar, Ember and Water Snake shards in the Torrhus hot springs. Only the spring zones, not the
/// whole island. Fallback: the Helix foraging and honeycomb tasks (island level on Torrhus) keep any period
/// they hold items for.
/// </summary>
public class TorrhusSpringsHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Torrhus Springs (Hunting)";
    protected override HashSet<string> Locations => ["Spring Shallows", "Spring Depths", "Torrhus Springs", "Spring Path"];
    protected override HashSet<string> DetectionItems => ["SHARD_SOLAR", "SHARD_EMBER", "SHARD_WATER_SNAKE"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Springs";
    protected override string HowTo => "Go to the hot springs on Torrhus (Spring Shallows and Spring Depths) and hunt Solars, Embers and Water Snakes for their shards.";
}

/// <summary>
/// Howling Spirit and Soul of the Alpha shards in the Spirit Cave / Howling Cave of The Park. The Sven
/// Slayer task lists these zones but only the wolf drops, so shard-only periods stayed unclassified; a
/// period holding a Sven drop keeps going to Sven Slayer.
/// </summary>
public class SpiritCaveHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Spirit Cave (Hunting)";
    protected override HashSet<string> Locations => ["Spirit Cave", "Howling Cave"];
    protected override HashSet<string> DetectionItems => ["SHARD_HOWLING_SPIRIT", "SHARD_SOUL_OF_THE_ALPHA"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Park";
    protected override string HowTo => "Go to the Spirit Cave or Howling Cave in The Park and hunt Howling Spirits and Souls of the Alpha for their shards.";
}

/// <summary>
/// Lapis Zombie shard in the Lapis Quarry (Deep Caverns). Fallback: Cobblestone/Redstone mining in the same
/// quarry keeps its periods.
/// </summary>
public class LapisZombieHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Lapis Zombie (Hunting)";
    protected override HashSet<string> Locations => ["Lapis Quarry"];
    protected override HashSet<string> DetectionItems => ["SHARD_LAPIS_ZOMBIE"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Lapis_Quarry";
    protected override string HowTo => "Go to the Lapis Quarry in the Deep Caverns and hunt Lapis Zombies for their shard.";
}

/// <summary>
/// Yog (Magma Fields) and Bal (Khazad-dûm) drops in the Crystal Hollows: Yoggie, the Bal shard and the Bal
/// pet. "Yog (Hunting)" keeps every period that holds a Yog shard.
/// </summary>
public class YogAndBalTask : MethodTask
{
    protected override string MethodName => "Yog and Bal";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Khazad-dûm", "Magma Fields"];
    protected override HashSet<string> DetectionItems => ["YOGGIE", "SHARD_BAL", "PET_BAL"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Crystal_Hollows";
    protected override string HowTo =>
        "Go to the Magma Fields in the Crystal Hollows and kill Yogs for Yoggies, or to Khazad-dûm and kill Bal for its shard and pet.";
    public override string Description =>
        "Killing Yog and Bal in the Crystal Hollows (Yoggie, Bal shard, Bal pet).";
}

/// <summary>
/// The Barn's animals (Farming Islands): leather, raw beef, raw chicken, feathers, eggs and pork. The
/// Barn crops go to FarmAndBarnCropsTask.
/// </summary>
public class BarnAnimalsTask : MethodTask
{
    protected override string MethodName => "Barn Animals";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    // the literal zone (not the "The Farming Islands" island): the Mushroom Desert has its own tasks
    protected override HashSet<string> Locations => ["The Barn"];
    protected override HashSet<string> DetectionItems =>
        ["LEATHER", "RAW_BEEF", "RAW_CHICKEN", "FEATHER", "EGG", "PORK", "MUTTON", "WOOL", "RABBIT", "RABBIT_HIDE", "RABBIT_FOOT"];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Barn";
    protected override string HowTo =>
        "Go to The Barn and kill the cows, pigs, chickens, sheep and rabbits there for leather, meat, feathers and eggs.";
    public override string Description =>
        "Killing the animals in The Barn on the Farming Islands (leather, beef, chicken, pork, feathers, eggs).";
}

/// <summary>
/// Dungeon loot claimed in the Dungeon Hub when no floor is known any more (the claim happened more than
/// 30 minutes after the last floor, see <see cref="DungeonRewardAttribution"/>): a lone chest cost
/// (DUNGEON_CHEST_COST), essence, talismans, potions. Same idea as <see cref="KuudraChestClaimsTask"/>
/// but for the Catacombs. Location-only and Fallback: every Catacombs floor zone is also a Dungeon Hub
/// zone, so on a floor the floor task always wins, and KuudraChestClaimsTask keeps its periods.
/// </summary>
public class DungeonChestClaimsTask : MethodTask
{
    // location-only on "Dungeon Hub", which is also the island of every floor zone: as a public task
    // its personal view (FindMatchingPeriods) would show the player's whole dungeon profit
    protected override bool Hidden => true;
    protected override string MethodName => "Dungeon Chest Claims";
    protected override string Category => "Passive";
    protected override TaskType TaskType => TaskType.Passive;
    protected override string ActionUnit => "claims";
    protected override HashSet<string> Locations => ["Dungeon Hub"];
    // like the floors: the zone alone is the evidence, a lone "-50000000x DUNGEON_CHEST_COST" counts
    protected override int MinLocationOnlyItems => 1;
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Dungeon_Hub";
    protected override string HowTo =>
        "Claim your dungeon chest rewards at Croesus in the Dungeon Hub. Opening a chest costs coins, which are subtracted from your profit.";
    public override string Description =>
        "Claiming dungeon chest loot in the Dungeon Hub when the run's floor is no longer known.";
}

/// <summary>
/// Crops farmed outside the Garden: the Hub farm ("Farm" zone: wheat, seeds, a little nether wart) and The
/// Barn (wheat, seeds, carrots, potatoes). The per-crop Garden tasks list only the Garden (and their rates -
/// Garden plots, Farming Fortune tools - do not describe these slower fields), so these get their own task
/// instead of widening them: widening would also pull Hub/Diana and fishing periods over to the crop tasks.
/// </summary>
public class FarmAndBarnCropsTask : MethodTask
{
    protected override string MethodName => "Farm and Barn Crops";
    protected override string Category => "Farming";
    protected override string ActionUnit => "crops";
    // literal zones (not the Hub / Farming Islands islands, which have their own tasks)
    protected override HashSet<string> Locations => ["Farm", "The Barn"];
    protected override HashSet<string> DetectionItems =>
    [
        "WHEAT", "SEEDS", "ENCHANTED_WHEAT", "ENCHANTED_SEEDS", "CARROT_ITEM", "ENCHANTED_CARROT",
        "POTATO_ITEM", "ENCHANTED_POTATO", "NETHER_STALK", "ENCHANTED_NETHER_STALK"
    ];
    protected override bool Fallback => true;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Barn";
    protected override string HowTo =>
        "Harvest the crop fields at the Hub farm or in The Barn (wheat, carrots, potatoes, nether wart). The Garden is far better once unlocked.";
    public override string Description =>
        "Farming wheat, carrots, potatoes and nether wart at the Hub farm and in The Barn instead of the Garden.";
}
