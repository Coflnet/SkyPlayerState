using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

// Non-hunting methods that had no task of their own (gap analysis against OutcroCalculator, 2026-11).
// Every item id is a bazaar product id, every zone a scoreboard sub-zone of SkyblockZones.cs. Seeds are only given
// where a sourced rate exists; the others rely on the tracked data of players, like Chill (Hunting).

/// <summary>The lava fishing evidence as a zone veto for the Crimson Isle tasks whose drops (Kada Lead, Red Sand...) are fished up in lava as well.</summary>
internal static class CrimsonLava
{
    public static Dictionary<string, HashSet<string>> Veto(IEnumerable<string> zones) =>
        zones.ToDictionary(zone => zone, _ => FishingEvidence.Lava);
}

/// <summary>Mob farming of a boss or mob whose item drops come with a place and no shard.</summary>
public abstract class BaseKillTask : MethodTask
{
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override List<DropEffect> Effects => [
        new() { Name = "Magic Find", Description = "Increases rare drop chance", EstimatedMultiplier = 1.2 },
        new() { Name = "Kill speed", Description = "Faster killing means more coins per hour", EstimatedMultiplier = 1.3 }
    ];
}

/// <summary>Mining of a plain block (no gemstones): the gauntlet defaults of <see cref="BaseMiningTask"/> do not apply.</summary>
public abstract class BaseBlockMiningTask : BaseMiningTask
{
    public override List<StatFactor> StatFactors => EndBlockMiningFactors;
    protected override List<RequiredItem> RequiredItems => [];
    protected override List<DropEffect> Effects => [
        new() { Name = "Mining Speed", Description = "Faster block breaking", EstimatedMultiplier = 1.3 },
        new() { Name = "Mining Fortune", Description = "More drops per block", EstimatedMultiplier = 1.5 }
    ];
}

/// <summary>
/// Exes float in the Mystic Marsh and cannot be hurt: two that touch merge into a Wai, two Wais into a Zee, and an
/// Atominizer turns an Exe, Wai or Zee into an X, Y or Z. Not <see cref="XyzMobTask"/> (unregistered): that one detects the
/// Xyz shard in the Crystal Hollows, this one the three bazaar items in the zone they really come from.
/// </summary>
public class XyzMysticMarshTask : BaseKillTask
{
    protected override string MethodName => "X, Y and Z (Mystic Marsh)";
    protected override HashSet<string> Locations => ["Mystic Marsh"];
    protected override HashSet<string> DetectionItems => ["X", "Y", "Z"];
    protected override List<MethodDrop> FormulaDrops => [new("X", 224), new("Y", 72), new("Z", 88)];
    protected override string ActionUnit => "atominized Exes";
    public override string Description =>
        "Turn the floating Exes in the Mystic Marsh on the Crimson Isle into X, Y and Z with an Atominizer. "
        + "Two Exes that touch merge into a Wai, and two Wais into a Zee; an Exe gives an X, a Wai a Y and a Zee a Z, and Y and Z sell for much more. "
        + "You need an Atominizer, and a Charminizer to move them.";
    protected override string HowTo =>
        "Use a Charminizer to make an Exe follow you and lead it into another Exe to make a Wai. Do the same with two Wais to make a Zee. "
        + "Use the Atominizer on them to get X, Y or Z. Sell X, Y and Z on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Atominizer";
}

/// <summary>Boss Corleone of Team Treasurite, the only source of Corleonite.</summary>
public class CorleoneTask : BaseKillTask
{
    protected override string MethodName => "Corleone";
    protected override HashSet<string> Locations => ["Mithril Deposits"];
    protected override HashSet<string> DetectionItems => ["CORLEONITE"];
    // 37 kills an hour at the 25% Corleonite chance of the wiki, before Looting
    protected override List<MethodDrop> FormulaDrops => [new("CORLEONITE", 9.25)];
    public override string Description =>
        "Kill Boss Corleone in his structure in the Mithril Deposits of the Crystal Hollows for Corleonite. "
        + "He spawns again and again in one of two rare structures, and a quarter of his kills drop Corleonite along with rough Ruby and Jade. "
        + "You need a lobby that has one of his structures.";
    protected override string HowTo =>
        "Find a Corleone structure in the Mithril Deposits, a lobby can have up to two. "
        + "Wait at the spawn and kill him when he appears, he throws a ball that spawns an exploding Smog. "
        + "If the lobby has both structures, walk between them. Sell the Corleonite on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Corleone";
}

