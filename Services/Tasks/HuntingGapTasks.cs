using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Per-shard and per-method hunting tasks that used to hide inside broader tasks ──
// Every task here is keyed by its shard tags. A shard period always beats a location-only task of the same zone, and
// against another item-matched task the matched coin value decides, so these only take periods whose shards outweigh
// what the neighbouring task matched. Where a period must never move, ZoneExcludedItems vetoes it. Pinned in
// HuntingGapTasks.Tests.cs.

/// <summary>
/// A hunt that is not a weapon fight: the shard is lassoed, netted, clicked or fed, so the weapon ladder, Looting and
/// Combat level of <see cref="BaseHuntingTask"/> do not apply (same shape as <see cref="TikiHuntingTask"/>). The tools
/// (lasso, net, Huntaxe, black hole) have no verified item tags, so they are named in the how-to text only.
/// </summary>
public abstract class CritterHuntingTask : BaseHuntingTask
{
    public override List<StatFactor> StatFactors => [new("skill:Hunting", 1, 50)];
    protected override List<RequiredItem> RequiredItems => [];
    protected override string ActionUnit => "catches";
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Hunting Fortune", Description = "Increases the amount of shards per capture", EstimatedMultiplier = 1.2 }
    ];
}

/// <summary>Pots of Honeycomb applied to a protected tree spawn a critter: the shared pot tags of the three lathering tasks.</summary>
internal static class HoneyPots
{
    public static readonly HashSet<string> Tags =
        ["FUN_SIZED_POT_OF_HONEYCOMB", "FAMILY_SIZED_POT_OF_HONEYCOMB", "JUMBO_POT_OF_HONEYCOMB", "BEHEMOTH_POT_OF_HONEYCOMB"];
}

// ── Honey lathering ──

public class MangroveHoneyLatheringTask : CritterHuntingTask
{
    protected override string MethodName => "Mangrove Honey Lathering";
    // Island key, same as MangroveForagingTask
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Murkwater Loch";
    protected override HashSet<string> DetectionItems => ["SHARD_MURKBAT", "SHARD_WOODLOUSE", "SHARD_STAG_BEETLE"];
    // A pot that was applied is a negative count. How many applications one pot gives is not sourced, so no FormulaCosts.
    protected override HashSet<string> ObservedCostItems => HoneyPots.Tags;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Murkbat";
    protected override string HowTo => HuntingText.Steps(
        "Buy Pots of Honeycomb on the bazaar; Fun-Sized, Family-Sized, Jumbo and Behemoth pots all work.",
        "Go to Moonglade and find a Mangrove Tree that has a Tree Protection Order.",
        "Apply a pot to the tree to spawn one critter.",
        "Lasso it: a Murkbat drags you upward, so keep the lasso attached until it is caught; a Stag Beetle breaks free if it touches you.",
        "Sell the shards on the bazaar and keep the honeycomb and logs.");
    public override string Description =>
        "Apply Pots of Honeycomb to protected Mangrove Trees on Moonglade to spawn critters, then catch them for their shards. Each pot spawns a Murkbat (1 in 6), a Woodlouse (1 in 3) or a Stag Beetle (1 in 2). Every catch also drops Enchanted Honeycomb and Enchanted Mangrove Logs, and the Murkbat sometimes a Mangcore. You need a Lasso and pots from the bazaar.";
}

public class FigHoneyLatheringTask : CritterHuntingTask
{
    protected override string MethodName => "Fig Honey Lathering";
    protected override HashSet<string> Locations => ["Moonglade"];
    protected override string Where => "Moonglade Marsh";
    protected override HashSet<string> DetectionItems => ["SHARD_HEWVER", "SHARD_HONEYMITE", "SHARD_HONEYHOG"];
    protected override HashSet<string> ObservedCostItems => HoneyPots.Tags;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Hewver";
    protected override string HowTo => HuntingText.Steps(
        "Buy Pots of Honeycomb on the bazaar.",
        "Go to Moonglade and apply a pot to a Fig Tree that has a Tree Protection Order.",
        "Lasso a Hewver and reel it in before it reaches you; its snout breaks your lasso on contact.",
        "Lasso a Honeyhog the same way, since it charges you and breaks free on contact; capture a Honeymite with a Huntaxe and a Pocket Black Hole.",
        "Sell the shards on the bazaar.");
    public override string Description =>
        "Apply Pots of Honeycomb to protected Fig Trees on Moonglade to spawn a Hewver (1 in 6), a Honeymite (1 in 3) or a Honeyhog (1 in 2), and catch it for its shard. Every catch also drops Enchanted Honeycomb and Enchanted Fig Logs, and the Hewver sometimes a Figstone. You need a Lasso, plus a Huntaxe and a Pocket Black Hole for Honeymites.";
}

