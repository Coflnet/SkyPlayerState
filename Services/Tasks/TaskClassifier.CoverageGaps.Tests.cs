using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Pins the catch-all tasks of CoverageGapTasks.cs (production 2026-10: ~6.8k of 110k periods were
/// unclassified in frequent, low-value zones) against real production period shapes - every one must
/// classify a period nothing else claimed, and a more specific existing task must keep every period it
/// matched, even when production prices make the catch-all's tag worth far more.
/// </summary>
public class TaskClassifierCoverageGapsTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static string Classify(string location, Dictionary<string, int> items, Dictionary<string, double> prices = null) =>
        Classifier.Classify(location, items, 10, prices: prices)?.TaskName;

    private static readonly string[] NewTaskNames =
    [
        "Crimson Isle Mobs", "Galatea Contests", "Glacite Walker", "Chill (Hunting)", "Honeybuzz (Hunting)",
        "Yog and Bal", "Barn Animals", "Farm and Barn Crops", "Dungeon Chest Claims",
        "Torrhus Springs (Hunting)", "Spirit Cave (Hunting)", "Lapis Zombie (Hunting)"
    ];

    // ── the fallback guarantee itself ──

    [Test]
    public void EveryNewTask_IsAFallback_AndOnlyTheLocationOnlyOnesAreHidden()
    {
        foreach (var name in NewTaskNames)
        {
            var task = Registry.GetByName(name) as MethodTask;
            task.Should().NotBeNull($"{name} must be registered");
            var signature = task.GetDetectionSignature();
            signature.Fallback.Should().BeTrue($"{name} may only classify periods no other task claims");
            // a location-only task's personal view (FindMatchingPeriods) takes every period in its zones,
            // so on a whole island it must stay a hidden accounting bucket
            task.IsHidden.Should().Be(signature.DetectionItems.Count == 0, $"{name}: location-only catch-alls are hidden, item keyed ones public");
        }
    }

    [Test]
    public void FallbackTask_NeverOutValuesARegularTask_EvenWithAPriceFarAboveIt()
    {
        // Helix Foraging (regular, items ~3k each) and a contest coupon priced at a billion: the
        // classifier compares matched value before Priority, so without Fallback the coupon would win
        var items = new Dictionary<string, int> { { "ENCHANTED_HELIX_LOG", 5 }, { "MIRIA_COUPON", 40 } };
        var prices = new Dictionary<string, double> { { "ENCHANTED_HELIX_LOG", 3200 }, { "MIRIA_COUPON", 1_000_000_000 } };

        Classify("Torrhus Canyon", items, prices).Should().Be("Helix Foraging");
    }

    [Test]
    public void FallbackTask_LosesToARegularLocationOnlyTask_ItNeverTakesItsPeriod()
    {
        // Galatea Fishing (regular, location-only) already classified this lone coupon period
        Classify("Driptoad Delve", new() { { "AGATHA_COUPON", 40 } }).Should().Be("Galatea Fishing");
    }

    // ── 1. Crimson Isle mobs ──

    [TestCase("Stronghold")]
    [TestCase("Dragontail")]
    [TestCase("Burning Desert")]
    [TestCase("The Dukedom")]
    [TestCase("Crimson Fields")]
    [TestCase("The Wasteland")]
    [TestCase("Magma Chamber")]
    [TestCase("Smoldering Tomb")]
    [TestCase("Mystic Marsh")]
    [TestCase("Crimson Isle")]
    public void CrimsonIsleMobZones_WithMobDrops_ClassifyToCrimsonIsleMobs(string zone)
    {
        // production shape: generic mob drops, nothing any dedicated task lists
        Classify(zone, new() { { "ROTTEN_FLESH", 40 }, { "STRING", 12 }, { "MAGMA_CHUNK", 3 } })
            .Should().Be("Crimson Isle Mobs");
    }

    [Test]
    public void CrimsonIsleMobs_NeedsTheUsualLocationOnlyActivity()
    {
        Classify("Stronghold", new() { { "ROTTEN_FLESH", 2 } }).Should().BeNull();
    }

    [Test]
    public void CrimsonIsleMobs_MoreSpecificTasksStillWin()
    {
        // shard hunting, mycelium, bazaar purchase - each keeps its period at the same zones
        Classify("Stronghold", new() { { "SHARD_BEZAL", 1 }, { "ROTTEN_FLESH", 40 } }).Should().Be("Bezal (Hunting)");
        Classify("Mystic Marsh", new() { { "MYCEL", 40 }, { "ROTTEN_FLESH", 12 } }).Should().Be("Mycelium");
        Classify("Stronghold", new() { { PseudoItems.BAZAAR_PURCHASE, 5000 }, { "ROTTEN_FLESH", 40 } }).Should().Be("Bazaar Purchases");
        // even when the generic drops are priced high
        Classify("Stronghold", new() { { "SHARD_BEZAL", 1 }, { "ROTTEN_FLESH", 40 } },
            new() { { "SHARD_BEZAL", 1500 }, { "ROTTEN_FLESH", 5_000_000 } }).Should().Be("Bezal (Hunting)");
    }

    // ── 2. Galatea contests ──

    [TestCase("Murkwater Loch", "AGATHA_COUPON")]
    [TestCase("Wyrmgrove Tomb", "AGATHA_COUPON")]
    [TestCase("Torrhus Canyon", "MIRIA_COUPON")]
    [TestCase("Miria's Hut", "MIRIA_PRIZE")]
    [TestCase("Murkwater Loch", "STARLYN_PRIZE")]
    public void GalateaContestRewards_ClassifyToGalateaContests(string zone, string tag)
    {
        // both Galatea islands: Murkwater Loch/Wyrmgrove Tomb are Moonglade, Torrhus Canyon/Miria's Hut Torrhus
        Classify(zone, new() { { tag, 3 } }).Should().Be("Galatea Contests");
    }

    [Test]
    public void GalateaContests_ForagingStillWins_EvenWithPricedCoupons()
    {
        var prices = new Dictionary<string, double> { { "AGATHA_COUPON", 9801 }, { "ENCHANTED_FIG_LOG", 1 } };
        Classify("Murkwater Loch", new() { { "ENCHANTED_FIG_LOG", 2 }, { "AGATHA_COUPON", 25 } }, prices)
            .Should().Be("Fig Foraging");
    }

    // ── 3. Glacite Walker armour ──

    [Test]
    public void GlaciteArmourAtTheGreatIceWall_ClassifiesToGlaciteWalker()
    {
        Classify("Great Ice Wall", new() { { "GLACITE_HELMET", 1 }, { "GLACITE_BOOTS", 1 } }).Should().Be("Glacite Walker");
    }

    [Test]
    public void GlaciteWalker_ShardHuntingAndMiningStillWin()
    {
        var prices = new Dictionary<string, double> { { "GLACITE_HELMET", 1_000_000_000 }, { "GLACITE_JEWEL", 46000 }, { "MITHRIL_ORE", 7 } };
        Classify("Great Ice Wall", new() { { "GLACITE_HELMET", 1 }, { "GLACITE_JEWEL", 2 } }, prices)
            .Should().Be("Glacite Walker (Hunting)");
        Classify("Great Ice Wall", new() { { "GLACITE_HELMET", 1 }, { "MITHRIL_ORE", 200 } }, prices)
            .Should().Be("Mithril Mining");
    }

    // ── 4. Hub farm / The Barn ──

    [TestCase("Farm")]
    [TestCase("The Barn")]
    public void CropsAtTheHubFarmAndBarn_ClassifyToFarmAndBarnCrops(string zone)
    {
        // production shape: Garden crop tasks list only the Garden, so these stayed unclassified
        Classify(zone, new() { { "WHEAT", 300 }, { "SEEDS", 400 }, { "POTATO_ITEM", 20 } }).Should().Be("Farm and Barn Crops");
        Classify(zone, new() { { "NETHER_STALK", 150 } }).Should().Be("Farm and Barn Crops");
    }

    [Test]
    public void BarnAnimalDrops_ClassifyToBarnAnimals()
    {
        Classify("The Barn", new() { { "LEATHER", 30 }, { "RAW_BEEF", 40 }, { "RAW_CHICKEN", 30 }, { "FEATHER", 30 }, { "EGG", 20 } })
            .Should().Be("Barn Animals");
    }

    [Test]
    public void FarmAndBarn_GardenCropsDianaAndPurchasesStillWin()
    {
        // the Garden keeps its crop task
        Classify("The Garden", new() { { "WHEAT", 300 }, { "SEEDS", 400 } }).Should().Be("Wheat Farming");
        // a Hub farm Diana burrow period with one stray carrot stays Diana
        Classify("Farm", new() { { "ENCHANTED_GOLD", 24 }, { "ROTTEN_FLESH", 5 }, { "CARROT_ITEM", 1 } }).Should().Be("Diana");
        // the Mushroom Desert keeps its mushroom task
        Classify("Mushroom Desert", new() { { "RED_MUSHROOM", 100 } }).Should().Be("Red Mushroom");
        Classify("The Barn", new() { { PseudoItems.BAZAAR_PURCHASE, 5000 }, { "WHEAT", 100 } }).Should().Be("Bazaar Purchases");
    }

    // ── 5. Chill / Honeybuzz shards ──

    [Test]
    public void ChillShardAtWyrmgroveTomb_ClassifiesToChillHunting()
    {
        // production shape: SHARD_CHILL 77, BONE 74, RUNEBLADE_TALISMAN 32
        Classify("Wyrmgrove Tomb", new() { { "SHARD_CHILL", 2 }, { "BONE", 30 }, { "RUNEBLADE_TALISMAN", 1 } })
            .Should().Be("Chill (Hunting)");
    }

    [Test]
    public void HoneybuzzShardOnTorrhus_ClassifiesToHoneybuzzHunting()
    {
        Classify("Torrhus Canyon", new() { { "SHARD_HONEYBUZZ", 3 } }).Should().Be("Honeybuzz (Hunting)");
    }

    [Test]
    public void ChillAndHoneybuzz_OtherTasksKeepTheirPeriods_EvenWhenTheShardIsPriced()
    {
        var prices = new Dictionary<string, double> { { "SHARD_CHILL", 1_000_000_000 }, { "SHARD_HONEYBUZZ", 1_000_000_000 }, { "ENCHANTED_HELIX_LOG", 3200 }, { "SHARD_MUDWORM", 10 } };
        Classify("Torrhus Canyon", new() { { "SHARD_HONEYBUZZ", 3 }, { "ENCHANTED_HELIX_LOG", 5 } }, prices).Should().Be("Helix Foraging");
        Classify("Wyrmgrove Tomb", new() { { "SHARD_CHILL", 2 }, { "SHARD_MUDWORM", 1 } }, prices).Should().Be("Mudworm (Hunting)");
    }

    // ── 6. Yog and Bal ──

    [TestCase("Khazad-dûm", "SHARD_BAL")]
    [TestCase("Khazad-dûm", "YOGGIE")]
    [TestCase("Magma Fields", "YOGGIE")]
    [TestCase("Khazad-dûm", "PET_BAL")]
    public void YogAndBalDrops_ClassifyToYogAndBal(string zone, string tag)
    {
        Classify(zone, new() { { tag, 2 }, { "SULPHUR", 6 } }).Should().Be("Yog and Bal");
    }

    [Test]
    public void YogAndBal_YogShardsAndCoalMiningStillWin()
    {
        var prices = new Dictionary<string, double> { { "SHARD_BAL", 121159 }, { "YOGGIE", 1_000_000_000 }, { "COAL", 6 }, { "SHARD_YOG", 3000 } };
        Classify("Magma Fields", new() { { "YOGGIE", 5 }, { "COAL", 300 } }, prices).Should().Be("Coal Mining");
        Classify("Khazad-dûm", new() { { "SHARD_BAL", 1 }, { "SHARD_YOG", 1 } }, prices).Should().Be("Yog (Hunting)");
    }

    // ── 7. Dungeon Hub chest claims ──

    [Test]
    public void LoneChestCostInTheDungeonHub_ClassifiesToDungeonChestClaims()
    {
        // production: a lone "-50000000x DUNGEON_CHEST_COST" period more than 30 minutes after the floor
        Classify("Dungeon Hub", new() { { PseudoItems.DUNGEON_CHEST_COST, -50_000_000 } }).Should().Be("Dungeon Chest Claims");
    }

    [Test]
    public void EssenceAndLootInTheDungeonHub_ClassifyToDungeonChestClaims()
    {
        Classify("Dungeon Hub", new() { { "ENDER_PEARL", 12 }, { "SUPERBOOM_TNT", 3 }, { "CAT_TALISMAN", 1 }, { PseudoItems.DUNGEON_CHEST_COST, -2_000_000 } })
            .Should().Be("Dungeon Chest Claims");
    }

    [Test]
    public void DungeonChestClaims_FloorsKuudraClaimsAndPurchasesStillWin()
    {
        // on a floor the floor task wins (every floor zone is also a Dungeon Hub zone)
        Classify("The Catacombs (F7)", new() { { PseudoItems.DUNGEON_CHEST_COST, -6_000_000 } }).Should().Be("F7");
        // a floor resolved by DungeonRewardAttribution is a floor zone too
        Classify("The Catacombs (M7)", new() { { "ESSENCE_WITHER", 40 }, { PseudoItems.DUNGEON_CHEST_COST, -50_000_000 } }).Should().Be("M7");
        // Kuudra loot claimed here keeps its task, whatever the chest cost weighs
        Classify("Dungeon Hub", new() { { "KUUDRA_TEETH", 3 }, { PseudoItems.DUNGEON_CHEST_COST, -50_000_000 } }).Should().Be("Kuudra Chest Claims");
        Classify("Dungeon Hub", new() { { PseudoItems.BAZAAR_PURCHASE, 5000 }, { PseudoItems.DUNGEON_CHEST_COST, -50_000_000 } }).Should().Be("Bazaar Purchases");
    }

    // ── Shards without a home (production 2026-10 sample) ──

    [Test]
    public void BarbarianDukeX_ShardAtTheDukedom_ClassifiesToItsOwnTask()
    {
        Classify("The Dukedom", new() { { "SHARD_BARBARIAN_DUKE_X", 4 }, { "LEATHER_CLOTH", 1 } }).Should().Be("Barbarian Duke X");
        // a lone shard, and with the generic mob drops that used to send it to Crimson Isle Mobs
        Classify("The Dukedom", new() { { "SHARD_BARBARIAN_DUKE_X", 2 } }).Should().Be("Barbarian Duke X");
        Classify("The Dukedom", new() { { "SHARD_BARBARIAN_DUKE_X", 4 }, { "PORK", 55 }, { "LEATHER_CLOTH", 2 } }).Should().Be("Barbarian Duke X");
        // without the shard the hidden fallback still takes the zone
        Classify("The Dukedom", new() { { "PORK", 55 }, { "LEATHER_CLOTH", 2 } }).Should().Be("Crimson Isle Mobs");
    }

    [Test]
    public void BarbarianDukeX_DoesNotTakeAnotherShardTasksPeriod()
    {
        Classify("Stronghold", new() { { "SHARD_BEZAL", 6 } }).Should().Be("Bezal (Hunting)");
    }

    [TestCase("Spring Shallows", "SHARD_SOLAR", 47)]
    [TestCase("Spring Depths", "SHARD_EMBER", 9)]
    [TestCase("Torrhus Springs", "SHARD_WATER_SNAKE", 3)]
    [TestCase("Spring Path", "SHARD_SOLAR", 5)]
    public void TorrhusSpringShards_ClassifyToTorrhusSpringsHunting(string zone, string shard, int count)
    {
        Classify(zone, new() { { shard, count } }).Should().Be("Torrhus Springs (Hunting)");
    }

    [Test]
    public void TorrhusSprings_HelixForagingKeepsItsPeriod_EvenWithAShardPriceFarAboveIt()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_HELIX_LOG", 23 }, { "SHARD_EMBER", 2 } };
        Classify("Torrhus Springs", items, new() { { "ENCHANTED_HELIX_LOG", 3200 }, { "SHARD_EMBER", 5_000_000 } }).Should().Be("Helix Foraging");
    }

    [TestCase("Murkwater Loch", "SHARD_VERDANT", 4)]
    [TestCase("Murkwater Shallows", "SHARD_AZURE", 1)]
    [TestCase("Murkwater Depths", "SHARD_SALMON", 2)]
    [TestCase("Murkwater Shallows", "SHARD_COD", 1)]
    [TestCase("Murkwater Loch", "SHARD_AZURE", 2)]
    public void MurkwaterFishingShards_ClassifyToGalateaFishingHunting_EvenASingleOne(string zone, string shard, int count)
    {
        Classify(zone, new() { { shard, count } }).Should().Be("Galatea Fishing (Hunting)");
    }

    [Test]
    public void MurkwaterShards_NamedShardTasksAndForagingKeepTheirPeriods()
    {
        Classify("Murkwater Shallows", new() { { "SHARD_AZURE", 1 }, { "SHARD_DREADWING", 1 } }).Should().Be("Dreadwing");
        Classify("Murkwater Loch", new() { { "SHARD_COD", 1 }, { "MANGROVE_LOG", 248 } }).Should().Be("Mangrove Foraging");
        // no shard, one item: the lowered threshold is only for shard periods
        Classify("Murkwater Shallows", new() { { "SEA_LUMIES", 1 } }).Should().BeNull();
    }

    [TestCase("Spirit Cave", "SHARD_SOUL_OF_THE_ALPHA", 106)]
    [TestCase("Howling Cave", "SHARD_HOWLING_SPIRIT", 16)]
    public void SpiritCaveShards_ClassifyToSpiritCaveHunting(string zone, string shard, int count)
    {
        Classify(zone, new() { { shard, count } }).Should().Be("Spirit Cave (Hunting)");
    }

    [Test]
    public void SpiritCave_SvenDropsStillGoToSvenSlayer()
    {
        Classify("Spirit Cave", new() { { "WOLF_TOOTH", 20 }, { "SHARD_HOWLING_SPIRIT", 2 } },
            new() { { "WOLF_TOOTH", 10 }, { "SHARD_HOWLING_SPIRIT", 1_000_000 } }).Should().Be("Sven Slayer");
    }

    [Test]
    public void LapisZombieShard_ClassifiesToLapisZombieHunting_ButMiningKeepsItsPeriod()
    {
        Classify("Lapis Quarry", new() { { "SHARD_LAPIS_ZOMBIE", 11 }, { "ROTTEN_FLESH", 11 } }).Should().Be("Lapis Zombie (Hunting)");
        Classify("Lapis Quarry", new() { { "SHARD_LAPIS_ZOMBIE", 4 }, { "COBBLESTONE", 300 } },
            new() { { "COBBLESTONE", 2 }, { "SHARD_LAPIS_ZOMBIE", 1_000_000 } }).Should().Be("Cobblestone Mining");
    }
}