/// <summary>Toxic Rain Slimes, which only spawn in a thunderstorm and are the source of Poison Samples.</summary>
public class ToxicRainSlimeTask : BaseKillTask
{
    protected override string MethodName => "Toxic Rain Slime";
    // island key: a Poison Sample cannot come from another activity of the Spider's Den (the Bayou Sludge source is another island)
    protected override HashSet<string> Locations => ["Spider's Den"];
    protected override string Where => "Spider's Den";
    protected override HashSet<string> DetectionItems => ["POISON_SAMPLE"];
    // 1,575 kills an hour, 20% of them toxic in a thunderstorm, 5% of those drop a sample
    protected override List<MethodDrop> FormulaDrops => [new("POISON_SAMPLE", 15.75)];
    public override string Description =>
        "Kill Rain Slimes in the Spider's Den during a thunderstorm for Poison Samples. "
        + "In a thunderstorm one in five Rain Slimes is a Toxic Rain Slime, which drops a Poison Sample 5% of the time.";
    protected override string HowTo =>
        "Go to the Spider's Den when a thunderstorm starts. "
        + "Kill Rain Slimes at the base of the hill with Grandma's House and between Michael's Crib and the Flint Skeleton hill. "
        + "Sell the Poison Samples, capture the slimes with a Pocket Black Hole for Rain Slime shards as well.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Toxic_Rain_Slime";
}

/// <summary>The Arachne boss fight on the altar of the Arachne's Sanctuary.</summary>
public class ArachneTask : BaseKillTask
{
    protected override string MethodName => "Arachne";
    protected override string Category => "Combat";
    protected override HashSet<string> Locations => ["Arachne's Sanctuary"];
    protected override HashSet<string> DetectionItems => ["SHARD_ARACHNE", "ARACHNE_FANG", "DARK_QUEENS_SOUL_DROP", "LUXURIOUS_SPOOL"];
    public override string Description =>
        "Summon and kill Arachne in Arachne's Sanctuary in the Spider's Den. "
        + "Four Arachne's Callings summon the weak form, one Arachne Crystal the strong form, which can also drop a Dark Queen's Soul. "
        + "Drops include Soul String, Luxurious Spool, Arachne's Fang and the Arachne shard.";
    protected override string HowTo =>
        "Go to Arachne's Sanctuary in the Spider's Den. Place four Arachne's Callings or one Arachne Crystal on the altar. "
        + "Each hit on her deals at most 1000 damage, and she splits into Arachne's Broods at 50% and 20% health. "
        + "Sell the drops, the Fang and the Dark Queen's Soul are the valuable ones.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Arachne";
}

/// <summary>The Magma Boss in the centre lava pool of the Magma Chamber, on the same timer in every lobby.</summary>
public class MagmaBossTask : BaseKillTask
{
    protected override string MethodName => "Magma Boss";
    protected override string Category => "Combat";
    protected override HashSet<string> Locations => ["Magma Chamber"];
    protected override HashSet<string> DetectionItems => ["COMPACT_OOZE", "REKINDLED_EMBER_FRAGMENT"];
    // the Rekindled Ember Fragment is fished up in lava too: that evidence keeps the period with Crimson Fishing
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => CrimsonLava.Veto(Locations);
    protected override HashSet<string> EvidenceItems => ["MAGMA_URCHIN"];
    protected override List<MethodDrop> FormulaDrops => [new("COMPACT_OOZE", 21), new("REKINDLED_EMBER_FRAGMENT", 21), new("MAGMA_URCHIN", 0.42)];
    public override string Description =>
        "Kill the Magma Boss in the Magma Chamber on the Crimson Isle. "
        + "He spawns every 2 minutes in the centre lava pool on the same timer in every lobby, "
        + "and always drops Compact Ooze and a Rekindled Ember Fragment, with a small chance of a Magma Urchin.";
    protected override string HowTo =>
        "Go to the Magma Chamber and wait at the centre lava pool. Damage him through his split phases until he flees and dies. "
        + "The spawn timer is the same in every lobby, so be at the pool when the 2 minutes are up.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Magma_Boss";
}