public class HelixHoneyLatheringTask : CritterHuntingTask
{
    protected override string MethodName => "Helix Honey Lathering";
    // The Torrhus zones without the Safari, like Helix Foraging
    protected override HashSet<string> Locations => [.. TorrhusLandZones.WithoutSafari];
    protected override string Where => "Torrhus Canyon";
    protected override HashSet<string> DetectionItems => ["SHARD_HIVETHIEF", "SHARD_PARCHED", "SHARD_POLLENDART"];
    protected override HashSet<string> ObservedCostItems => HoneyPots.Tags;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Hivethief";
    protected override string HowTo => HuntingText.Steps(
        "Buy Pots of Honeycomb on the bazaar and warp to Torrhus.",
        "Apply a pot to a Helix Tree that has a Tree Protection Order.",
        "Lasso a Hivethief and hold on while it drags you up; lasso a Pollendart as it dives.",
        "A Parched is a mob that only axes hurt; capture it with a Huntaxe and a Pocket Black Hole, and note it can heal once.");
    public override string Description =>
        "Apply Pots of Honeycomb to protected Helix Trees on Torrhus to spawn a Hivethief (1 in 6), a Parched (1 in 3) or a Pollendart (1 in 2), and catch it for its shard. Every catch also drops Enchanted Honeycomb and Enchanted Helix Logs, and the Hivethief sometimes a Helixis. You need a Lasso, plus a Huntaxe and a Pocket Black Hole for the Parched.";
}

// ── Lotus Atoll critters ──

public class LotusLilyPadCrittersTask : CritterHuntingTask
{
    protected override string MethodName => "Lotus Lily Pad Critters";
    // Island key, same as LotusAtollTask. The explosion's Lotus stays with Lotus Atoll when no critter shard dropped.
    protected override HashSet<string> Locations => ["Lotus Atoll"];
    protected override HashSet<string> DetectionItems => ["SHARD_SEA_SHINE", "SHARD_FLIP_FLOPPER"];
    // Site defaults: Flipflopper 17 and Seashine 7 per 5 minutes (the 50%:25% spawn odds), Flipflopper drops 32 Lotus and
    // 20% Silver Lotus, Seashine 1 Silver Lotus (wiki). Both calculators of the site are treated as one explosion stream.
    protected override List<MethodDrop> FormulaDrops =>
        [new("SHARD_FLIP_FLOPPER", 204), new("SHARD_SEA_SHINE", 84), new("LOTUS", 6528), new("LOTUS_SILVER", 125)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Lotus_Atoll";
    protected override string HowTo => HuntingText.Steps(
        "Travel to Lotus Atoll through the Ship Navigator on Backwater Bayou.",
        "Punch the loose lily pads into each other; the bigger pad absorbs the smaller one.",
        "When a pad reaches size 8 it floats up and explodes; everyone within 30 blocks who touched it gets the rewards.",
        "Net the Flipflopper (it knocks you back hard) and the Seashine (it teleports away and blinds you for 2 seconds when hit).",
        "More Tracking raises the spawn chances, and sea creatures also spawn from the explosion, so be ready to fight.");
    public override string Description =>
        "Push the floating lily pads on Lotus Atoll together until one reaches size 8 and explodes. Each explosion has a 50% chance to spawn a Flipflopper and a 25% chance to spawn a Seashine; net them for their shards, Lotus and Silver Lotus. You need Fishing 10 to reach Lotus Atoll and a Medium Fishing Net or better.";
}

public class TewtilHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Tewtil (Hunting)";
    protected override HashSet<string> Locations => ["Lotus Atoll"];
    protected override string Where => "Tewtil Tunnel";
    protected override HashSet<string> DetectionItems => ["SHARD_TEWTIL"];
    // The frogs fed are a negative count. Gold and Diamond frogs are refused by Tewtils.
    protected override HashSet<string> ObservedCostItems =>
        LotusAtollTask.FrogSpecies.SelectMany(f => new[] { "BRONZE", "SILVER" }.Select(t => $"{f}_{t}")).ToHashSet();
    // No FormulaDrops: the site default (25 Tewtils per 5 minutes, 900 frogs an hour) looks too high to seed the estimate,
    // the tracked data of players replaces it.
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Tewtil";
    protected override string HowTo => HuntingText.Steps(
        "Fish up Trophy Frogs on Lotus Atoll; Tewtils refuse Gold and Diamond frogs.",
        "Go to the Tewtil Tunnel or Lotus Eater's Cave, where most Tewtils swim.",
        "Feed one Tewtil three frogs; it grows with each one and explodes on the third.",
        "Collect the shard and the Lotus, then find the next Tewtil.");
    public override string Description =>
        "Feed Trophy Frogs to Tewtils on Lotus Atoll until they explode into a Tewtil shard. Tewtils cannot be damaged; three Bronze or Silver frogs make one explode, giving the shard and 8 Lotus. You need Fishing 10 and a supply of Trophy Frogs.";
}

