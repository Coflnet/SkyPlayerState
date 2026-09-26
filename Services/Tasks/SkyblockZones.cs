using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Maps a scoreboard/tab "zone" name to the SkyBlock island it belongs to.
///
/// <para>
/// <see cref="Services.ScoreboardParser.ExtractArea"/> reads the sidebar's area line, which for
/// most islands (Crystal Hollows, Crimson Isle, The End, Moonglade, Torrhus, Dwarven Mines, ...)
/// reports the fine grained SUB-ZONE ("Jungle", "Goblin Holdout", "Dwarven Village", "Mystic Marsh", ...) and
/// only rarely the island name itself. A task declared with island-level <c>Locations</c> (e.g.
/// <c>["Crystal Hollows"]</c>) therefore almost never matches on an exact string comparison against
/// that sub-zone, which is exactly what let "Sludge Mining" (Locations = ["Crystal Hollows"]) go
/// undetected in practice. This class is the single source of truth for zone-to-island resolution
/// so every place that compares a task's <c>Locations</c> to a live/stored zone (the classifier,
/// <c>MethodTask.FindMatchingPeriods</c>, <c>IslandTask.Execute</c>, the backfill job, ...) resolves
/// it the same way instead of re-deriving (and drifting from) its own copy.
/// </para>
/// </summary>
public static class SkyblockZones
{
    /// <summary>
    /// Zone names that are reused by more than one island (or are otherwise not resolvable from
    /// the zone name alone), so mapping them to a single island would be actively wrong for some
    /// players. Deliberately left out of <see cref="IslandOf"/> (returns null); live classification
    /// can still resolve these via the tab list's "Area:" line
    /// (<see cref="Models.ExtractedInfo.CurrentIsland"/>), which is authoritative where the static
    /// map cannot be.
    /// </summary>
    public static readonly HashSet<string> AmbiguousZones = new(StringComparer.Ordinal)
    {
        "Dragon's Lair",     // Crystal Hollows AND Galatea
        "Wizard Tower",      // reported to also exist outside the Hub
        "Your Island",       // private island, same name regardless of island type
        "The Bastion",       // Crimson Isle AND reported elsewhere
        "Community Center",  // Hub AND Crimson Isle both use this exact zone name
    };

    private static readonly Dictionary<string, string> ZoneToIsland = BuildMap();

    /// <summary>
    /// Multi-island "groups" - an island-level match for the group name (e.g. a task declaring
    /// <c>Locations = ["Galatea"]</c>) matches a zone on ANY island in the group. Corrected
    /// 2026-09 (game owner knowledge): Galatea is an archipelago of two separate islands with
    /// essentially non-overlapping tasks/content - "Moonglade" (the default, <c>/warp galatea</c>)
    /// and "Torrhus" (tab Area reports its entry zone, "Torrhus Canyon") - not one combined island.
    /// A third island, "Lunarise", is planned but unreleased and has no zones yet.
    /// </summary>
    private static readonly Dictionary<string, string> IslandToGroup = new(StringComparer.Ordinal)
    {
        ["Moonglade"] = "Galatea",
        ["Torrhus"] = "Galatea",
    };

    /// <summary>The island a zone belongs to, or null when unknown/ambiguous.</summary>
    public static string IslandOf(string zone)
    {
        if (string.IsNullOrEmpty(zone) || AmbiguousZones.Contains(zone))
            return null;
        return ZoneToIsland.GetValueOrDefault(zone);
    }

    /// <summary>
    /// All zones known to belong to <paramref name="island"/> (does not include the island name
    /// itself unless it was also registered as a zone, e.g. "Crystal Hollows").
    /// </summary>
    public static IEnumerable<string> ZonesOf(string island) =>
        ZoneToIsland.Where(kv => kv.Value == island).Select(kv => kv.Key);

    /// <summary>The multi-island group <paramref name="island"/> belongs to (see <see cref="IslandToGroup"/>), or null if it isn't part of one.</summary>
    public static string GroupOf(string island) =>
        island != null && IslandToGroup.TryGetValue(island, out var group) ? group : null;

    /// <summary>
    /// Resolves a raw tab list "Area: &lt;value&gt;" reading (see TabListUpdate) to the canonical
    /// island name <see cref="Models.ExtractedInfo.CurrentIsland"/> should store. Unlike the
    /// scoreboard sidebar (always a sub-zone), the tab list reports the island name itself for
    /// most islands - but Galatea's two islands are a confirmed exception (production logs, player
    /// Ekwav): the tab Area line reports each one's entry sub-region ("Moonglade Marsh"/"Torrhus
    /// Canyon") instead of a clean island name. Delegates to the same zone map used for scoreboard
    /// sub-zones (both sub-regions are registered there, resolving to "Moonglade"/"Torrhus"
    /// respectively - never to the group name "Galatea", which is not itself a zone) so the two
    /// lookups never drift apart; a value the map does not recognize (e.g. "Private Island", which
    /// is deliberately NOT registered here since the scoreboard's own zone for it is the
    /// separately-ambiguous "Your Island" - see AmbiguousZones - and task Locations use the literal
    /// string "Private Island", e.g. MyceliumTask) passes through unchanged.
    /// </summary>
    public static string NormalizeTabArea(string area) =>
        string.IsNullOrEmpty(area) ? area : IslandOf(area) ?? area;

