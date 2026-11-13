using Coflnet.Sky.PlayerState.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Pins the per-shard hunting tasks of HuntingGapTasks.cs and the changes to the broader tasks they were split from
/// (Critter Safari, Spring Critters, Howling Spirit, the foraging tasks): each new task wins its own periods, the
/// neighbour named in the test keeps the periods that must stay with it, and the broad task still catches what is left.
/// Prices are the bazaar sell offers of 2026-11-12, used for ranking only.
/// </summary>
public class HuntingGapTasksTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static readonly Dictionary<string, double> Prices = new()
    {
        { "SHARD_MURKBAT", 301_000 }, { "SHARD_WOODLOUSE", 81_000 }, { "SHARD_STAG_BEETLE", 50_000 },
        { "SHARD_HEWVER", 196_000 }, { "SHARD_HONEYMITE", 92_700 }, { "SHARD_HONEYHOG", 54_800 },
        { "SHARD_HIVETHIEF", 26_100 }, { "SHARD_PARCHED", 23_500 }, { "SHARD_POLLENDART", 8_000 },
        { "SHARD_SEA_SHINE", 226_900 }, { "SHARD_FLIP_FLOPPER", 84_300 }, { "SHARD_TEWTIL", 130_000 }, { "SHARD_LOTUM", 44_500 },
        { "SHARD_TORRID", 419_600 }, { "SHARD_SPRAWL", 150_000 }, { "SHARD_HAGGARD", 14_500 }, { "SHARD_GOLDOLOT", 193_700 },
        { "SHARD_SEPIALOT", 30_500 }, { "SHARD_ANT", 50_000 }, { "SHARD_THYST", 54_500 }, { "SHARD_SOUL_OF_THE_ALPHA", 29_900 },
        { "SHARD_HOWLING_SPIRIT", 6_700 }, { "SHARD_DIVE_GHAST", 141_100 }, { "SHARD_CINDER_BAT", 44_900 },
        { "SHARD_HIDEONWALL", 139_100 }, { "SHARD_DOOMSPIRAL", 70_600 }, { "SHARD_HIDEONFLOOR", 134_600 },
        { "SHARD_FLUFFLING", 46_200 }, { "SHARD_SNOOZLE", 50_400 }, { "SHARD_WUMPA", 38_200 }, { "SHARD_BLOODBAT", 5_000 },
        { "SHARD_CHAMELEON", 777_700 }, { "SHARD_HUMMINGBIRD", 36_300 }, { "SHARD_GRIZZLY_BEAR", 124_100 }, { "HELIXIS", 517_000 },
        { "SHARD_QUEEN_BEE", 612_500 }, { "SHARD_STONEWORM", 118_900 }, { "SHARD_SYCOPHANT", 49_400 },
        { "SHARD_SPECIAL_ZEALOT", 18_900 }, { "SHARD_MINER_ZOMBIE", 8_000 },
        { "MANGROVE_LOG", 8 }, { "ENCHANTED_MANGROVE_LOG", 1_200 }, { "FIG_LOG", 5 }, { "ENCHANTED_FIG_LOG", 1_500 },
        { "HELIX_LOG", 14 }, { "ENCHANTED_HELIX_LOG", 2_200 }, { "HONEYCOMB", 300 }, { "RUBY_VEILSHROOM", 40 },
        { "LOTUS", 40 }, { "WATER_LILY", 3 }, { "COMMON_FROG_BRONZE", 500 },
        { "FINE_AMETHYST_GEM", 1_200 }, { "ROUGH_AMETHYST_GEM", 25 }, { "GHAST_TEAR", 1_000 }, { "SUBLIME_SILK", 300_000 },
        { "WORM_MEMBRANE", 40_000 }, { "REVENANT_FLESH", 400 }
    };

    private static string Name(string zone, Dictionary<string, int> items, bool priced = true) =>
        Classifier.Classify(zone, items, 10, prices: priced ? Prices : null)?.TaskName;

    // ── shape: every hunting task explains itself ──

    private static List<MethodTask> HuntingTasks() =>
        Registry.MethodTasks.Where(t => t.GetDetectionSignature().Category == "Hunting" && !t.IsHidden).ToList();

    [Test]
    public void EveryHuntingTask_HasAHandWrittenDescription_AndAtLeastThreeSteps()
    {
        var tasks = HuntingTasks();
        tasks.Count.Should().BeGreaterThanOrEqualTo(45, "the 30 existing hunting tasks plus the new ones");
        foreach (var task in tasks)
        {
            var name = task.GetDetectionSignature().MethodName;
            task.Description.Length.Should().BeGreaterThan(100, $"{name} needs the hand written 2-4 sentence text, not the generated one");
            task.Description.Should().NotContain("$", $"{name}: prices change and stay out of the text");
            task.StepsForTest.Count.Should().BeGreaterThanOrEqualTo(3, name);
        }
    }

    private static MethodTask Method(string taskName) => (MethodTask)Registry.GetByName(taskName);

    private static string StepText(string taskName) => string.Join(" ", Method(taskName).StepsForTest.Select(s => s.Text));

    [TestCase("Hellwisp (Hunting)", "Burning Desert")]
    [TestCase("Yog (Hunting)", "Blazing Volcano")]
    [TestCase("Flaming Spider (Hunting)", "Blazing Volcano")]
    [TestCase("Xyz (Hunting)", "strong weapon")]
    [TestCase("Ghost (Hunting)", "Ghost Coins")]
    [TestCase("Bruiser (Hunting)", "tank-type")]
    [TestCase("Invisibug (Hunting)", "AoE")]
    [TestCase("Birries (Hunting)", "with a Huntrap")]
    [TestCase("Dung Beetle (Hunting)", "hunt Dung Beetles")]
    [TestCase("Mudworm (Hunting)", "catch Mudworms with a Huntrap")]
    public void HowTo_NoLongerStatesTheWrongFact(string taskName, string wrongFact)
    {
        StepText(taskName).Should().NotContain(wrongFact);
    }

    [Test]
    public void HowTo_StatesTheVerifiedMechanic()
    {
        StepText("Xyz (Hunting)").Should().Contain("Lasso");
        StepText("Invisibug (Hunting)").Should().Contain("Lasso");
        StepText("Birries (Hunting)").Should().Contain("Lasso or a Fishing Net");
        StepText("Bruiser (Hunting)").Should().Contain("Zealot Bruiser Hideout");
        StepText("Hellwisp (Hunting)").Should().Contain("Matriarch's Lair");
    }

    [Test]
    public void FlareHunting_NamesTheMagmaChamberPillars()
    {
        Method("Flare (Hunting)").StepsForTest.Select(s => s.Text)
            .Should().Contain(t => t.Contains("Go to Magma Chamber"));
    }

    [Test]
    public void NewTasks_AreRegistered_InTheHuntingCategory()
    {
        string[] names =
        [
            "Mangrove Honey Lathering", "Fig Honey Lathering", "Helix Honey Lathering", "Lotus Lily Pad Critters", "Tewtil (Hunting)",
            "Lotum (Hunting)", "Critter Safari Party (Loot Share)", "Critter Safari: Haunted", "Critter Safari: Forest", "Critter Safari: Cavern", "Critter Safari: Icy",
            "Torrhus Sea Creatures (Hunting)", "Sepialot and Goldolot (Hunting)", "Ant (Hunting)", "Thyst (Hunting)",
            "Soul of the Alpha (Hunting)", "Dive Ghast (Hunting)", "Cinderbat (Hunting)"
        ];
        foreach (var name in names)
        {
            var task = Registry.GetByName(name) as MethodTask;
            task.Should().NotBeNull($"{name} must be registered");
            task.GetDetectionSignature().Category.Should().Be("Hunting", name);
            task.IsHidden.Should().BeFalse(name);
        }
    }

    [Test]
    public void OnlySoulOfTheAlpha_AndTheLeftoverSafariBuckets_AreFallbacks()
    {
        var fallbacks = HuntingTasks().Where(t => t.GetDetectionSignature().Fallback).Select(t => t.GetDetectionSignature().MethodName);
        fallbacks.Should().Contain(["Soul of the Alpha (Hunting)", "Critter Safari", "Howling Spirit (Hunting)", "Spring Critters (Hunting)"]);
        foreach (var name in new[] { "Mangrove Honey Lathering", "Tewtil (Hunting)", "Critter Safari: Forest", "Thyst (Hunting)", "Cinderbat (Hunting)" })
            Method(name).GetDetectionSignature().Fallback.Should().BeFalse(name);
    }

    // ── honey lathering ──

    [Test]
    public void MangroveLathering_WinsItsShards_ForagingKeepsTheLogsOnlyPeriods()
    {
        Name("Murkwater Loch", new() { { "SHARD_MURKBAT", 1 }, { "ENCHANTED_MANGROVE_LOG", 8 }, { "MANGROVE_LOG", 200 } })
            .Should().Be("Mangrove Honey Lathering");
        Name("Murkwater Loch", new() { { "SHARD_STAG_BEETLE", 2 } }).Should().Be("Mangrove Honey Lathering");
        Name("Murkwater Loch", new() { { "ENCHANTED_MANGROVE_LOG", 20 }, { "MANGROVE_LOG", 3000 } }).Should().Be("Mangrove Foraging");
        // a long felling period with one Stag Beetle: the logs outweigh the shard
        Name("Murkwater Loch", new() { { "MANGROVE_LOG", 30_000 }, { "SHARD_STAG_BEETLE", 1 } }).Should().Be("Mangrove Foraging");
    }

    [Test]
    public void FigLathering_WinsItsShards_FigForagingKeepsTheRest()
    {
        Name("Moonglade Marsh", new() { { "SHARD_HEWVER", 1 }, { "ENCHANTED_FIG_LOG", 6 } }).Should().Be("Fig Honey Lathering");
        Name("Moonglade Marsh", new() { { "SHARD_HONEYHOG", 3 }, { "SHARD_HONEYMITE", 1 } }).Should().Be("Fig Honey Lathering");
        Name("Moonglade Marsh", new() { { "FIG_LOG", 20_000 }, { "ENCHANTED_FIG_LOG", 100 } }).Should().Be("Fig Foraging");
    }

    [Test]
    public void LatheringPots_AreCostsOfTheLatheringTasks_NotAClassificationSignal()
    {
        foreach (var name in new[] { "Mangrove Honey Lathering", "Fig Honey Lathering", "Helix Honey Lathering" })
            Method(name).ObservedCostItemsForTest
                .Should().BeEquivalentTo(["FUN_SIZED_POT_OF_HONEYCOMB", "FAMILY_SIZED_POT_OF_HONEYCOMB", "JUMBO_POT_OF_HONEYCOMB", "BEHEMOTH_POT_OF_HONEYCOMB"]);
        // a consumed pot without a shard says nothing about which tree it was applied to
        Name("Murkwater Loch", new() { { "MANGROVE_LOG", 500 }, { "JUMBO_POT_OF_HONEYCOMB", -3 } }).Should().Be("Mangrove Foraging");
    }

    [Test]
    public void HelixLathering_TakesPollendartFromHoneycomb_ButNotTheHelixForagingPeriods()
    {
        Name("Torrhus Canyon", new() { { "SHARD_HIVETHIEF", 1 }, { "ENCHANTED_HELIX_LOG", 4 } }).Should().Be("Helix Honey Lathering");
        Name("Torrhus Canyon", new() { { "SHARD_POLLENDART", 4 } }).Should().Be("Helix Honey Lathering");
        Name("Spring Path", new() { { "SHARD_PARCHED", 2 } }).Should().Be("Helix Honey Lathering");
        Name("Torrhus Canyon", new() { { "HELIX_LOG", 15_000 }, { "ENCHANTED_HELIX_LOG", 300 }, { "SHARD_POLLENDART", 2 } }).Should().Be("Helix Foraging");
        Name("Torrhus Canyon", new() { { "HONEYCOMB", 400 } }).Should().Be("Honeycomb Gathering");
        // not in the Safari: Helix Foraging lists no Safari zone either
        Name("Critter Safari", new() { { "SHARD_POLLENDART", 2 } }).Should().Be("Critter Safari");
    }

    // ── tree gift shards stay with foraging (C1) ──

    [Test]
    public void TreeGiftShards_AreForaging()
    {
        Name("Moonglade Marsh", new() { { "SHARD_CHAMELEON", 1 } }).Should().Be("Fig Foraging");
        Name("Murkwater Loch", new() { { "SHARD_HUMMINGBIRD", 2 }, { "MANGROVE_LOG", 900 } }).Should().Be("Mangrove Foraging");
        Name("Torrhus Canyon", new() { { "SHARD_GRIZZLY_BEAR", 1 } }).Should().Be("Helix Foraging");
        Name("Torrhus Heights", new() { { "HELIXIS", 1 }, { "HELIX_LOG", 400 } }).Should().Be("Helix Foraging");
        Name("Torrhus Canyon", new() { { "SHARD_QUEEN_BEE", 1 }, { "HONEYCOMB", 30 } }).Should().Be("Honeycomb Gathering");
    }

    // ── Lotus Atoll ──

    [Test]
    public void LotusLilyPadCritters_WinTheExplosionWithAShard_LotusAtollKeepsTheRest()
    {
        Name("Lotus Atoll", new() { { "SHARD_SEA_SHINE", 1 }, { "SHARD_FLIP_FLOPPER", 2 }, { "LOTUS", 200 }, { "WATER_LILY", 100 } })
            .Should().Be("Lotus Lily Pad Critters");
        Name("Lotus Atoll", new() { { "LOTUS", 200 }, { "WATER_LILY", 100 } }).Should().Be("Lotus Atoll");
        Name("Lotus Atoll", new() { { "COMMON_FROG_BRONZE", 3 }, { "LOTUS", 20 } }).Should().Be("Lotus Atoll");
    }

    [Test]
    public void Tewtil_WinsAFedTewtil_FrogFishingStaysLotusAtoll()
    {
        // two frogs caught, one Tewtil fed three: the shard outweighs the frogs and Lotus
        Name("Tewtil Tunnel", new() { { "SHARD_TEWTIL", 1 }, { "COMMON_FROG_BRONZE", -3 }, { "LOTUS", 8 } }).Should().Be("Tewtil (Hunting)");
        Name("Lotus Atoll", new() { { "SHARD_TEWTIL", 4 } }).Should().Be("Tewtil (Hunting)");
        Name("Tewtil Tunnel", new() { { "COMMON_FROG_BRONZE", 20 }, { "LOTUS", 300 } }).Should().Be("Lotus Atoll");
        Method("Tewtil (Hunting)").ObservedCostItemsForTest
            .Should().Contain(["COMMON_FROG_BRONZE", "TREE_FROG_SILVER"]).And.NotContain("COMMON_FROG_GOLD");
    }

    [Test]
    public void Lotum_WinsItsShard_WithoutStealingTheOthers()
    {
        Name("Lotus Atoll", new() { { "SHARD_LOTUM", 3 }, { "LOTUS", 24 } }).Should().Be("Lotum (Hunting)");
        Name("Lotus Atoll", new() { { "LOTUS", 24 } }).Should().Be("Lotus Atoll");
        Name("Lotus Atoll", new() { { "SHARD_LOTUM", 1 }, { "SHARD_TEWTIL", 1 } }).Should().Be("Tewtil (Hunting)");
    }

    // ── Critter Safari ──

    [TestCase("SHARD_HIDEONWALL")]
    [TestCase("SHARD_HIDEONFLOOR")]
    [TestCase("SHARD_SNOOZLE")]
    [TestCase("SHARD_WUMPA")]
    public void SafariLootShare_WinsEvenWithValuableBiomeShards(string shard)
    {
        var items = new Dictionary<string, int> { { PseudoItems.SAFARI_LOOT_SHARE, 1 }, { shard, 1000 } };
        Name("Critter Safari", items).Should().Be("Critter Safari Party (Loot Share)");
        Name("Critter Safari", items, priced: false).Should().Be("Critter Safari Party (Loot Share)");
    }

    [Test]
    public void SafariLootShare_RequiresPositiveEvidenceInTheSafari()
    {
        Name("Critter Safari", new() { { PseudoItems.SAFARI_LOOT_SHARE, 1 } }).Should().Be("Critter Safari Party (Loot Share)");
        Name("Critter Safari", new() { { PseudoItems.SAFARI_LOOT_SHARE, 0 }, { "SHARD_HIDEONWALL", 2 } }).Should().Be("Critter Safari: Haunted");
        Name("Critter Safari", new() { { PseudoItems.SAFARI_LOOT_SHARE, -1 }, { "ESSENCE_SAFARI", 40 } }).Should().Be("Critter Safari");
        Name("Critter Safari Entrance", new() { { PseudoItems.SAFARI_LOOT_SHARE, 1 } }).Should().NotBe("Critter Safari Party (Loot Share)");
    }

    [TestCase("SHARD_HIDEONWALL", "Critter Safari: Haunted")]
    [TestCase("SHARD_DOOMSPIRAL", "Critter Safari: Haunted")]
    [TestCase("SHARD_HIDEONFLOOR", "Critter Safari: Forest")]
    [TestCase("SHARD_FLUFFLING", "Critter Safari: Forest")]
    [TestCase("SHARD_SNOOZLE", "Critter Safari: Cavern")]
    [TestCase("SHARD_WUMPA", "Critter Safari: Icy")]
    public void SafariShards_ClassifyToTheirBiome(string shard, string expected)
    {
        Name("Critter Safari", new() { { shard, 2 } }).Should().Be(expected);
    }

    [Test]
    public void SafariBiomes_UseDisjointCritterLists()
    {
        var biomes = new[] { "Haunted", "Forest", "Cavern", "Icy" }
            .Select(b => Method($"Critter Safari: {b}").GetDetectionSignature().DetectionItems).ToList();
        biomes.SelectMany(b => b).Should().OnlyHaveUniqueItems();
        biomes.Should().OnlyContain(b => b.Count >= 9 && b.All(i => i.StartsWith("SHARD_")));
    }

    [Test]
    public void SafariPeriodWithSeveralBiomes_GoesToTheMostValuableBiome()
    {
        // Older periods without explicit loot-share evidence retain their biome attribution.
        var items = new Dictionary<string, int> { { "SHARD_BLOODBAT", 6 }, { "SHARD_HIDEONFLOOR", 1 } };
        Name("Critter Safari", items).Should().Be("Critter Safari: Forest");
        Name("Critter Safari", new() { { "SHARD_BLOODBAT", 6 }, { "SHARD_FLUFFLING", 1 }, { "HELIX_LOG", 100 } }).Should().Be("Critter Safari: Forest");
    }

    [Test]
    public void CritterSafari_StillCatchesWhatNoBiomeClaims()
    {
        Name("Critter Safari", new() { { "ESSENCE_SAFARI", 40 } }).Should().Be("Critter Safari");
        Name("Critter Safari", new() { { "SHARD_RAINBUG", 1 } }).Should().Be("Critter Safari");
        Name("Critter Safari", new() { { "SHARD_BUNBUN", 3 } }).Should().Be("Critter Safari");
    }

    // ── Torrhus ──

    [Test]
    public void TorrhusSeaCreatures_TakeTheFishedShards_SpringCrittersKeepTheNettedOnes()
    {
        Name("Spring Shallows", new() { { "SHARD_TORRID", 1 }, { "HELIX_LOG", 300 }, { "ENCHANTED_HELIX_LOG", 20 } })
            .Should().Be("Torrhus Sea Creatures (Hunting)");
        Name("Spring Depths", new() { { "SHARD_SPRAWL", 2 }, { "SHARD_HAGGARD", 5 } }).Should().Be("Torrhus Sea Creatures (Hunting)");
        Name("Spring Depths", new() { { "SHARD_SOLAR", 5 }, { "SHARD_EMBER", 2 } }).Should().Be("Spring Critters (Hunting)");
        Name("Spring Shallows", new() { { "HELIX_LOG", 300 }, { "ENCHANTED_HELIX_LOG", 20 } }).Should().Be("Helix Foraging");
        // the exclusive drops only add weight to a period that holds a shard; without one the fishing stays Helix Foraging
        Name("Spring Shallows", new() { { "SHARD_HAGGARD", 1 }, { "SUBLIME_SILK", 1 }, { "HELIX_LOG", 20_000 } }).Should().Be("Torrhus Sea Creatures (Hunting)");
        Name("Spring Shallows", new() { { "SUBLIME_SILK", 1 }, { "HELIX_LOG", 100 } }).Should().Be("Helix Foraging");
    }

    [Test]
    public void TorrhusSeaCreatureShardsInTheCritterSafari_AreNotTorrhusSeaCreatures()
    {
        Name("Critter Safari", new() { { "SHARD_TORRID", 1 } }).Should().Be("Critter Safari");
        Name("Spring Depths", new() { { "SHARD_TORRID", 1 } }).Should().Be("Torrhus Sea Creatures (Hunting)");
    }

    [Test]
    public void SpringCritters_NoLongerListTheMovedShards()
    {
        var detection = Method("Spring Critters (Hunting)").GetDetectionSignature().DetectionItems;
        detection.Should().BeEquivalentTo(["SHARD_SOLAR", "SHARD_EMBER", "SHARD_WATER_SNAKE", "SHARD_DUSTYBIT", "RUBY_VEILSHROOM"]);
    }

    [Test]
    public void SepialotAndGoldolot_WinTheirShards_HelixForagingOnTheSpringPathStays()
    {
        Name("Spring Depths", new() { { "SHARD_GOLDOLOT", 1 }, { "RUBY_VEILSHROOM", 3 } }).Should().Be("Sepialot and Goldolot (Hunting)");
        Name("Spring Path", new() { { "SHARD_SEPIALOT", 3 } }).Should().Be("Sepialot and Goldolot (Hunting)");
        // one Goldolot (worth more than the logs) must not take a foraging period on the Spring Path
        Name("Spring Path", new() { { "HELIX_LOG", 1000 }, { "SHARD_GOLDOLOT", 1 } }).Should().Be("Helix Foraging");
        Name("Spring Path", new() { { "ENCHANTED_HELIX_LOG", 3 }, { "SHARD_SEPIALOT", 1 } }).Should().Be("Helix Foraging");
        Name("Spring Path", new() { { "SHARD_SOLAR", 5 } }).Should().Be("Spring Critters (Hunting)");
    }

    [Test]
    public void Ant_WinsItsShards_HelixForagingKeepsTheLogs()
    {
        Name("Ant's Cave", new() { { "SHARD_ANT", 3 }, { "HELIX_LOG", 40 } }).Should().Be("Ant (Hunting)");
        Name("Ant's Cave", new() { { "SHARD_QUEEN_ANT", 1 } }).Should().Be("Ant (Hunting)");
        Name("Ant's Cave", new() { { "HELIX_LOG", 300 } }).Should().Be("Helix Foraging");
    }

    // ── Crystal Hollows, The Park, Crimson Isle ──

    [Test]
    public void Thyst_WinsWhenTheShardsOutweighTheGems_BothDirections()
    {
        Name("Jungle", new() { { "SHARD_THYST", 2 }, { "FINE_AMETHYST_GEM", 10 }, { "ROUGH_AMETHYST_GEM", 100 } }).Should().Be("Thyst (Hunting)");
        Name("Jungle", new() { { "SHARD_THYST", 1 }, { "FINE_AMETHYST_GEM", 100 } }).Should().Be("Thyst Mining");
        Name("Jungle", new() { { "FINE_AMETHYST_GEM", 30 }, { "ROUGH_AMETHYST_GEM", 300 } }).Should().Be("Thyst Mining");
    }

    [Test]
    public void SoulOfTheAlpha_AndHowlingSpirit_SplitTheOldSpiritCave()
    {
        Name("Howling Cave", new() { { "SHARD_SOUL_OF_THE_ALPHA", 20 } }).Should().Be("Soul of the Alpha (Hunting)");
        Name("Soul Cave", new() { { "SHARD_SOUL_OF_THE_ALPHA", 5 } }).Should().Be("Soul of the Alpha (Hunting)");
        Name("Howling Cave", new() { { "SHARD_HOWLING_SPIRIT", 9 } }).Should().Be("Howling Spirit (Hunting)");
        Registry.GetByName("Spirit Cave (Hunting)").Should().BeNull("renamed to Howling Spirit (Hunting)");
        Registry.GetByName("Torrhus Springs (Hunting)").Should().BeNull("renamed to Spring Critters (Hunting)");
    }

    [Test]
    public void DiveGhast_WinsItsShard_CrimsonIsleMobsKeepTheRest()
    {
        Name("The Wasteland", new() { { "SHARD_DIVE_GHAST", 1 }, { "GHAST_TEAR", 2 } }).Should().Be("Dive Ghast (Hunting)");
        Name("The Wasteland", new() { { "ROTTEN_FLESH", 50 }, { "GHAST_TEAR", 2 } }).Should().Be("Crimson Isle Mobs");
    }

    [Test]
    public void Cinderbat_IsOnTheCrimsonIsle_NotGalatea()
    {
        Name("Blazing Volcano", new() { { "SHARD_CINDER_BAT", 2 } }).Should().Be("Cinderbat (Hunting)");
        Name("Smoldering Tomb", new() { { "SHARD_CINDER_BAT", 1 } }).Should().Be("Cinderbat (Hunting)");
        Name("Blazing Volcano", new() { { "BLAZE_ROD", 60 }, { "ROTTEN_FLESH", 30 } }).Should().Be("Crimson Isle Mobs");
        Name("Dive-Ember Pass", new() { { "SHARD_CINDER_BAT", 1 } }).Should().NotBe("Cinderbat (Hunting)");
        Registry.GetByName("Cinderbat").Should().BeNull("the Galatea version stays unregistered");
    }

    // ── changes to existing tasks ──

    [Test]
    public void ZealotHunting_TakesTheSpecialZealotShard()
    {
        Name("Dragon's Nest", new() { { "SHARD_SPECIAL_ZEALOT", 2 } }).Should().Be("Zealot (Hunting)");
    }

    [Test]
    public void MinerZombieHunting_AlsoCoversTheDiamondReserve()
    {
        Name("Diamond Reserve", new() { { "SHARD_MINER_ZOMBIE", 4 } }).Should().Be("Miner Zombie (Hunting)");
        Name("Obsidian Sanctuary", new() { { "SHARD_MINER_ZOMBIE", 4 } }).Should().Be("Miner Zombie (Hunting)");
        Name("Diamond Reserve", new() { { "DIAMOND", 400 } }).Should().NotBe("Miner Zombie (Hunting)");
    }

    [Test]
    public void StonewormAndScatha_AreScathaMining_AndSycophantIsRevenantSlayer()
    {
        Name("Crystal Hollows", new() { { "SHARD_STONEWORM", 1 } }).Should().Be("Scatha Mining");
        Name("Crystal Hollows", new() { { "SHARD_THYST", 1 } }).Should().Be("Thyst (Hunting)");
        Name("Crypts", new() { { "SHARD_SYCOPHANT", 1 }, { "REVENANT_FLESH", 20 } }).Should().Be("Revenant Slayer");
    }

    [Test]
    public void Dreadwing_RatesMatchTheSite_AndTheSideShardsAreNoDetectionItems()
    {
        var dreadwing = Method("Dreadwing");
        dreadwing.FormulaDropsForTest.Should().Contain(d => d.ItemTag == "SHARD_DREADWING" && d.RatePerHour == 36);
        dreadwing.FormulaDropsForTest.Select(d => d.ItemTag).Should().Contain(["SHARD_PHANFLARE", "SHARD_PHANPYRE"]);
        dreadwing.GetDetectionSignature().DetectionItems.Should().BeEquivalentTo(["SHARD_DREADWING"]);
    }

    [Test]
    public void FishingHuntingVariants_NameTheirZoneShards()
    {
        StepText("Bayou Fishing (Hunting)").Should().Contain("Bayou Sludge");
        StepText("Crimson Fishing (Hunting)").Should().Contain("Lava Leech");
        StepText("Lotus Atoll").Should().Contain("Net Lotusfish").And.NotContain("fish you catch for Lotusfish");
    }

    // ── Hunting Trap ──

    [Test]
    public void HuntingTrap_UsesTheWikiCaptureTimesByRarity()
    {
        HuntingTrapTask.CaptureHours(HuntingTrapTask.ShardRarity.Common).Should().Be(10);
        HuntingTrapTask.CaptureHours(HuntingTrapTask.ShardRarity.Legendary).Should().Be(20);
        HuntingTrapTask.CaptureHours(HuntingTrapTask.ShardRarity.Rare).Should().BeInRange(12, 18);
    }

    [TestCase("SHARD_HELLWISP", 20.0)]
    [TestCase("SHARD_YOG", 10.0)]
    [TestCase("SHARD_XYZ", 17.5)]
    public async Task HuntingTrap_ActionsPerHourFollowTheChosenShardsCaptureTime(string shard, double hours)
    {
        var parameters = new TaskParams
        {
            TestTime = new DateTime(2026, 11, 12, 12, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            LocationProfit = [],
            CleanPrices = new() { [shard] = 1_000_000 },
            BazaarPrices = [],
            Names = []
        };

        var result = await new HuntingTrapTask().Execute(parameters);

        HuntingTrapTask.CaptureHoursOf(shard).Should().Be(hours);
        result.Breakdown.ActionsPerHour.Should().BeApproximately(1 / hours, 1e-9);
        result.Breakdown.Drops.Single().RatePerHour.Should().BeApproximately(1 / hours, 1e-9);
        result.ProfitPerHour.Should().Be((int)(1_000_000 / hours));
    }

    [Test]
    public void HuntingTrap_DropsTheWrongSpots_AndKnowsTheRightOnes()
    {
        foreach (var removed in new[] { "SHARD_KADA_KNIGHT", "SHARD_BEZAL", "SHARD_OBSIDIAN_DEFENDER", "SHARD_WITHER_SPECTER" })
            HuntingTrapTask.LocationsOf(removed).Should().BeNull($"{removed} had wrong zones");
        HuntingTrapTask.LocationsOf("SHARD_CINDER_BAT").Should().Equal("Smoldering Tomb");
        HuntingTrapTask.LocationsOf("SHARD_XYZ").Should().Equal("Mystic Marsh");
        HuntingTrapTask.LocationsOf("SHARD_HELLWISP").Should().Equal("Matriarch's Lair");
        HuntingTrapTask.LocationsOf("SHARD_BAYOU_SLUDGE").Should().Equal("Backwater Bayou");
    }

    [Test]
    public void HuntingTrap_EverySpotIsARealZoneOrIsland()
    {
        foreach (var (shard, spots) in HuntingTrapTask.Spots)
            foreach (var spot in spots)
                (SkyblockZones.IslandOf(spot) ?? SkyblockZones.IslandOfLocation(spot)).Should().NotBeNull($"{shard}: {spot}");
    }

    private static TaskParams TrapParams(Dictionary<string, long> prices) => new()
    {
        TestTime = new DateTime(2026, 11, 12, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = new Dictionary<string, Period[]>(),
        CleanPrices = prices,
        BazaarPrices = [],
        Names = new Dictionary<string, string>()
    };

    [Test]
    public async Task HuntingTrap_RanksByCoinsPerHour_NotByThePriceAlone()
    {
        // a flat 24 h would pick Torrid (419.6k); the common Zealot is captured faster and earns more per hour
        var result = await new HuntingTrapTask().Execute(TrapParams(new() { { "SHARD_TORRID", 419_600 }, { "SHARD_ZEALOT", 400_000 } }));
        result.Message.Should().Contain("SHARD_ZEALOT");
        result.ProfitPerHour.Should().Be(40_000);
        result.Breakdown.Drops.Single().RatePerHour.Should().BeApproximately(0.1, 1e-9);
    }
}