public class LotumHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Lotum (Hunting)";
    protected override HashSet<string> Locations => ["Lotus Atoll"];
    protected override HashSet<string> DetectionItems => ["SHARD_LOTUM"];
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_LOTUM", 240), new("LOTUS", 1920)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Lotum";
    protected override string HowTo => HuntingText.Steps(
        "Travel to Lotus Atoll.",
        "Click a Lotum; it glows and jumps to the next lotus flower.",
        "Follow it and click again until it drops the shard (three jumps with the Frog Legs attribute instead of four).");
    public override string Description =>
        "Click Lotums around the water on Lotus Atoll until they give up their shard. Each click makes one jump to a nearby lotus flower, and after the fourth jump it drops its shard and 8 Lotus. No tool is needed, only Fishing 10 to reach the island.";
}

// ── Critter Safari biomes ──

public class CritterSafariPartyTask : MethodTask
{
    protected override string MethodName => "Critter Safari Party (Loot Share)";
    protected override string Category => "Hunting";
    protected override string ActionUnit => "catches";
    protected override HashSet<string> Locations => ["Critter Safari"];
    protected override HashSet<string> DetectionItems => [PseudoItems.SAFARI_LOOT_SHARE];
    protected override int MinLocationOnlyItems => 1;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Critter_Safari";
    protected override string HowTo => HuntingText.Steps(
        "Reach Hunting 15 and obtain a Safari Ticket from Miria's contests.",
        "Organize a party and enter the same Critter Safari through the Safari Manager in Torrhus Canyon.",
        "Spread out across the biomes and throw Critter Capsules at valuable critters; every player in the Safari receives shards from successful catches.",
        "Collect floor drops along the way and sell your shared shards on the bazaar.");
    public override string Description =>
        "Hunt with a party in the Critter Safari and collect shared shards from the group's catches across its four biomes. Every player in the Safari receives a shard when a critter is caught, so coordinating catches can reward the whole group. You need Hunting 15 and a Safari Ticket, then enter through the Safari Manager in Torrhus Canyon.";
}

/// <summary>
/// One biome of the Critter Safari. The scoreboard only reports "Critter Safari", so the biome is told apart by the
/// shards that were caught (the biomes have disjoint critter lists, wiki Critter_Safari); the biome with the most
/// valuable matched shards wins. A run is bounded by capsules, not time, so there is no hourly formula. Not a
/// <see cref="BaseHuntingTask"/>: the weapon ladder is meaningless here, capsules do the catching.
/// </summary>
public abstract class CritterSafariBiomeTask : MethodTask
{
    protected override string Category => "Hunting";
    protected override string ActionUnit => "catches";
    protected override HashSet<string> Locations => ["Critter Safari"];
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems => new() { ["Critter Safari"] = [PseudoItems.SAFARI_LOOT_SHARE] };
    // a dedicated minigame instance: even a single shard is unambiguous
    protected override int MinLocationOnlyItems => 1;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Critter_Safari";
    protected override string HowTo => HuntingText.Steps(
        "Win a Safari Ticket in Miria's contest; better brackets give tickets with 40, 48 or 56 capsules instead of 32.",
        "Talk to the Safari Manager in Torrhus Canyon to enter, then walk to the biome.",
        "Throw Critter Capsules at the most valuable critters: commons are always caught, uncommons 75%, rares 50%, epics 25% and legendaries 5% of the time, and the 20th throw at a legendary is guaranteed.",
        "Pick up floor drops (marked by string) for Safari Essence, Hunting XP, shards or quest items.",
        "Sell the shards on the bazaar; every critter anyone catches also gives you its shard.");
}

