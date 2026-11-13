using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearModelServiceTests
{
    private static GearModelService Service(InMemoryGearReader reader, int gearCacheSize = GearModelService.DefaultGearCacheSize)
        => new(reader, new Tasks.TaskRegistry(), null!, NullLogger<GearModelService>.Instance, GearModelOptions.Default, gearCacheSize);

    private static MeasuredTaskGear Compute(params GearObservationInput[] inputs) => GearModelService.Compute(Wheat, inputs, Now);

    /// <summary>Player i's own gear (a unique gem, so a fingerprint of its own) with tool fortune 100 + 20 i.</summary>
    private static ObservedGear PlayerGear(int i)
    {
        var gear = TestGear(harvesting: i % 3 + 4, toolFortune: 100 + 20 * i);
        gear.Slots[GearSnapshot.Tool].Gems["PERIDOT_0"] = $"TEST_GEM_{i}";
        return gear;
    }

    /// <summary>Ten players with rising tool fortune, rates and a rare drop each, plus paired harvesting changes of two of them.</summary>
    private static List<GearObservationInput> TenPlayers()
    {
        var inputs = Enumerable.Range(1, 10)
            .Select(i => Obs(i, PlayerGear(i), 2, 1000 + 300 * i, rarePerHour: 50 * i, stats: new() { ["Farming Fortune"] = 1000 + i })).ToList();
        inputs.Add(Obs(1, TestGear(harvesting: 5), 1.5, 1000, at: Now.AddDays(-3)));
        inputs.Add(Obs(1, TestGear(harvesting: 6), 1.5, 1100, at: Now.AddDays(-1)));
        inputs.Add(Obs(2, TestGear(harvesting: 5), 1.5, 1000, at: Now.AddDays(-3)));
        inputs.Add(Obs(2, TestGear(harvesting: 6), 1.5, 1200, at: Now.AddDays(-1)));
        return inputs;
    }

    [Test]
    public void FishingCatalog_ExcludesMiningArmor_WithoutDroppingSetBonusesOrRenamedItems()
    {
        // Production MAGMA_LORD_GAUNTLET is a necklace despite its internal id.
        var gear = new ObservedGear
        {
            Slots = new()
            {
                [GearSnapshot.Tool] = new() { Tag = "HELLFIRE_ROD", ItemType = "FISHING ROD", Stats = new() { ["Fishing Speed"] = 75 } },
                [GearSnapshot.Helmet] = new() { Tag = "DIVAN_HELMET", ItemType = "HELMET", Stats = new() { ["Mining Speed"] = 100 } },
                [GearSnapshot.Chestplate] = new() { Tag = "MAGMA_LORD_CHESTPLATE", ItemType = "CHESTPLATE" },
                [GearSnapshot.Leggings] = new() { Tag = "MAGMA_LORD_LEGGINGS", ItemType = "LEGGINGS" },
                [GearSnapshot.Necklace] = new() { Tag = "MAGMA_LORD_GAUNTLET", ItemType = "NECKLACE", Stats = new() { ["Sea Creature Chance"] = 2.5 } },
                [GearSnapshot.Gloves] = new() { Tag = "GILLSPLASH_GLOVES", ItemType = "GLOVES", Stats = new() { ["Fishing Speed"] = 5 } }
            }
        };
        var result = GearModelService.Compute("Flaming Worm Fishing",
            Enumerable.Range(1, 3).Select(player => Obs(player, gear, 1, 1000, category: GearModifierTable.FishingLava)).ToList(), Now);

        result.Pieces.Select(p => p.Slot).Should().BeEquivalentTo(new[] { GearSnapshot.Tool, GearSnapshot.Chestplate, GearSnapshot.Leggings, GearSnapshot.Necklace, GearSnapshot.Gloves });
        result.Recommended!.Pieces.Keys.Should().BeEquivalentTo(new[] { GearSnapshot.Tool, GearSnapshot.Chestplate, GearSnapshot.Leggings, GearSnapshot.Necklace, GearSnapshot.Gloves });
    }

    [TestCase("Amber Mining", GearModifierTable.MiningGem, "BAL", true)]
    [TestCase("Sapphire Mining", GearModifierTable.MiningGem, "BAL", true)]
    [TestCase("Topaz Mining", GearModifierTable.MiningGem, "BAL", true)]
    [TestCase("Iron Mining", GearModifierTable.MiningOre, "BAL", true)]
    [TestCase("Cactus Farming", GearModifierTable.Farming, "HEDGEHOG", true)]
    [TestCase("Tungsten Mining", GearModifierTable.MiningOre, "MAMMOTH", false)]
    [TestCase("Fig Foraging", GearModifierTable.Foraging, "GOLDEN_DRAGON", false)]
    [TestCase("Fig Foraging", GearModifierTable.Foraging, "OCELOT", false)]
    [TestCase("Obsidian Mining", GearModifierTable.MiningOre, "ENDERMAN", false)]
    public void PetCatalog_KeepsRelevantFortune_AndFiltersUnrelatedPerks(string task, string category, string petType, bool kept)
    {
        var gear = new ObservedGear { Pet = new GearPet { Type = petType, Tier = "LEGENDARY", Level = 100 } };
        var inputs = Enumerable.Range(1, 3).Select(player => Obs(player, gear, 1, 1000, category: category)).ToList();

        var result = GearModelService.Compute(task, inputs, Now);

        result.Pieces.Any(p => p.Slot == GearPieceCatalog.PetSlot).Should().Be(kept);
        result.Pieces.Should().OnlyContain(p => p.ScoreContribution > 0);
    }

    [Test]
    public void Compute_EveryPartOfTheModel_FromTenPlayers()
    {
        var result = GearModelService.Compute(Wheat, TenPlayers(), Now);

        result.Distribution!.Players.Should().Be(10);
        result.LateGame.Should().NotBeNull();
        result.Curve.Should().NotBeNull();
        result.Curve!.SlopePer10Pct.Should().BePositive("the rate rises with the tool fortune");
        result.Pieces.Should().Contain(p => p.Slot == GearSnapshot.Tool).And.Contain(p => p.Slot == GearPieceCatalog.PetSlot);
        result.Recommended!.Basis.Should().Be(GearBuildRecommender.BasisCohort);
        result.Recommended.CoinsPerHour.Should().Be(result.LateGame!.CoinsPerHour, "the build stands for the late game cohort");
        result.Effects.Should().ContainSingle(e => e.Changes.Single().Component == "tool.enchants.harvesting");
        result.Formula.EffectCount.Should().BeGreaterThan(0);
    }

    private static ObservedGear WheatGear(int harvesting, int cultivating, long cultivated, double fortune)
    {
        var gear = TestGear(harvesting: harvesting, toolFortune: fortune);
        var tool = gear.Slots[GearSnapshot.Tool];
        tool.Enchants["cultivating"] = cultivating;
        tool.Counters["farmed_cultivating"] = cultivated;
        return gear;
    }

    /// <summary>
    /// Regression (live Wheat Farming): the catalog stripped the top players' own upgrades (their enchant levels and
    /// counters differ), so the recommended build scored below the late game threshold and got the curve's low rate.
    /// </summary>
    [Test]
    public void Wheat_TheTopPlayersUpgradesAreKept_TheBuildGetsTheCohortRate()
    {
        var inputs = Enumerable.Range(1, 8).Select(i => Obs(i, WheatGear(3, 1, 1_000, 100), 2, 1000 * i)).ToList();
        inputs.Add(Obs(9, WheatGear(6, 10, 100_000_000, 300), 3, 20_000));
        inputs.Add(Obs(10, WheatGear(5, 9, 50_000_000, 280), 3, 22_000));

        // the two top players as the cohort, unshrunk: the test covers the catalog, the cohort rules have their own tests
        var twoPlayerCohort = GearModelOptions.Default with { LateGameMinPlayers = 2, LateGameMinHours = 1, LateGamePriorHours = 0 };
        var result = GearModelService.Compute(Wheat, inputs, Now, twoPlayerCohort);

        var top = result.Pieces.Where(p => p.Slot == GearSnapshot.Tool).OrderByDescending(p => p.ScoreContribution).First();
        top.Item!.Enchants.Should().BeEquivalentTo(new Dictionary<string, int> { ["harvesting"] = 5, ["cultivating"] = 9 },
            "the lower of the two top players' levels");
        top.Item.Counters["farmed_cultivating"].Should().Be(50_000_000);
        top.Item.Stats["Farming Fortune"].Should().Be(280);
        var endgame = result.Recommended!;
        endgame.Pieces[GearSnapshot.Tool].Should().Be(top.Id);
        endgame.Score.Should().BeGreaterThanOrEqualTo(result.LateGame!.ScoreFrom, "tool 280 + Fermento 75 + Elephant 150");
        endgame.Score.Should().Be(500, "505, 2 significant digits");
        endgame.CoinsPerHour.Should().Be(result.LateGame.CoinsPerHour).And.Be(21_000);
        endgame.Basis.Should().Be(GearBuildRecommender.BasisCohort);
        endgame.Extrapolated.Should().BeFalse();
    }

    [Test]
    public void Privacy_NoPlayerKeyOrFingerprintInThePublicJson()
    {
        var inputs = TenPlayers();
        var result = GearModelService.Compute(Wheat, inputs, Now);
        var model = new TaskGearModel
        {
            Distribution = result.Distribution, LateGame = result.LateGame, Curve = result.Curve, Pieces = result.Pieces,
            RecommendedEndgame = result.Recommended, ModifierEffects = result.Effects
        };

        var json = JsonSerializer.Serialize(model);

        foreach (var input in inputs)
        {
            json.Should().NotContain(input.Observation.PlayerKey);
            json.Should().NotContain(input.Observation.GearFingerprint);
        }
        json.Should().NotContainEquivalentOf("playerKey").And.NotContainEquivalentOf("fingerprint");
    }

    [Test]
    public void OnePlayer_PublishesNothingButTheFormula()
    {
        var result = Compute(Obs(1, TestGear(), 3, 1000), Obs(1, TestGear(harvesting: 5), 3, 1000));

        result.Distribution.Should().BeNull();
        result.LateGame.Should().BeNull();
        result.Curve.Should().BeNull();
        result.Pieces.Should().BeEmpty();
        result.Recommended.Should().BeNull();
        result.Effects.Should().BeEmpty();
    }

    [Test]
    public void Compute_OtherCategoriesObservations_AreLeftOut()
    {
        Compute(Obs(1, TestGear(), 1, 1000), Obs(2, TestGear(), 1, 1000, category: GearModifierTable.Other)).Distribution.Should().BeNull();
    }

    [Test]
    public async Task Service_LoadsThirteenWeeks_JoinsTheGear_AndKeepsTheResult()
    {
        var reader = new InMemoryGearReader();
        reader.Add(Obs(1, ScoreGear(100), 1, 1000, at: Now.AddDays(-80)), Obs(2, ScoreGear(200), 1, 3000, at: Now.AddDays(-1)),
            Obs(3, ScoreGear(300), 1, 99_000, at: Now.AddDays(-100)));
        var orphan = Obs(4, ScoreGear(400), 1, 50_000, at: Now.AddDays(-2));
        reader.Rows.Add((Wheat, orphan.Observation)); // its gear row expired
        var service = Service(reader);

        var result = await service.Refresh(Wheat, Now);

        reader.WeeksRead.Should().HaveCount(GearModelService.WindowWeeks);
        result.Distribution!.Players.Should().Be(2, "the 100 day old observation is outside the window, the orphan has no gear");
        result.Distribution.ScoreP50.Should().Be(150);
        service.GetMeasured("wheat farming").Should().BeSameAs(result);

        await service.Refresh(Wheat, Now);
        reader.GearReads.Should().Be(2, "known fingerprints are cached, only the orphan is asked for again");
    }

    /// <summary>Regression: a full gear cache was cleared after the task's cached fingerprints were chosen, dropping their observations.</summary>
    [Test]
    public async Task Service_FullGearCache_KeepsTheTasksGear()
    {
        var reader = new InMemoryGearReader();
        reader.Add(Obs(1, ScoreGear(100), 1, 1000), Obs(2, ScoreGear(200), 1, 1000));
        var service = Service(reader, gearCacheSize: 2);
        (await service.Refresh(Wheat, Now)).Distribution.Should().NotBeNull();

        reader.Add(Obs(3, ScoreGear(300), 1, 1000));
        var result = await service.Refresh(Wheat, Now);

        result.Distribution!.ScoreP50.Should().Be(200, "the two cached fingerprints and the new one: the median of 100, 200 and 300");
    }

    [Test]
    public void Distribution_BelowFivePlayers_ScoresRoundedAndBuildsBanded()
    {
        var published = Compute(Obs(1, ScoreGear(1234), 1, 1000), Obs(2, ScoreGear(1456), 1, 1000), Obs(2, ScoreGear(1500), 1, 1000)).Distribution!;

        published.Rounded.Should().BeTrue();
        new[] { published.ScoreP10, published.ScoreP25, published.ScoreP75, published.ScoreP90 }.Should().OnlyContain(p => p == published.ScoreP50,
            "below 5 players only the median is published");
        published.ScoreP50.Should().Be(1400, "one vote per player: 1419 between player 1's 1234 and player 2's builds, 2 significant digits");
        published.PlayerBuilds.Should().Be(2, "3 builds banded");
        published.Hours.Should().Be(2, "3 hours banded");
    }

    [Test]
    public void UnboundTaskThatIsNotFought_NoModel_ModifierEffectsStay()
    {
        var inputs = new[]
        {
            Obs(1, TestGear(tool: "TEST_TOOL_A"), 1.5, 1000, at: Now.AddDays(-3), category: GearModifierTable.Other),
            Obs(1, TestGear(tool: "TEST_TOOL_B"), 1.5, 1100, at: Now.AddDays(-1), category: GearModifierTable.Other),
            Obs(2, TestGear(tool: "TEST_TOOL_A"), 1.5, 1000, at: Now.AddDays(-3), category: GearModifierTable.Other),
            Obs(2, TestGear(tool: "TEST_TOOL_B"), 1.5, 1200, at: Now.AddDays(-1), category: GearModifierTable.Other)
        };

        var result = GearModelService.Compute("Not A Task", inputs, Now);

        result.Formula.Kind.Should().Be(GearScoreFormula.None);
        result.Distribution.Should().BeNull();
        result.Pieces.Should().BeEmpty();
        result.Recommended.Should().BeNull();
        result.Effects.Should().NotBeEmpty("modifier effects do not need a score");
    }

    /// <summary>Regression: a store timeout (an OperationCanceledException) escaped the refresh and ended the hourly loop.</summary>
    [Test]
    public async Task RefreshAll_AStoreTimeout_IsAFailedTask_NotTheEndOfTheCycle()
    {
        var reader = new TimingOutReader();
        var service = new GearModelService(reader, new Tasks.TaskRegistry(), null!, NullLogger<GearModelService>.Instance);

        await service.RefreshAll(System.Threading.CancellationToken.None);

        reader.Reads.Should().Be(5, "the cycle stops after 5 failed tasks in a row, it does not throw");
    }

    private sealed class TimingOutReader : IGearObservationReader
    {
        public int Reads { get; private set; }

        public Task<List<GearObservationRow>> ReadWeek(string taskName, System.DateTime weekStart, int limit, System.Threading.CancellationToken cancellationToken)
        {
            Reads++;
            throw new TaskCanceledException("read timeout");
        }

        public Task<Dictionary<string, ObservedGear>> ReadGear(IReadOnlyCollection<string> fingerprints, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, ObservedGear>());
    }

    [Test]
    public void ModifierEffects_PlayersBandedBelowFive()
    {
        var inputs = Enumerable.Range(1, 3).SelectMany(i => new[]
        {
            Obs(i, TestGear(harvesting: 5), 1.5, 1000, at: Now.AddDays(-3)), Obs(i, TestGear(harvesting: 6), 1.5, 1100, at: Now.AddDays(-1))
        }).ToArray();

        var effect = Compute(inputs).Effects.Single();

        effect.Players.Should().Be(2, "3 players banded");
        effect.Rounded.Should().BeTrue();
    }

    [TestCase(12_345, 12_000)]
    [TestCase(17_900.5, 18_000)]
    [TestCase(-987, -990)]
    [TestCase(0.01234, 0.012)]
    [TestCase(0, 0)]
    public void RoundSignificant_TwoDigits(double value, double expected)
    {
        GearModelPublisher.RoundSignificant(value, 2).Should().Be(expected);
    }

    [TestCase(3.7, 2)]
    [TestCase(0.8, 0.5)]
    [TestCase(12, 10)]
    [TestCase(57, 50)]
    [TestCase(0, 0)]
    public void BandLowerBound_OneTwoFive(double value, double expected)
    {
        GearModelPublisher.BandLowerBound(value).Should().Be(expected);
    }

    [Test]
    public void StatMedians_OnlyBaseGearObservations_AndOnlyStatsOfTwoPlayers()
    {
        var gear = TestGear();
        var inputs = new[]
        {
            Obs(1, gear, 1, 1000, stats: new() { ["Farming Fortune"] = 1000, ["Wheat Fortune"] = 50 }),
            Obs(2, gear, 1, 1000, stats: new() { ["Farming Fortune"] = 1200 }),
            Obs(3, gear, 1, 1000)
        };

        GearSharedItems.StatMedians(inputs, 2).Should().BeEquivalentTo(new Dictionary<string, double> { ["Farming Fortune"] = 1100 },
            "one player's Wheat Fortune is not published");
    }

    [TestCase(5, 0)]
    [TestCase(10, 0)]
    [TestCase(15, 12.5)]
    [TestCase(20, 50, Description = "ties: the last of the equal values")]
    [TestCase(35, 87.5)]
    [TestCase(40, 100)]
    [TestCase(99, 100)]
    public void PercentileRank_InverseOfPercentile(double value, double expected)
    {
        GearStatistics.PercentileRank([10, 20, 20, 30, 40], value).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void PercentileRank_RoundTripsThePercentile()
    {
        var sorted = Enumerable.Range(1, 1000).Select(i => i * 1.5).ToList();

        foreach (var p in new[] { 0.1, 0.25, 0.5, 0.9 })
            GearStatistics.PercentileRank(sorted, GearStatistics.Percentile(sorted, p)).Should().BeApproximately(p * 100, 1e-9);
    }

    [Test]
    public void Confidence_HighNeedsFivePlayersAndLowVariation()
    {
        GearStatistics.Confidence(5, 0.2).Should().Be(GearStatistics.High);
        GearStatistics.Confidence(5, 0.5).Should().Be(GearStatistics.Medium);
        GearStatistics.Confidence(4, 0.1).Should().Be(GearStatistics.Low);
    }
}