/// <summary>
/// Kada Knights for their Kada Leads. <see cref="KadaKnightHuntingTask"/> (shard) is a different method: it wins the
/// periods that only hold the shard, this one those with a lead.
/// </summary>
public class KadaKnightTask : BaseKillTask
{
    protected override string MethodName => "Kada Knight";
    protected override HashSet<string> Locations => ["Crimson Isle"];
    protected override string Where => "Magma Chamber";
    protected override HashSet<string> DetectionItems => ["KADA_LEAD"];
    // The site default (226 kills per 5 minutes) looks too high to seed the estimate, the tracked data of players replaces it.
    // Kada Leads are also fished up in lava: that evidence keeps the period with Crimson Fishing.
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => CrimsonLava.Veto(Locations);
    public override string Description =>
        "Kill Kada Knights in the caves that lead to the Magma Chamber on the Crimson Isle for Kada Leads. "
        + "One in five kills drops a Kada Lead, and capturing them with a Pocket Black Hole adds the Kada Knight shard.";
    protected override string HowTo =>
        "Go to the north or south cave leading into the Magma Chamber. Kill Kada Knights quickly with a Looting weapon. "
        + "Sell the Kada Leads on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Kada_Knight";
}

/// <summary>Red Sand on the Crimson Isle. The Barbarian Outpost and The Bastion are not mapped zones, so they are left out.</summary>
public class RedSandMiningTask : BaseBlockMiningTask
{
    protected override string MethodName => "Red Sand Mining";
    protected override HashSet<string> Locations => ["Burning Desert", "Dragontail"];
    // SAND:1 is red sand (MinionData.cs)
    protected override HashSet<string> DetectionItems => ["SAND:1", "ENCHANTED_RED_SAND"];
    // both are fished up in lava too: that evidence keeps the period with Crimson Fishing
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => CrimsonLava.Veto(Locations);
    public override string Description =>
        "Mine Red Sand on the Crimson Isle in the Burning Desert or around Dragontail. "
        + "Red Sand is a mining collection block, and you sell it as Enchanted Red Sand.";
    protected override string HowTo =>
        "Go to the Burning Desert on the Crimson Isle. Mine the Red Sand with high Mining Fortune. "
        + "Compact it into Enchanted Red Sand and sell it on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Red_Sand";
    protected override double ActionsPerHour => 4000;
}

/// <summary>Pure Quartz of the Abandoned Quarry, which gives much more Nether Quartz than the ore of the Crimson Isle.</summary>
public class QuartzMiningTask : BaseBlockMiningTask
{
    protected override string MethodName => "Quartz Mining";
    protected override HashSet<string> Locations => ["Abandoned Quarry"];
    protected override HashSet<string> DetectionItems => ["QUARTZ", "ENCHANTED_QUARTZ"];
    public override string Description =>
        "Mine Pure Quartz in the Abandoned Quarry of the Dwarven Mines, which gives much more Nether Quartz than the quartz ore on the Crimson Isle.";
    protected override string HowTo =>
        "Go to the Abandoned Quarry in the Dwarven Mines. Mine the Pure Quartz. Compact it into Enchanted Quartz and sell it on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Nether_Quartz";
    protected override double ActionsPerHour => 4000;
}

/// <summary>Sand of the Mushroom Desert.</summary>
public class SandMiningTask : BaseBlockMiningTask
{
    protected override string MethodName => "Sand Mining";
    protected override HashSet<string> Locations => ["Mushroom Desert"];
    protected override HashSet<string> DetectionItems => ["SAND", "ENCHANTED_SAND"];
    public override string Description =>
        "Mine Sand in the Mushroom Desert on The Farming Islands and sell it as Enchanted Sand.";
    protected override string HowTo =>
        "Go to the Mushroom Desert. Mine Sand with high Mining Fortune. Compact it and sell it on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Sand";
    protected override double ActionsPerHour => 4000;
}

/// <summary>Frozen Treasures in the ice of the Glacial Cave, only reachable in the Season of Jerry.</summary>
public class GlacialCaveTask : BaseBlockMiningTask
{
    protected override string MethodName => "Glacial Cave";
    protected override HashSet<string> Locations => ["Glacial Cave"];
    // the treasure table of the wiki; Packed Ice and Enchanted Ice are left out, mining the ice itself gives them too
    protected override HashSet<string> DetectionItems =>
        ["GLACIAL_FRAGMENT", "FROZEN_BAIT", "GLOWY_CHUM_BAIT", "ICE_BAIT", "WHITE_GIFT", "GREEN_GIFT", "RED_GIFT", "ENCHANTED_PACKED_ICE"];
    public override string Description =>
        "Break the ice in the Glacial Cave on Jerry's Workshop for Frozen Treasures. 1-4 treasures appear inside the ice every 10 seconds.";
    protected override string HowTo =>
        "Go to the Glacial Cave on Jerry's Workshop. "
        + "Break the ice blocks that hold a Frozen Treasure, the chat says FROZEN TREASURE! You found. "
        + "Sell the Glacial Fragments and other treasures on the bazaar.";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Glacial_Cave";
    protected override double ActionsPerHour => 1500;
}