public class CritterSafariHauntedTask : CritterSafariBiomeTask
{
    protected override string MethodName => "Critter Safari: Haunted";
    protected override HashSet<string> DetectionItems =>
    [
        "SHARD_HIDEONWALL", "SHARD_DOOMSPIRAL", "SHARD_GAZER", "SHARD_GIMMIEGOLD", "SHARD_HIDEYHO",
        "SHARD_DUPLICO", "SHARD_LITTERBUG", "SHARD_BLOODBAT", "SHARD_AREITA", "SHARD_SOLSNATCHER"
    ];
    public override string Description =>
        "Use your Critter Capsules in the Haunted biome of the Critter Safari, a haunted mansion with a small cave where the Doomspiral spawns. Hideonwall and Doomspiral are its most valuable critters; the Hideyho is found by playing hide and seek. You need Hunting 15 and a Safari Ticket from Miria's contests.";
}

public class CritterSafariForestTask : CritterSafariBiomeTask
{
    protected override string MethodName => "Critter Safari: Forest";
    protected override HashSet<string> DetectionItems =>
    [
        "SHARD_HIDEONFLOOR", "SHARD_FLUFFLING", "SHARD_MACAW", "SHARD_PARAKEET", "SHARD_BLUEBIRD",
        "SHARD_TREEFROG", "SHARD_WOODCHUCKER", "SHARD_FOXTROT", "SHARD_HONEYBUG"
    ];
    public override string Description =>
        "Use your Critter Capsules in the Forest biome of the Critter Safari, a network of trees with a small cave below. Hideonfloor and Fluffling are its most valuable critters, and the Birdfeeder summons a Bluebird, Parakeet or Macaw if you bring it food from floor drops. You need Hunting 15 and a Safari Ticket.";
}

public class CritterSafariCavernTask : CritterSafariBiomeTask
{
    protected override string MethodName => "Critter Safari: Cavern";
    protected override HashSet<string> DetectionItems =>
    [
        "SHARD_SNOOZLE", "SHARD_CHUCKWALLA", "SHARD_GEMZIE", "SHARD_SCRAPPY", "SHARD_ROCKMITE",
        "SHARD_FLITTER", "SHARD_DRIFTLING", "SHARD_SHYWORM", "SHARD_CAVERNFISH"
    ];
    public override string Description =>
        "Use your Critter Capsules in the Cavern biome of the Critter Safari, a mine with raw gold veins and ore carts. Snoozle, Chuckwalla and Gemzie are its most valuable critters. You need Hunting 15 and a Safari Ticket.";
}

public class CritterSafariIcyTask : CritterSafariBiomeTask
{
    protected override string MethodName => "Critter Safari: Icy";
    protected override HashSet<string> DetectionItems =>
    [
        "SHARD_WUMPA", "SHARD_MANTIS_SHRIMP", "SHARD_BILLYGOAT", "SHARD_TROODON", "SHARD_NOZZLENOSE",
        "SHARD_POLARIS", "SHARD_STRONGARM", "SHARD_SHUDDERSQUID", "SHARD_TEPID"
    ];
    public override string Description =>
        "Use your Critter Capsules in the Icy biome of the Critter Safari. Wumpa is its most valuable critter. The biome has the Glacite Tunnels cold meter, so stay near campfires and out of the water. You need Hunting 15 and a Safari Ticket.";
}

// ── Torrhus ──