    /// <summary>
    /// True when a task's declared <c>Locations</c> matches <paramref name="zone"/> - directly
    /// (zone-specific Locations like <c>["Jungle"]</c>), via the zone's parent island (island-level
    /// Locations like <c>["Crystal Hollows"]</c>), or via the island's multi-island group (e.g.
    /// <c>["Galatea"]</c> matches a zone on either Moonglade or Torrhus - see <see cref="GroupOf"/>).
    /// </summary>
    public static bool Matches(IReadOnlyCollection<string> taskLocations, string zone)
    {
        if (taskLocations == null || taskLocations.Count == 0 || zone == null)
            return false;
        if (taskLocations.Contains(zone))
            return true;
        var island = IslandOf(zone);
        if (island == null)
            return false;
        if (taskLocations.Contains(island))
            return true;
        var group = GroupOf(island);
        return group != null && taskLocations.Contains(group);
    }

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string island, params string[] zones)
        {
            foreach (var zone in zones)
            {
                if (AmbiguousZones.Contains(zone))
                    continue;
                map[zone] = island;
            }
        }

        Add("Crystal Hollows",
            "Crystal Hollows", "Crystal Nucleus", "Fairy Grotto", "Goblin Holdout", "Goblin Queen's Den",
            "Jungle Temple", "Lost Precursor City", "Magma Fields", "Mithril Deposits", "Precursor Remnants",
            "Jungle", "Khazad-dûm", "Mines of Divan");

        Add("Crimson Isle",
            "Crimson Isle", "Smoldering Tomb", "Scarleton", "Dragontail", "Aura's Lab", "Belly of the Beast",
            "Blazing Volcano", "Burning Desert", "Cathedral", "Chief's Hut", "Courtyard",
            "Dragontail Auction House", "Dragontail Bank", "Dragontail Blacksmith", "Dragontail Town Square",
            "Dojo", "Forgotten Skull", "Igor's Workshop", "Mage Council", "Mage Outpost", "Matriarch's Lair",
            "Minion Shop", "Mystic Marsh", "Odger's Hut", "Plhlegblast Airport", "Ruins of Ashfang",
            "Scarleton Auction House", "Scarleton Bank", "Scarleton Blacksmith", "Scarleton Plaza",
            "Scarleton Town Square", "Stronghold", "The Dukedom", "Throne Room", "Volcano Cave", "Volcano Manor",
            "Magma Chamber");

        Add("The End",
            "The End", "Dragon's Nest", "Void Sepulture", "Void Slate", "Zealot Bruiser Hideout");

        Add("Dwarven Mines",
            "Dwarven Mines", "Aristocrat's Passage", "Barracks of Heroes", "CC Inc.", "Cliffside Veins", "Commission's Office",
            "Dwarven Tavern", "Dwarven Village", "Far Reserve", "First-Class Lounge", "Forge", "Forge Basin",
            "Gates to the Mines", "Grand Library", "Great Ice Wall", "Hanging Court", "Lava Springs",
            "Miner's Guild", "Palace Bridge", "Puzzler's Hideout", "Rampart's Quarry", "Royal Mines",
            "Royal Palace", "Royal Quarters", "The Great Forge", "The Lift", "The Mist", "Upper Mines",
            "Glacite Tunnels", "Great Glacite Lake", "Glacite Mineshafts", "The Forge");

        // Galatea is an archipelago of two separate islands (corrected 2026-09, game owner
        // knowledge), each verified zone-by-zone against hypixelskyblock.minecraft.wiki (Galatea /
        // Moonglade_Marsh / Torrhus_Canyon pages, 2026-09) - their tasks are essentially disjoint.
        // The island KEYS below ("Moonglade"/"Torrhus") are deliberately never used as a zone name:
        // both "Moonglade Marsh" and "Torrhus Canyon" are themselves real zones (each island's own
        // entry area, same self-referencing pattern as e.g. "Crystal Hollows"), AND several tasks
        // list one of those exact strings as one specific zone among several unrelated ones
        // (HuntingTasks.cs, HuntingTrapTask.cs, MobFarmTasks.cs). If the island key equalled the
        // zone name, Matches() would let those tasks silently expand to match every zone on the
        // whole island via the IslandOf(zone) fallback, instead of just the one zone they meant.
        // Use the "Galatea" group (IslandToGroup/GroupOf) for a task that genuinely spans both.
        Add("Moonglade", // the default Galatea island - "/warp galatea" (IslandInfo below)
            "Moonglade Marsh", "Tangleburg", "Fusion House", "SwampCut Inc.", "Tangleburg Bank",
            "Tangleburg Library", "Tangleburg's Path", "Evergreen Plateau", "South Reaches", "South Wetlands",
            "West Reaches", "Verdant Summit", "Westbound Wetlands", "Murkwater Outpost", "North Reaches",
            "North Wetlands", "Red House", "Moonglade's Edge", "Murkwater Loch", "Murkwater Shallows",
            "Murkwater Depths", "Ancient Ruins", "Forest Temple", "Reefguard Pass", "Drowned Reliquary",
            "Squid Cave", "Dive-Ember Pass", "Stride-Ember Fissure", "Side-Ember Way", "Driptoad Pass",
            "Driptoad Delve", "Kelpwoven Tunnels", "Bubbleboost Column", "Wyrmgrove Tomb", "Tomb Floodway",
            "Tranquil Pass", "Tranquility Sanctum");
            // "Dragon's Lair" also exists here but is shared with Crystal Hollows - see AmbiguousZones.

        Add("Torrhus", // the second Galatea island - tab Area line reports "Torrhus Canyon" (its entry zone)
            "Torrhus Canyon", "Critter Safari Entrance", "Miria's Hut", "Pangolin Hideaway", "Torrhus Heights",
            "Spring Path", "Torrhus Springs", "Spring Shallows", "Spring Depths", "Ant's Cave", "Hotspot Haven",
            "Desert Temple");

        // Its own separate Fishing island (reached via the Ship Navigator NPC on Backwater Bayou),
        // NOT a Galatea zone - was wrongly nested under Galatea before this fix. Verified:
        // hypixelskyblock.minecraft.wiki/w/Lotus_Atoll (2026-09).
        Add("Lotus Atoll", "Lotus Atoll", "Lotus Eater's Cave", "Lotus Highlands", "Tewtil Tunnel");

        // The Farming Islands - a separate island from Crimson Isle (was wrongly nested there
        // before this fix, presumably because both are desert-themed). Verified zone-by-zone
        // against hypixelskyblock.minecraft.wiki (The_Farming_Islands / Mushroom_Desert / The_Barn
        // pages, 2026-09): The Barn (/warp barn, Farming I) is the entry point, with a warp pad to
        // Mushroom Desert (Farming V) and its sub-areas.
        Add("The Farming Islands",
            "The Barn", "Windmill",
            "Mushroom Desert", "Oasis", "Mushroom Gorge", "Glowing Mushroom Cave", "Desert Settlement",
            "Desert Mountain", "Archaeological Site", "Underground Lab", "Overgrown Mushroom Cave",
            "Jake's House", "Shepherd's Keep", "Trapper's Den", "Treasure Hunter Camp");

        Add("Hub",
            "Hub", "Auction House", "Bank", "Bazaar Alley", "Blacksmith", "Canvas Room", "Colosseum",
            "Farm", "Fashion Shop", "Flower House", "Forest", "Graveyard", "Hexatorum", "Library",
            "Mountain", "Museum", "Pet Care", "Ruins", "Tavern", "Thaumaturgist", "Village", "Wilderness",
            "Crypts", "Coal Mine",
            // Confirmed in production scoreboard logs (player Ekwav) paired with tab Area: Hub.
            "Builder's House", "Combat Settlement", "Communal Stew", "Fishing Outpost", "Foraging Camp");

        Add("The Park",
            "The Park", "Birch Park", "Dark Thicket", "Howling Cave", "Jungle Island", "Melody's Plateau",
            "Savanna Woodland", "Spruce Woods", "The Howling Cave", "The Wolf's Den", "Viking Longhouse",
            "Spooky Festival"); // seasonal event instance of The Park, not its own island

        Add("Deep Caverns",
            "Deep Caverns", "Diamond Reserve", "Emerald Reserve", "Gold Reserve", "Gunpowder Mines",
            "Lapis Quarry", "Obsidian Sanctuary", "Pigmen's Den", "Redstone Quarry", "Slimehill");

        Add("Gold Mine", "Gold Mine");

        Add("Backwater Bayou", "Backwater Bayou");

        Add("Spider's Den",
            "Spider's Den", "Arachne's Sanctuary", "Archaeologist's Camp", "Spider Mound", "The Spider's Den",
            "Gravel Mines", "Arachne's Burrow", "Grandma's House");

        Add("Garden",
            "The Garden", "Plot 1", "Plot 2", "Plot 3", "Plot 4", "Plot 5", "Plot 6",
            "Plot 7", "Plot 8", "Plot 9", "Plot 10", "Plot 11", "Plot 12");

        Add("Kuudra", "Kuudra", "Kuudra's Hollow");

        Add("Jerry",
            "Jerry's Workshop", "Jerry Pond", "Sunken Jerry Pond", "Reflective Pond", "Mount Jerry",
            "Glacial Cave", "Hot Springs", "Gary's Shack", "Terry's Shack");

        Add("Rift",
            "The Rift", "Wyld Woods", "Dreadfarm", "West Village", "Shifted Tavern");

        Add("Dungeon Hub",
            "Dungeon Hub", "The Catacombs", "Master Mode", "Floor VII",
            "Master Mode Catacombs Floor IV", "Master Mode Catacombs Floor V",
            "Master Mode Catacombs Floor VI", "Master Mode Catacombs Floor VII");

        return map;
    }

    /// <summary>
    /// Warp command and wiki page for an island, keyed by the same island names <see cref="IslandOf"/>
    /// returns. Used to build the "Type /warp X" and "[Map of island]" steps in
    /// <see cref="MethodTask.Steps"/> when a task has no more specific <c>WarpCommand</c>/<c>WikiUrl</c>
    /// of its own. All warp commands and wiki URLs here were verified (real in-game /warp aliases,
    /// wiki pages checked to return HTTP 200 - not the wiki's soft-404 "noarticletext" page) before
    /// being added; keep it that way when adding new islands.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string WarpCommand, string WikiUrl)> IslandInfo =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Crystal Hollows"] = ("/warp crystals", WikiPageUrl("Crystal Hollows")),
            ["Dwarven Mines"] = ("/warp mines", WikiPageUrl("Dwarven Mines")),
            ["Crimson Isle"] = ("/warp isle", WikiPageUrl("Crimson Isle")),
            ["The End"] = ("/warp end", WikiPageUrl("The End")),
            ["Moonglade"] = ("/warp galatea", WikiPageUrl("Moonglade Marsh")),
            ["Torrhus"] = ("/warp torrhus", WikiPageUrl("Torrhus Canyon")), // also "/warp canyon" - both verified
            ["Lotus Atoll"] = ("/warp lotus", WikiPageUrl("Lotus Atoll")), // also "/warp atoll" - both verified
            // "/warp desert" also works for Mushroom Desert specifically, but only after consuming a
            // Travel Scroll to Mushroom Island - /warp barn is the one guaranteed to always work.
            ["The Farming Islands"] = ("/warp barn", WikiPageUrl("The Farming Islands")),
            ["The Park"] = ("/warp park", WikiPageUrl("The Park")),
            ["Spider's Den"] = ("/warp spider", WikiPageUrl("Spider's Den")),
            ["Hub"] = ("/warp hub", WikiPageUrl("Hub")),
            ["Garden"] = ("/warp garden", WikiPageUrl("The Garden")),
            ["Backwater Bayou"] = ("/warp bayou", WikiPageUrl("Backwater Bayou")),
            ["Deep Caverns"] = ("/warp deep", WikiPageUrl("Deep Caverns")),
            ["Gold Mine"] = ("/warp gold", WikiPageUrl("Gold Mine")),
            ["Rift"] = ("/warp rift", WikiPageUrl("The Rift")),
            ["Dungeon Hub"] = ("/warp dungeon_hub", WikiPageUrl("Dungeon Hub")),
            ["Kuudra"] = ("/warp kuudra", WikiPageUrl("Kuudra's Hollow")),
            ["Jerry"] = ("/warp jerry", WikiPageUrl("Jerry's Workshop")),
        };

    /// <summary>Warp command for an island, or null if unknown.</summary>
    public static string WarpCommandFor(string island) =>
        island != null && IslandInfo.TryGetValue(island, out var info) ? info.WarpCommand : null;

    /// <summary>Wiki page (with the island's map) for an island, or null if unknown.</summary>
    public static string IslandWikiUrl(string island) =>
        island != null && IslandInfo.TryGetValue(island, out var info) ? info.WikiUrl : null;

    /// <summary>
    /// Community wiki page URL for a page title (spaces -&gt; underscores, everything else
    /// percent-escaped so apostrophes etc. always resolve). wiki.hypixel.net was shut down
    /// July 2026; hypixelskyblock.minecraft.wiki is the community successor.
    /// </summary>
    public static string WikiPageUrl(string pageTitle) =>
        string.IsNullOrEmpty(pageTitle) ? null
            : "https://hypixelskyblock.minecraft.wiki/w/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
}
