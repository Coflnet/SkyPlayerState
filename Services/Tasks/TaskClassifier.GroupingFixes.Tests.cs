using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Pins the grouping fixes of the one week production data review (2026-11-12): periods that landed in a task that only
/// shares an item with the real activity, and the new non-hunting tasks of NonHuntingGapTasks.cs against their neighbours.
/// </summary>
public class TaskClassifierGroupingFixesTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ENCHANTED_GOLD"] = 460, ["ANCIENT_CLAW"] = 150, ["GRIFFIN_FEATHER"] = 20000, ["MYCEL"] = 2, ["ENCHANTED_MYCELIUM"] = 1000,
        ["ENDER_PEARL"] = 100, ["ENCHANTED_ENDER_PEARL"] = 16000, ["ENCHANTED_ENDSTONE"] = 273, ["OBSIDIAN"] = 10,
        ["SYNTHETIC_HEART"] = 2_000_000, ["CONTROL_SWITCH"] = 300_000, ["HARD_STONE"] = 0.3, ["MITHRIL_ORE"] = 1,
        ["X"] = 19000, ["Y"] = 58000, ["Z"] = 130000, ["CORLEONITE"] = 988000, ["POISON_SAMPLE"] = 570000,
        ["SHARD_RAIN_SLIME"] = 3000, ["SHARD_KADA_KNIGHT"] = 4000, ["KADA_LEAD"] = 16000, ["COMPACT_OOZE"] = 38000,
        ["REKINDLED_EMBER_FRAGMENT"] = 60000, ["ARACHNE_FANG"] = 619000, ["STRING"] = 5, ["SAND:1"] = 0.3,
        ["ENCHANTED_RED_SAND"] = 1170, ["QUARTZ"] = 2, ["ENCHANTED_QUARTZ"] = 582, ["SAND"] = 1, ["ENCHANTED_SAND"] = 470,
        ["GLACIAL_FRAGMENT"] = 90000, ["WHITE_GIFT"] = 2180, ["RED_MUSHROOM"] = 5, ["DIAMOND"] = 10, ["SHARD_XYZ"] = 5000
    };

    private static string NameAt(string location, Dictionary<string, int> items) =>
        Classifier.Classify(location, items, 10, prices: Prices)?.TaskName;

    // ── Diana ──

    [TestCase("Wilderness")]
    [TestCase("Ruins")]
    public void GoldAlone_IsNotDiana(string zone)
        => NameAt(zone, new() { ["ENCHANTED_GOLD"] = 400 }).Should().NotBe("Diana");

    [Test]
    public void GoldNextToAncientClaws_IsStillDiana()
        => NameAt("Ruins", new() { ["ENCHANTED_GOLD"] = 400, ["ANCIENT_CLAW"] = 30 }).Should().Be("Diana");

    [Test]
    public void GriffinFeathers_AreDiana()
        => NameAt("Forest", new() { ["GRIFFIN_FEATHER"] = 3, ["ENCHANTED_GOLD"] = 100 }).Should().Be("Diana");

    [Test]
    public void BazaarAlleyPeriodWithDianaItems_IsNotDiana()
        => NameAt("Bazaar Alley", new() { ["ANCIENT_CLAW"] = 30, ["ENCHANTED_GOLD"] = 400 }).Should().NotBe("Diana");

    // ── Mycelium ──

    [Test]
    public void MinionOutputOnThePrivateIsland_IsNotMycelium()
        => NameAt("Private Island", new() { ["MYCEL"] = 800, ["ENCHANTED_MYCELIUM"] = 20 }).Should().NotBe("Mycelium");

    [Test]
    public void MyceliumMinedInMysticMarsh_IsStillMycelium()
        => NameAt("Mystic Marsh", new() { ["MYCEL"] = 800, ["ENCHANTED_MYCELIUM"] = 20 }).Should().Be("Mycelium");

    // ── The End: mining against Zealots (FD) ──

    [Test]
    public void EndStoneMinedWithStrayPearls_IsEndStoneMining_NotZealots()
        => NameAt("The End", new() { ["ENDER_STONE"] = 5000, ["ENDER_PEARL"] = 20 }).Should().Be("End Stone Mining");

    [Test]
    public void CompactedEndStoneWithPearls_IsEndStoneMining()
        => NameAt("The End", new() { ["ENCHANTED_ENDSTONE"] = 2000, ["ENCHANTED_ENDER_PEARL"] = 2, ["ENDER_PEARL"] = 30 })
            .Should().Be("End Stone Mining");

    [Test]
    public void ObsidianMinedWithPearls_IsObsidianMining()
        => NameAt("The End", new() { ["OBSIDIAN"] = 1500, ["ENDER_PEARL"] = 25 }).Should().Be("Obsidian Mining");

    [Test]
    public void SummoningEyeKeepsAMiningVolumePeriodWithZealots()
        => NameAt("The End", new() { ["ENDER_STONE"] = 5000, ["ENDER_PEARL"] = 20, ["SUMMONING_EYE"] = 2 }).Should().Be("Zealots (FD)");

    [Test]
    public void ZealotPeriodWithAFewStrayBlocks_StaysZealots()
        => NameAt("The End", new() { ["ENDER_PEARL"] = 60, ["ENDER_STONE"] = 40, ["ENCHANTED_ENDSTONE"] = 56 }).Should().Be("Zealots (FD)");

    [Test]
    public void ZealotEyesAlone_StayZealots()
        => NameAt("Zealot Bruiser Hideout", new() { ["SUMMONING_EYE"] = 3, ["ENDER_PEARL"] = 50 }).Should().Be("Zealots (FD)");

    private static string NameAtMinutes(string location, Dictionary<string, int> items, double minutes) =>
        Classifier.Classify(location, items, minutes, prices: Prices)?.TaskName;

    [Test]
    public void LongZealotSession_DoesNotTurnIntoMiningByDuration()
    {
        // 900 obsidian in 30 minutes is 1.8k/h: zealots, not the ~32k/h of mining (an absolute count of 100 flipped this)
        NameAtMinutes("The End", new() { ["OBSIDIAN"] = 900, ["ENDER_PEARL"] = 500 }, 30).Should().Be("Zealots (FD)");
    }

    [Test]
    public void ObsidianMiningSession_IsObsidianMining_WithPearls()
        => NameAtMinutes("The End", new() { ["OBSIDIAN"] = 16000, ["ENDER_PEARL"] = 20 }, 30).Should().Be("Obsidian Mining");

    [Test]
    public void RawEndStone_IsNotClaimedByObsidianMining()
    {
        var obsidianClaim = ((MethodTask)Registry.GetByName("Obsidian Mining")).GetDetectionSignature().BulkClaim;
        obsidianClaim.IsBulk(new() { ["ENDER_STONE"] = 5000 }, 10).Should().BeFalse();
        obsidianClaim.IsBulk(new() { ["OBSIDIAN"] = 5000 }, 10).Should().BeTrue();
        var endStoneClaim = ((MethodTask)Registry.GetByName("End Stone Mining")).GetDetectionSignature().BulkClaim;
        endStoneClaim.IsBulk(new() { ["ENDER_STONE"] = 5000 }, 10).Should().BeTrue();
        endStoneClaim.IsBulk(new() { ["OBSIDIAN"] = 5000 }, 10).Should().BeFalse();
    }

    [Test]
    public void BulkRule_RatesAShortPeriodAsAtLeastTenMinutes()
    {
        var rule = new BulkItemRule(new Dictionary<string, int> { ["OBSIDIAN"] = 6000 });
        rule.IsBulk(new() { ["OBSIDIAN"] = 900 }, 1).Should().BeFalse("one minute is not extrapolated to 54k an hour");
        rule.IsBulk(new() { ["OBSIDIAN"] = 1100 }, 1).Should().BeTrue();
        rule.IsBulk(new() { ["OBSIDIAN"] = 3000 }, 30).Should().BeTrue("6k an hour");
        rule.IsBulk(new() { ["OBSIDIAN"] = 2900 }, 30).Should().BeFalse();
    }

    [Test]
    public void LongRobotSession_WithModerateStone_IsStillAutomaton()
        => NameAtMinutes("Lost Precursor City", new() { ["HARD_STONE"] = 250, ["SYNTHETIC_HEART"] = 1 }, 30).Should().Be("Automaton");

    // ── Precursor City: Automaton against powder mining ──

    [Test]
    public void PartsFromChestsOfBulkMining_ArePrecursorCityPowderMining()
        => NameAt("Lost Precursor City", new() { ["HARD_STONE"] = 3000, ["SYNTHETIC_HEART"] = 1, ["CONTROL_SWITCH"] = 1 })
            .Should().Be("Precursor City Powder Mining");

    [Test]
    public void RobotPartsAlone_AreAutomaton()
        => NameAt("Lost Precursor City", new() { ["SYNTHETIC_HEART"] = 1, ["CONTROL_SWITCH"] = 2 }).Should().Be("Automaton");

    [Test]
    public void RobotFightWithAFewBlocks_IsStillAutomaton()
        => NameAt("Lost Precursor City", new() { ["HARD_STONE"] = 20, ["SYNTHETIC_HEART"] = 1 }).Should().Be("Automaton");

    // ── hotspot fishing is not offered ──

    [Test]
    public void HotspotFishingTasks_AreNotRegistered_BecauseNothingEmitsHotspotCatch()
    {
        var unregistered = new[] { typeof(BayouHotspotFishingTask), typeof(CrimsonHotspotFishingTask), typeof(BayouHotspotFishingHuntingTask) };
        TaskCatalog.IntentionallyUnregistered.Should().Contain(unregistered);
        Registry.GetByName("Bayou Hotspot Fishing").Should().BeNull();
        Registry.GetByName("Crimson Hotspot Fishing").Should().BeNull();
        Registry.GetByName("Bayou Hotspot Fishing (Hunting)").Should().BeNull();
    }

    [Test]
    public void BayouCatchPeriod_IsBayouFishing()
        => NameAt("Backwater Bayou", new() { ["HOTSPOT_CATCH"] = 3, ["RAW_FISH"] = 100 }).Should().Be("Bayou Fishing");

    // ── X, Y and Z ──

    [Test]
    public void AtominizedItems_AreXyz_EvenNextToMycelium()
        => NameAt("Mystic Marsh", new() { ["X"] = 30, ["Y"] = 10, ["Z"] = 8, ["MYCEL"] = 500 }).Should().Be("X, Y and Z (Mystic Marsh)");

    [Test]
    public void MyceliumWithoutAtominizedItems_StaysMycelium()
        => NameAt("Mystic Marsh", new() { ["MYCEL"] = 800 }).Should().Be("Mycelium");

    [Test]
    public void XyzShardInMysticMarsh_StaysXyzHunting()
        => NameAt("Mystic Marsh", new() { ["SHARD_XYZ"] = 8 }).Should().Be("Xyz (Hunting)");

    // ── Corleone ──

    [Test]
    public void CorleoniteInMithrilDeposits_IsCorleone()
        => NameAt("Mithril Deposits", new() { ["CORLEONITE"] = 2, ["MITHRIL_ORE"] = 300, ["HARD_STONE"] = 100 }).Should().Be("Corleone");

    [Test]
    public void MithrilDepositsWithoutCorleonite_StaysPowderMining()
        => NameAt("Mithril Deposits", new() { ["MITHRIL_ORE"] = 300, ["HARD_STONE"] = 100 }).Should().Be("Mithril Deposits Powder Mining");

    // ── Toxic Rain Slime ──

    [Test]
    public void PoisonSampleInTheSpidersDen_IsToxicRainSlime()
        => NameAt("Spider's Den", new() { ["POISON_SAMPLE"] = 1, ["SHARD_RAIN_SLIME"] = 5 }).Should().Be("Toxic Rain Slime");

    [Test]
    public void RainSlimeShardsWithoutASample_StayRainSlimeHunting()
        => NameAt("Spider's Den", new() { ["SHARD_RAIN_SLIME"] = 5 }).Should().Be("Rain Slime (Hunting)");

    // ── Arachne ──

    [Test]
    public void ArachnesFangInTheSanctuary_IsArachne()
        => NameAt("Arachne's Sanctuary", new() { ["ARACHNE_FANG"] = 1, ["STRING"] = 100 }).Should().Be("Arachne");

    [Test]
    public void StringAloneInTheSanctuary_IsNotArachne()
        => NameAt("Arachne's Sanctuary", new() { ["STRING"] = 100 }).Should().NotBe("Arachne");

    // ── Magma Boss and Kada Knight ──

    [Test]
    public void BossDropsInTheMagmaChamber_AreMagmaBoss()
        => NameAt("Magma Chamber", new() { ["COMPACT_OOZE"] = 5, ["REKINDLED_EMBER_FRAGMENT"] = 5 }).Should().Be("Magma Boss");

    [Test]
    public void KadaShardOnly_StaysKadaKnightHunting()
        => NameAt("Magma Chamber", new() { ["SHARD_KADA_KNIGHT"] = 5 }).Should().Be("Kada Knight (Hunting)");

    [Test]
    public void KadaLeads_AreKadaKnight()
        => NameAt("Magma Chamber", new() { ["KADA_LEAD"] = 3, ["SHARD_KADA_KNIGHT"] = 1 }).Should().Be("Kada Knight");

    [Test]
    public void KadaKnightHasItsOwnNameAndKey()
    {
        var task = Registry.GetByName("Kada Knight");
        task.Should().NotBeNull();
        task.Should().NotBeSameAs(Registry.GetByName("Kada Knight (Hunting)"));
        task.Name.Should().NotBe(Registry.GetByName("Kada Knight (Hunting)").Name);
    }

    [Test]
    public void BossDropsInTheMagmaChamber_AreNotKadaKnight()
        => NameAt("Magma Chamber", new() { ["COMPACT_OOZE"] = 5 }).Should().NotBe("Kada Knight");

    // ── lava fishing keeps its periods ──

    [TestCase("Crimson Isle", "KADA_LEAD", "Kada Knight")]
    [TestCase("Stronghold", "KADA_LEAD", "Kada Knight")]
    [TestCase("Magma Chamber", "REKINDLED_EMBER_FRAGMENT", "Magma Boss")]
    [TestCase("Burning Desert", "ENCHANTED_RED_SAND", "Red Sand Mining")]
    [TestCase("Dragontail", "SAND:1", "Red Sand Mining")]
    public void CrimsonDropsFishedUpInLava_StayCrimsonFishing(string zone, string dualDrop, string landTask)
    {
        NameAt(zone, new() { [dualDrop] = 20, ["MAGMA_FISH"] = 30, ["LAVA_HORSE_BRONZE"] = 2 }).Should().Be("Crimson Fishing");
        NameAt(zone, new() { [dualDrop] = 20 }).Should().Be(landTask, "without a lava catch the drop is the land task's");
    }

    // ── mining ──

    [Test]
    public void RedSandInTheBurningDesert_IsRedSandMining()
        => NameAt("Burning Desert", new() { ["SAND:1"] = 800, ["ENCHANTED_RED_SAND"] = 5 }).Should().Be("Red Sand Mining");

    [Test]
    public void RedSandInDragontail_IsRedSandMining()
        => NameAt("Dragontail", new() { ["ENCHANTED_RED_SAND"] = 40 }).Should().Be("Red Sand Mining");

    [Test]
    public void QuartzInTheAbandonedQuarry_IsQuartzMining()
        => NameAt("Abandoned Quarry", new() { ["QUARTZ"] = 500, ["ENCHANTED_QUARTZ"] = 20 }).Should().Be("Quartz Mining");

    [Test]
    public void DiamondsInTheRoyalMines_StayDiamondMining()
        => NameAt("Royal Mines", new() { ["DIAMOND"] = 500 }).Should().Be("Diamond Mining");

    [Test]
    public void QuartzOutsideTheQuarry_IsNotQuartzMining()
        => NameAt("Royal Mines", new() { ["QUARTZ"] = 500 }).Should().NotBe("Quartz Mining");

    [Test]
    public void SandInTheMushroomDesert_IsSandMining()
        => NameAt("Mushroom Desert", new() { ["SAND"] = 500, ["ENCHANTED_SAND"] = 5 }).Should().Be("Sand Mining");

    [Test]
    public void MushroomsInTheMushroomDesert_StayMushroomFarming()
        => NameAt("Mushroom Desert", new() { ["RED_MUSHROOM"] = 300 }).Should().Be("Red Mushroom");

    [Test]
    public void FrozenTreasuresInTheGlacialCave_AreGlacialCave()
        => NameAt("Glacial Cave", new() { ["GLACIAL_FRAGMENT"] = 4, ["WHITE_GIFT"] = 20 }).Should().Be("Glacial Cave");

    [Test]
    public void TheSameItemsOutsideTheGlacialCave_AreNotGlacialCave()
        => NameAt("Hot Springs", new() { ["GLACIAL_FRAGMENT"] = 4, ["WHITE_GIFT"] = 20 }).Should().NotBe("Glacial Cave");

    // ── Garden: Lunar Moth Trapping against farming ──

    [Test]
    public void FarmingPeriodWithMothDropsAndSpentBait_IsFarming_NotLunarMoth()
        => NameAt("The Garden", new() { ["ENCHANTED_WHEAT"] = 13000, ["MOONDEW"] = -40, ["SHARD_LUNAR_MOTH"] = 2 }).Should().NotBe("Lunar Moth Trapping");

    [Test]
    public void BaitingPeriodWithAFewCrops_StaysLunarMoth()
        => NameAt("The Garden", new() { ["ENCHANTED_WHEAT"] = 300, ["MOONDEW"] = -48, ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = 3, ["SHARD_LUNAR_MOTH"] = 3 }).Should().Be("Lunar Moth Trapping");

    // ── Fishing: Spooky Festival against Water Fishing (Hunting) ──

    [TestCase("WEREWOLF_SKIN")]
    [TestCase("SOUL_FRAGMENT")]
    [TestCase("DEEP_SEA_ORB")]
    public void SpookyExclusiveDropsKeepAShardFishingPeriodWithSpookyFishing(string spookyDrop)
        => NameAt("Hub", new() { ["RAW_FISH"] = 100, ["SHARD_FISHY"] = 2, [spookyDrop] = 3 }).Should().Be("Spooky Fishing");

    [TestCase("GREEN_CANDY")]
    [TestCase("PURPLE_CANDY")]
    public void CandyAloneIsNoProofOfTheFestival_ShardFishingStaysWaterHunting(string candy)
        => NameAt("Hub", new() { ["RAW_FISH"] = 100, ["SHARD_FISHY"] = 2, [candy] = 3 }).Should().Be("Water Fishing (Hunting)");

    [Test]
    public void ShardFishingWithoutSpookyDrops_StaysWaterHunting()
        => NameAt("Hub", new() { ["RAW_FISH"] = 100, ["SHARD_FISHY"] = 2 }).Should().Be("Water Fishing (Hunting)");

    // ── The End: Zealot (Hunting) against Zealots (FD) ──

    [Test]
    public void ShardDominantZealotPeriod_IsZealotHunting()
        => NameAt("Dragon's Nest", new() { ["SHARD_ZEALOT"] = 20, ["SHARD_SPECIAL_ZEALOT"] = 2 }).Should().Be("Zealot (Hunting)");

    [Test]
    public void DropDominantZealotPeriod_IsZealotsFd()
        => NameAt("Dragon's Nest", new() { ["SUMMONING_EYE"] = 3, ["ENDER_PEARL"] = 200, ["ENCHANTED_ENDSTONE"] = 40, ["SHARD_ZEALOT"] = 3 }).Should().Be("Zealots (FD)");
}