/// <summary>Torrhus sea creatures fished from the springs; their shards are charmed with a Salt. Island key: the shards only come from these creatures.</summary>
public class TorrhusSeaCreaturesHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Torrhus Sea Creatures (Hunting)";
    // The island key would include the Critter Safari, whose own shards stay with Critter Safari (like Helix Honey Lathering)
    protected override HashSet<string> Locations => [.. TorrhusLandZones.WithoutSafari];
    protected override string Where => "Spring Shallows";
    protected override HashSet<string> DetectionItems =>
        ["SHARD_TORRID", "SHARD_SPRAWL", "SHARD_GIANT_ISOPOD", "SHARD_BRINELING", "SHARD_SILKBREEZE", "SHARD_HAGGARD"];
    // exclusive rare drops of these creatures
    protected override HashSet<string> EvidenceItems =>
        ["SEA_BRINE", "SUBLIME_SILK", "WATER_HYACINTH", "ISOPOD_HUSK", "REINFORCED_NETTING", "DISTANT_ECHO"];
    // the three Salt types, only counted when consumed
    protected override HashSet<string> ObservedCostItems =>
        ["LUSHLILAC_BONBON", "PRIME_LUSHLILAC_BONBON", "EXALTED_LUSHLILAC_BONBON"];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Torrhus_Springs";
    protected override string HowTo => HuntingText.Steps(
        "Warp to Torrhus and fish in the Torrhus Springs, for example the Spring Shallows.",
        "Use a Salt before you kill a sea creature; your Charm Chance decides whether you get its shard.",
        "A Giant Isopod always appears at the top of the springs and dives to the Desert Temple once it is at half health, so follow it down.",
        "Sell the shards on the bazaar; Sublime Silk and Isopod Husk are rare bonus drops.");
    public override string Description =>
        "Fish in the water on Torrhus and kill the sea creatures with a Salt active to charm their shards. Haggard and Brineling are most of the catches; Sprawl, Torrid, Silkbreeze and Giant Isopod are rarer and their shards are worth more. They also drop Helix Logs and Ruby Veilshroom. Torrid needs Fishing 15, and Torrhus needs Foraging XII and Heart of the Forest tier 4.";
}

public class SepialotGoldolotHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Sepialot and Goldolot (Hunting)";
    protected override HashSet<string> Locations => ["Spring Depths", "Spring Path"];
    protected override string Where => "Spring Depths";
    protected override HashSet<string> DetectionItems => ["SHARD_GOLDOLOT", "SHARD_SEPIALOT"];
    // a Helix foraging period on the Spring Path that picked up one Goldolot stays with Helix Foraging
    protected override Dictionary<string, HashSet<string>> ZoneExcludedItems =>
        new() { ["Spring Path"] = ["HELIX_LOG", "ENCHANTED_HELIX_LOG"] };
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Sepialot";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Spring Depths (underwater) or the Spring Path on Torrhus.",
        "Walk up to a Sepialot until a red warning sign appears over it.",
        "Stand still: after 5 seconds the sign turns yellow, and after 3 more seconds it disappears.",
        "Only then throw your net or lasso; throwing early scares it away.");
    public override string Description =>
        "Net Sepialots in the Spring Depths and on the Spring Path of Torrhus; one in five of them is a Goldolot, whose shard is worth far more. Each catch also drops Ruby Veilshroom. You need a Fishing Net or a Lasso and some patience.";
}

public class AntHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Ant (Hunting)";
    protected override HashSet<string> Locations => ["Ant's Cave"];
    protected override HashSet<string> DetectionItems => ["SHARD_ANT", "SHARD_QUEEN_ANT"];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Ant";
    protected override string HowTo => HuntingText.Steps(
        "Swim down the Spring Depths on Torrhus and take the path to the Ant's Cave.",
        "Break Ant Hills and kill the Ants with a Vacuum while a Salt is active.",
        "Kill the Ants around a Queen Ant first; she cannot be damaged until they are dead.",
        "Leave a Huntrap in the cave for passive shards.");
    public override string Description =>
        "Kill Ants in the Ant's Cave on Torrhus with a Salt active to charm their shards. Ants are pests, so only a Vacuum damages them, and breaking an Ant Hill spawns 2-4 more. Huntraps in the cave catch Ant and Queen Ant shards.";
}

// ── Crystal Hollows, The Park, Crimson Isle ──

