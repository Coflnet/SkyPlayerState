using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class CrimsonIsleTask : IslandTask
{
    protected override string RegionName => "crimson isle";
    protected override HashSet<string> locationNames =>
    [
        "Smoldering Tomb",
        "The Bastion",
        "Scarleton",
        "Dragontail",
        "Aura's Lab",
        "Belly of the Beast",
        "Blazing Volcano",
        "Burning Desert",
        "Cathedral",
        "Chief's Hut",
        "Community Center",
        "Courtyard",
        "Dragontail Auction House",
        "Dragontail Bank",
        "Dragontail Blacksmith",
        "Dragontail Town Square",
        "Dojo",
        "Forgotten Skull",
        "Igor's Workshop",
        "Mage Council",
        "Mage Outpost",
        "Matriarch's Lair",
        "Minion Shop",
        "Mystic Marsh",
        "Odger's Hut",
        "Plhlegblast Airport",
        "Ruins of Ashfang",
        "Scarleton Auction House",
        "Scarleton Bank",
        "Scarleton Blacksmith",
        "Scarleton Plaza",
        "Scarleton Town Square",
        "Stronghold",
        "The Dukedom",
        "Throne Room",
        "Volcano Cave",
        "Volcano Manor"
    ];
}

public class CrimsonIsleFishingTask : IslandTask
{
    protected override string RegionName => "crimson isle fishing";
    protected override HashSet<string> locationNames =>
    [
        "Volcano Cave",
        "Burning Desert",
        "Mystic Marsh"
    ];
}

/// <summary>
/// Mage Outlaw is a mini boss in the Courtyard near Scarleton on the Crimson Isle, respawns 2
/// minutes after death, drops 1 Spell Powder and 20-35 Glowstone Dust.
/// </summary>
public class MageOutlawTask : MethodTask
{
    protected override string MethodName => "Mage Outlaw";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "kills";
    protected override HashSet<string> Locations => ["Courtyard"];
    protected override HashSet<string> DetectionItems => ["SPELL_POWDER", "ENCHANTED_GLOWSTONE_DUST", "GLOWSTONE_DUST"];
    // 30 Glowstone Dust/kill * ~15 kills/h (2 min respawn) = 450/h; 1 Spell Powder/kill * 15 = 15/h.
    protected override List<MethodDrop> FormulaDrops => [new("GLOWSTONE_DUST", 450), new("SPELL_POWDER", 15)];
    protected override double ActionsPerHour => 15;
    protected override string HowTo => "Go to the Courtyard near Scarleton on the Crimson Isle and kill Mage Outlaw - it respawns about 2 minutes after death.";
    // no RequiredItems/Effects: the drops are guaranteed per kill, the rate is set by the 2 minute respawn
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Mage_Outlaw";
}

/// <summary>
/// Heavy Pearls are collected from the Matriarch (Crimson Isle) - a limited number can be
/// obtained per day. Production 2026-10: ~21 players per 10h with {HEAVY_PEARL: 3..12}.
/// </summary>
public class HeavyPearlsTask : MethodTask
{
    protected override string MethodName => "Heavy Pearls";
    protected override string Category => "Mob Farming";
    protected override string ActionUnit => "pearls";
    protected override HashSet<string> Locations => ["Matriarch's Lair", "Belly of the Beast"];
    protected override HashSet<string> DetectionItems => ["HEAVY_PEARL"];
    protected override List<MethodDrop> FormulaDrops => [new("HEAVY_PEARL", 8)];
    protected override string HowTo => "Visit the Matriarch in the Belly of the Beast on the Crimson Isle and collect Heavy Pearls. Only a limited number can be collected per day.";
    public override string Description =>
        "Collecting Heavy Pearls from the Matriarch on the Crimson Isle. The amount you can collect is limited per day.";
}
