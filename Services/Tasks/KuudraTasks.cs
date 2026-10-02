using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

public abstract class BaseKuudraTask : MethodTask
{
    protected override string Category => "Kuudra";
    protected override string ActionUnit => "runs";
    protected override string WarpCommand => "/warp kuudra";
    // A Kuudra tier zone ("Kuudra's Hollow (T1)".."(T5)") is a dedicated instance - it can only ever
    // mean that one tier, so a couple of essence/attribute shard items is already unambiguous
    // evidence, unlike a shared open-world zone. See DungeonTasks.BaseDungeonTask for the same idea.
    protected override int MinLocationOnlyItems => 1;
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "TERROR_CHESTPLATE", Reason = "Kuudra armor set" },
        new() { ItemTag = "HYPERION", Reason = "Weapon" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Crimson Essence multiplier", Description = "Higher tier drops more essence per run", EstimatedMultiplier = 1.0 },
        new() { Name = "Party size", Description = "Full party of 4 speeds up runs significantly", EstimatedMultiplier = 1.5 }
    ];
}

// Bare "Kuudra"/"Kuudra's Hollow" deliberately removed from every tier below: neither ever occurs
// on the real scoreboard (verified against production logs), and the bare island-level name matched
// EVERY tier's periods via SkyblockZones.Matches' island fallback - the same "every floor matches
// every dungeon task" bug as DungeonTasks.cs. See SkyblockZones.cs for the real per-tier zone
// strings ("Kuudra's Hollow (T1)" etc.) each tier now matches instead.
public class KuudraT1Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T1";
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T1)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 600)];
    protected override double ActionsPerHour => 12;
    protected override string HowTo => "Queue for Kuudra Basic (T1) via the NPC in the Crimson Isle. Fight waves of mobs and defeat Kuudra. Easiest tier, good for beginners.";
}
public class KuudraT2Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T2";
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T2)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 900)];
    protected override double ActionsPerHour => 10;
    protected override string HowTo => "Queue for Kuudra Hot (T2). Requires better gear than T1, drops more Crimson Essence.";
}
public class KuudraT3Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T3";
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T3)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 1200), new("ATTRIBUTE_SHARD", 15)];
    protected override double ActionsPerHour => 8;
    protected override string HowTo => "Queue for Kuudra Burning (T3). First tier that drops Attribute Shards. Requires good armor and team coordination.";
}
public class KuudraT4Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T4";
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T4)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 1500), new("ATTRIBUTE_SHARD", 30)];
    protected override double ActionsPerHour => 6;
    protected override string HowTo => "Queue for Kuudra Fiery (T4). High attribute shard drops. Requires strong Terror/Aurora armor and good team.";
}
public class KuudraT5Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T5";
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T5)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 2000), new("ATTRIBUTE_SHARD", 50)];
    protected override double ActionsPerHour => 5;
    protected override string HowTo => "Queue for Kuudra Infernal (T5). Highest tier with best drops. Requires maxed gear, team of 4 experienced players. Best money maker in the game for endgame players.";
}

/// <summary>
/// Kuudra loot is claimed outside the instance (Croesus in the Dungeon Hub, Forgotten Skull) so the
/// chest rewards land in a different zone than the Kuudra tier zones. Most claims are re-attributed
/// to the tier zone they belong to (see KuudraRewardAttribution); this task only receives claims whose
/// run tier is unknown. Periods that were already resolved to a Catacombs floor by
/// DungeonRewardAttribution are not affected. A claim takes seconds, so each period counts as one run.
/// </summary>
public class KuudraChestClaimsTask : BaseKuudraTask
{
    protected override string MethodName => "Kuudra Chest Claims";
    protected override HashSet<string> Locations => ["Dungeon Hub", "Forgotten Skull"];
    protected override HashSet<string> DetectionItems =>
    [
        "KUUDRA_TEETH", "KUUDRA_TENTACLE", "KUUDRA_MANDIBLE",
        .. new[] { "CRIMSON", "AURORA", "TERROR", "FERVOR", "HOLLOW" }
            .SelectMany(set => new[] { "HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS" }.Select(piece => $"{set}_{piece}"))
    ];
    protected override List<MethodDrop> FormulaDrops => [new("KUUDRA_TEETH", 100)];
    protected override double ActionsPerHour => 8;
    // one run per claim: the claim period counts as the time one run takes (7.5 minutes)
    protected override TimeSpan MinimumPeriodDuration => TimeSpan.FromHours(1 / ActionsPerHour);
    protected override string HowTo => "Claim your Kuudra chest rewards after a run. Loot whose Kuudra tier is unknown shows up here; claims for a known tier are counted at that tier.";
    public override string Description =>
        "Claiming Kuudra chest loot (teeth, tentacles, mandibles, Kuudra armor) outside the instance when the run's tier is unknown.";
}
