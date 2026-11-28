using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Mining Ender Nodes in The End (community wiki "Ender Node", checked 2026-11-28: 18 End Stone and 18 Obsidian nodes
/// are on the island at any time and respawn every 0.25 s; about a third of the nodes spawn 2-4 Nest Endermites, each of
/// which drops 1 Mite Gel and 1 Enchanted End Stone). Mite Gel and Ender Gauntlets only come from nodes, so before this
/// task the node periods went to Zealots (FD) through their Enchanted Ender Pearls (production 2026-11-26..28: 106 periods,
/// 21 players).
/// </summary>
public class EnderNodesTask : BaseMiningTask
{
    /// <summary>
    /// Mite Gel (Nest Endermites) or exp bottles at an hourly rate only node mining reaches (~480 Mite Gel/h for a dedicated
    /// miner); a zealot or dragon fight holds a few of them at most.
    /// </summary>
    internal static readonly BulkItemRule NodeBulk = new(
        new Dictionary<string, int> { ["MITE_GEL"] = 200, ["GRAND_EXP_BOTTLE"] = 60 });

    /// <summary>
    /// Drops of the other activities of The End (Zealots, Voidgloom Seraphs, the Ender Dragon). Next to one of them a
    /// handful of Mite Gel is a node broken on the way, not node mining, unless the period is <see cref="NodeBulk"/>.
    /// </summary>
    internal static readonly string[] FightDrops =
    [
        "SUMMONING_EYE", "CRYSTAL_FRAGMENT", "NULL_SPHERE", "SHARD_ZEALOT", "SHARD_SPECIAL_ZEALOT", "SHARD_BRUISER",
        "SHARD_OBSIDIAN_DEFENDER", .. T4VoidgloomsTask.BossDrops, .. EnderDragonTask.DragonLoot
    ];

    protected override string MethodName => "Ender Nodes";
    protected override HashSet<string> Locations => ["The End"];
    // Mite Gel, gauntlets, geodes, shulkers, Magical Runes and Shrimp only drop from nodes; Grand/Titanic exp bottles
    // have no other source in The End (Stonk miners get no Nest Endermites and so no Mite Gel, only the bottles)
    protected override HashSet<string> DetectionItems =>
    [
        "MITE_GEL", "ENDER_GAUNTLET", "ENDSTONE_GEODE", "END_STONE_SHULKER", "RUNE_MAGIC", "SHRIMP_THE_FISH",
        "GRAND_EXP_BOTTLE", "TITANIC_EXP_BOTTLE"
    ];
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => new() { ["The End"] = [.. FightDrops] };
    protected override BulkItemRule BulkClaim => NodeBulk;
    // ~500 nodes/h: production 2026-11-26..28 (3.3 h of node periods, 21 players) gave ~480 Mite Gel/h and per Mite Gel
    // 1.08 Enchanted End Stone, 0.33 Enchanted Ender Pearls, 0.17 Grand and 0.08 Enchanted Obsidian. Titanic bottles drop
    // from 1.31% of the obsidian nodes (wiki), about a quarter of the nodes mined.
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("MITE_GEL", 480), new("ENCHANTED_ENDSTONE", 520), new("ENCHANTED_ENDER_PEARL", 160),
        new("GRAND_EXP_BOTTLE", 80), new("ENCHANTED_OBSIDIAN", 40), new("TITANIC_EXP_BOTTLE", 1.5)
    ];
    // the Ender Gauntlet is co-op soulbound (NEU), geodes and shulkers have no bazaar buyers: rare node loot without a rate
    protected override HashSet<string> RareDrops => ["ENDER_GAUNTLET", "ENDSTONE_GEODE", "END_STONE_SHULKER", "RUNE_MAGIC", "SHRIMP_THE_FISH"];
    // finding the nodes matters more than breaking them; the Endermite pet spawns more Nest Endermites (wiki)
    public override List<StatFactor> StatFactors =>
    [
        new("pet:ENDERMITE", 0.35, 100),
        new("stat:Mining Speed", 0.35, 4000),
        new("skill:Mining", 0.30, 60)
    ];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Endermite Pet", Description = "A Legendary Endermite pet (or the Mite Spawner attribute) spawns more Nest Endermites, each worth a Mite Gel", EstimatedMultiplier = 1.3 },
        new() { Name = "Mining Speed", Description = "Break nodes faster", EstimatedMultiplier = 1.2 }
    ];
    protected override string Where => "The End";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Ender_Node";
    protected override string HowTo =>
        "Go to The End and look for Ender Nodes: purple blocks with purple particles in the End Stone and Obsidian all over the island, also around the Dragon's Nest. "
        + "Mine every node you see; a broken node turns into bedrock and back after 5 seconds, and a new one spawns somewhere else right away. "
        + "About a third of the nodes spawn Nest Endermites, kill them for Mite Gel, the most valuable drop. "
        + "Don't use a Stonk: it removes the Nest Endermites and with them the Mite Gel.";
    protected override string ActionUnit => "nodes";
    protected override double ActionsPerHour => 500;
}
