using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// Galatea is an archipelago of two separate islands, Moonglade (default, /warp galatea) and
// Torrhus (tab Area: "Torrhus Canyon") - see SkyblockZones.cs for the verified zone split. All
// three trackers below only ever listed Moonglade zones (none of Torrhus's - Critter Safari
// Entrance, Miria's Hut, Torrhus Heights, Torrhus/Spring zones), so despite the generic "galatea"
// RegionName/naming they are already Moonglade-only and need no splitting; Where/Island/Warp/Wiki
// resolve correctly to "Moonglade" (via locationNames.FirstOrDefault() -> SkyblockZones.IslandOf)
// once the zone map itself is correct. No general tracker exists yet for Torrhus specifically.
public class GalateaDivingTask : IslandTask
{
    protected override string RegionName => "galatea";
    protected override HashSet<string> locationNames =>
    [
        "Driptoad Delve",
        "Ancient Ruins",
        "Dive-Ember Pass",
        "Drowned Reliquary",
        "Kelpwoven Tunnels",
        "Murkwater Depths",
        "Murkwater Shallows",
        "Reefguard Pass",
        "Squid Cave"
    ];
}
public class GalateaFishingTask : IslandTask
{
    protected override string RegionName => "galatea";
    protected override HashSet<string> locationNames =>
    [
        "Driptoad Delve"
    ];
}
public class GalateaTask : IslandTask
{
    protected override string RegionName => "galatea";
    protected override HashSet<string> locationNames =>
    [
        "Ancient Ruins",
        "Evergreen Plateau",
        "Fusion House",
        "Murkwater Outpost",
        "North Wetlands",
        "North Reaches",
        "Red House",
        "Moonglade Marsh",
        "Murkwater Loch", // down but outside of water
        "Reefguard Pass",
        "Side-Ember Way",
        "South Reaches",
        "South Wetlands",
        "Stride-Ember Fissure",
        "Tangleburg",
        "Tangleburg Bank",
        "Tangleburg's Path",
        "Tomb Floodway",
        "Tranquil Pass",
        "Tranquility Sanctum",
        "Verdant Summit",
        "West Reaches",
        "Wyrmgrove Tomb"
    ];
}