public class ThystHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Thyst (Hunting)";
    protected override HashSet<string> Locations => ["Crystal Hollows"];
    protected override string Where => "Jungle";
    protected override HashSet<string> DetectionItems => ["SHARD_THYST"];
    // site default: 36 Thysts per 5 minutes
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_THYST", 432)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Thyst";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Jungle in the Crystal Hollows and mine Amethyst.",
        "When a Thyst spawns, it leaps at you and hits for 250 damage.",
        "Lasso it (or kill it with a Salt active) to get the shard.",
        "Keep mining; the Armadillo pet's Tunneler ability also spawns Thysts.");
    public override string Description =>
        "Mine Amethyst in the Crystal Hollows and lasso the Thysts it spawns. Every Amethyst block you break has a 20% chance to spawn one, so you earn shards on top of your gemstones. You need a Lasso; a Salt also works.";
}

/// <summary>
/// Soul of the Alpha in the caves of The Park. Fallback for the same reason as Howling Spirit: the Sven Slayer task lists
/// these zones and a period with a Sven drop must stay with it. A weapon hunt (Huntaxe and Pocket Black Hole).
/// </summary>
public class SoulOfTheAlphaHuntingTask : BaseHuntingTask
{
    protected override string MethodName => "Soul of the Alpha (Hunting)";
    protected override string ActionUnit => "catches";
    protected override HashSet<string> Locations => ["Soul Cave", "Howling Cave", "Spirit Cave"];
    protected override string Where => "Howling Cave";
    protected override HashSet<string> DetectionItems => ["SHARD_SOUL_OF_THE_ALPHA"];
    protected override bool Fallback => true;
    // site default: 48 per 5 minutes
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_SOUL_OF_THE_ALPHA", 576)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Soul_of_the_Alpha";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Howling Cave in The Park.",
        "Bring a Soul of the Alpha to low health with a Huntaxe and place a Pocket Black Hole to capture it.",
        "Place a Huntrap in the Howling Cave for extra shards while you are away.");
    public override string Description =>
        "Capture Souls of the Alpha in the caves of The Park for their shard. They spawn in the Soul Cave, Howling Cave and Spirit Cave. You need a Huntaxe and a Pocket Black Hole, or a Salt.";
}

public class DiveGhastHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Dive Ghast (Hunting)";
    protected override HashSet<string> Locations => ["The Wasteland"];
    protected override HashSet<string> DetectionItems => ["SHARD_DIVE_GHAST"];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Dive_Ghast";
    protected override string HowTo => HuntingText.Steps(
        "Go to the Wasteland near the Forgotten Skull at night.",
        "Wear a Netherrack-Looking Sunshade to make them passive, or dodge their dives.",
        "Lasso one (or kill it with a Salt active) for the shard.");
    public override string Description =>
        "Lasso Dive Ghasts in the Wasteland on the Crimson Isle at night for their shard. They only spawn near the Forgotten Skull between 7 PM and 6 AM SkyBlock time, and they dive at you and explode instead of shooting fireballs. They also drop Ghast Tears.";
}

/// <summary>
/// Cinderbat on the Crimson Isle (caves under the Blazing Volcano). The old CinderbatTask (Galatea ember zones, still
/// unregistered) was wrong about the place; the shard tag is confirmed on the bazaar.
/// </summary>
public class CinderbatHuntingTask : CritterHuntingTask
{
    protected override string MethodName => "Cinderbat (Hunting)";
    protected override HashSet<string> Locations => ["Crimson Isle"];
    protected override string Where => "Blazing Volcano";
    protected override HashSet<string> DetectionItems => ["SHARD_CINDER_BAT"];
    // site default: 23 per 5 minutes
    protected override List<MethodDrop> FormulaDrops => [new("SHARD_CINDER_BAT", 276)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Cinderbat";
    protected override string HowTo => HuntingText.Steps(
        "Search the caves under the Blazing Volcano for fire particles on the ceiling.",
        "Throw your Lasso before it flies off; Spider's Boots help you get closer.",
        "Check the bazaar before selling: buy orders for this shard are much lower than sell offers.");
    public override string Description =>
        "Catch Cinderbats in the caves under the Blazing Volcano on the Crimson Isle for their legendary shard. They hang from blocks without a nametag, surrounded by fire particles, and fly around fast once disturbed; they never attack. You need a Lasso, or a Huntaxe and a Pocket Black Hole, or a Salt.";
}
