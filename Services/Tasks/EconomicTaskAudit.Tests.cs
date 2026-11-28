using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Crafts.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public class EconomicTaskAuditTests
{
    private static TaskParams Parameters() => new()
    {
        ExtractedInfo = new() { KatStatus = new() },
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        CleanPrices = new(), BazaarPrices = new(), Names = new(), LocationProfit = new()
    };

    [Test]
    public void ViperCommunityRate_DeductsTheNpcPriceForEveryAcquiredShard()
    {
        var task = new ViperShardNpcFlipTask();
        var aggregate = new BucketAggregate { WSeconds = 600, ItemCounts = new() { ["SHARD_VIPER"] = 10 } };
        TaskEstimator.RateFromAggregate(task, aggregate, new() { ["SHARD_VIPER"] = 200_000 })
            .Should().BeApproximately(6_000_000, 0.01);
        task.ScaledFormulaCost(new() { ["SHARD_VIPER"] = -10 }, 1, _ => 200_000).Should().Be(0);
    }

    /// <summary>
    /// Regression (2026-11-28): the flip had no FormulaDrops (Execute prices it), so the public estimate showed 0 coins/h even
    /// at a bazaar price above Kiara's 100k. It is now the same margin: 10 shards per 3 minute run, and 0 like Execute when
    /// the flip would lose money.
    /// </summary>
    [TestCase(106_000, 1_200_000)]
    [TestCase(100_000, 0)]
    [TestCase(90_000, 0)]
    public void ViperFormulaRate_IsTheMarginOfTenShardsPerRun(double price, double expected)
    {
        var task = new ViperShardNpcFlipTask();

        TaskEstimator.FormulaRate(task, new() { ["SHARD_VIPER"] = price }).Should().BeApproximately(expected, 1);
    }

    /// <summary>Review regression: without a shard price the formula charged 200 shards x 100k and no revenue (-20M/h).</summary>
    [Test]
    public void ViperFormulaRate_WithoutAShardPrice_IsZero()
    {
        var task = new ViperShardNpcFlipTask();

        TaskEstimator.FormulaRate(task, new()).Should().Be(0);
        TaskEstimator.FormulaRate(task, null).Should().Be(0);
        TaskEstimator.HasPricedFormulaDrop(task, new() { ["SHARD_VIPER"] = 90_000 }).Should().BeFalse();
    }

    [Test]
    public void ViperCommunityRate_WithoutAShardPrice_ChargesNoNpcPrice()
    {
        var aggregate = new BucketAggregate { WSeconds = 600, ItemCounts = new() { ["SHARD_VIPER"] = 10 } };

        TaskEstimator.RateFromAggregate(new ViperShardNpcFlipTask(), aggregate, new()).Should().Be(0);
    }

    /// <summary>
    /// Regression (2026-11-28): the Hunting Trap estimate was 0 coins/h because Execute picks the shard and the estimator only
    /// saw the empty FormulaDrops. The estimator now prices the same pick: the best shard per capture hour.
    /// </summary>
    [Test]
    public async Task HuntingTrapFormulaRate_IsTheBestShardPerCaptureHour_LikeExecute()
    {
        var task = new HuntingTrapTask();
        var prices = new Dictionary<string, double> { ["SHARD_GIANT_ISOPOD"] = 55_141, ["SHARD_HELLWISP"] = 15_111, ["SHARD_XYZ"] = 32_949 };
        var parameters = Parameters();
        foreach (var (tag, price) in prices)
            parameters.CleanPrices[tag] = (long)price;

        var rate = TaskEstimator.FormulaRate(task, prices);
        var shown = (await task.Execute(parameters)).ProfitPerHour;

        rate.Should().BeApproximately(55_141 / HuntingTrapTask.CaptureHours(HuntingTrapTask.ShardRarity.Epic), 1);
        shown.Should().Be((int)rate);
        TaskEstimator.FormulaRate(task, new()).Should().Be(0, "no shard price, no estimate");
    }

    /// <summary>
    /// Public tasks whose formula rate is 0 at any price: their estimate is 0 coins/h whenever their community data is too thin
    /// (fewer than the minimum contributors). Pinned so a new task without a seed is a deliberate choice. Dive Ghast has no
    /// sourced rate (16 periods with 1-3 shards each in 7 days, 2026-11-28).
    /// </summary>
    [Test]
    public void TasksWithoutAFormulaSeed_AreKnown()
    {
        var unseeded = new TaskRegistry().PublicMethodTasks
            .Where(t => !t.FormulaDropsAt(new AnyPrice()).Any(d => d.RatePerHour > 0))
            .Select(t => t.GetDetectionSignature().MethodName)
            .ToList();

        unseeded.Should().BeEquivalentTo(
        [
            "Galatea Fishing", "Galatea Fishing (Hunting)", "Moonglade Sea Creatures (Hunting)", "Glacite Walker", "Chill (Hunting)",
            "Honeybuzz (Hunting)", "Spring Critters (Hunting)", "Howling Spirit (Hunting)", "Lapis Zombie (Hunting)", "Yog and Bal",
            "Barn Animals", "Farm and Barn Crops", "Pest (Hunting)", "Critter Safari Party (Loot Share)", "Critter Safari: Haunted",
            "Critter Safari: Forest", "Critter Safari: Cavern", "Critter Safari: Icy", "Mangrove Honey Lathering", "Fig Honey Lathering",
            "Tewtil (Hunting)", "Torrhus Sea Creatures (Hunting)", "Sepialot and Goldolot (Hunting)", "Ant (Hunting)",
            "Dive Ghast (Hunting)", "Pest", "Arachne", "Kada Knight", "Red Sand Mining", "Quartz Mining", "Sand Mining", "Glacial Cave"
        ]);
    }

    /// <summary>A high price for every tag, so a price dependent formula (Hunting Trap, a profitable Viper flip) has drops.</summary>
    private sealed class AnyPrice : IReadOnlyDictionary<string, double>
    {
        private const double Price = 1e9;
        public double this[string key] => Price;
        public IEnumerable<string> Keys => [];
        public IEnumerable<double> Values => [];
        public int Count => 0;
        public bool ContainsKey(string key) => true;
        public bool TryGetValue(string key, out double value) { value = Price; return true; }
        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, double>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public void HubCommunityCenter_LuminoFiberDoesNotProveAFactionQuest()
    {
        var classifier = new TaskClassifier(new TaskRegistry());
        classifier.Classify("Community Center", new Dictionary<string, int> { ["LUMINO_FIBER"] = 1 }, 10)
            ?.TaskName.Should().NotBe("Daily Crimson Quests");
        classifier.Classify("Scarleton", new Dictionary<string, int> { ["LUMINO_FIBER"] = 1 }, 10)
            ?.TaskName.Should().Be("Daily Crimson Quests");
    }

    [Test]
    public void Experimentation_RewardsAreOnThePrivateIsland()
    {
        var classifier = new TaskClassifier(new TaskRegistry());
        classifier.Classify("Your Island", new() { ["GRAND_EXP_BOTTLE"] = 3 }, 60)?.TaskName
            .Should().Be("Experimentation Table");
        classifier.Classify("Village", new() { ["GRAND_EXP_BOTTLE"] = 3 }, 60)?.TaskName
            .Should().NotBe("Experimentation Table");
        new ExperimentationTableTask().EffectiveWarpCommandForTest.Should().Be("/is");
    }

    [Test]
    public void FactionQuestDetection_DoesNotClaimAshfangOrLavaFishing()
    {
        var task = new DailyCrimsonQuestsTask();
        var parameters = Parameters();
        foreach (var zone in new[] { "Ruins of Ashfang", "Magma Chamber", "Scarleton" })
            parameters.LocationProfit[zone] = [new Period { Location = zone, ItemsCollected = new() { ["LUMINO_FIBER"] = 1 } }];
        task.FindMatchingPeriodsForAggregation(parameters).Should().ContainSingle(p => p.Location == "Scarleton");
    }

    [Test]
    public void Arachne_CommonSoulStringIsSufficientEvidence()
    {
        new TaskClassifier(new TaskRegistry()).Classify("Arachne's Sanctuary", new() { ["SOUL_STRING"] = 30 }, 60)?.TaskName
            .Should().Be("Arachne");
        new CorleoneTask().WikiUrlForTest.Should().EndWith("/Boss_Corleone");
    }

    [TestCase(0, 0, 0, 564000)]
    [TestCase(500, 75, 25, 6138000)]
    public void Composter_AccountsForFullCycleCostsAndUpgrades(int speed, int multiDrop, int reduction, int expected)
    {
        var service = new ComposterService();
        var (_, _, profit) = service.GetBestFlip(new() { ["WHEAT"] = 1, ["VOLTA"] = 10000 }, 100000,
            new Composter { SpeedPercentIncrease = speed, MultiDropChance = multiDrop, CostReductionPercent = reduction });
        profit.Should().BeApproximately(expected, 1);
    }

    [Test]
    public async Task Kat_AttributeReducesDurationAndTheLastCandidateIsUsable()
    {
        var parameters = Parameters();
        parameters.ExtractedInfo.AttributeLevel = new() { ["Kat's Favorite"] = 10 };
        parameters.Cache[typeof(List<KatTask.FlipData>)] = new()
        {
            LastUpdated = DateTime.UtcNow,
            Data = new List<KatTask.FlipData>
            {
                new(new KatUpgradeResult { CoreData = new() { Name = "Bee", Hours = 10 }, Profit = 100000 },
                    new PriceSumary { Med = 100000, Volume = 2 })
            }
        };
        var result = await new KatTask().Execute(parameters);
        result.ProfitPerHour.Should().BeInRange(10659, 10660); // 100000 * .97 / (9 hours + .1 setup)
    }

    [Test]
    public async Task Kat_EmptyMarketDoesNotThrow()
    {
        var parameters = Parameters();
        parameters.Cache[typeof(List<KatTask.FlipData>)] = new() { LastUpdated = DateTime.UtcNow, Data = new List<KatTask.FlipData>() };
        (await new KatTask().Execute(parameters)).Message.Should().Contain("No profitable Kat upgrades");
    }

    [TestCase(0)]
    [TestCase(60)]
    public async Task IslandTracker_HandlesNoItemsAndZeroDuration(int seconds)
    {
        var parameters = Parameters();
        var start = DateTime.UtcNow;
        parameters.LocationProfit["Backwater Bayou"] = [new Period
        {
            Location = "Backwater Bayou", StartTime = start, EndTime = start.AddSeconds(seconds), Profit = 100,
            ItemsCollected = new()
        }];
        var result = await new BackwaterBayouTask().Execute(parameters);
        result.ProfitPerHour.Should().Be(seconds == 0 ? 0 : 6000);
    }
    [Test]
    public void IslandTrackers_CoverTheirCanonicalIslandAndDecemberOpening()
    {
        new CrimsonIsleTask().CoversLocation("Magma Chamber").Should().BeTrue();
        new CrimsonIsleTask().CoversLocation("The Wasteland").Should().BeTrue();
        new CrimsonIsleTask().CoversLocation("Community Center").Should().BeFalse();
        new TheEndTask().CoversLocation("The End").Should().BeTrue();
        new JerryTask().IsPossibleAt(new DateTime(2026, 12, 1)).Should().BeTrue();
    }
    [Test]
    public void Barn_TracksMelonsAndPumpkinsButDoesNotInventOtherAnimals()
    {
        var classifier = new TaskClassifier(new TaskRegistry());
        foreach (var crop in new[] { "MELON", "PUMPKIN" })
            classifier.Classify("The Barn", new() { [crop] = 10 }, 60)?.TaskName.Should().Be("Farm and Barn Crops");
        classifier.Classify("The Barn", new() { ["RABBIT"] = 1 }, 60)?.TaskName.Should().NotBe("Barn Animals");
    }
    [Test]
    public async Task DailyRewards_DoNotProveAllQuestOrShopStockWasConsumed()
    {
        var parameters = Parameters();
        parameters.TestTime = DateTime.UtcNow;
        parameters.CleanPrices["SHARD_VIPER"] = 200000;
        foreach (var (zone, item) in new[] { ("Scarleton", "LUMINO_FIBER"), ("North Reaches", "SHARD_VIPER") })
            parameters.LocationProfit[zone] = [new Period { Location = zone, StartTime = parameters.TestTime.AddMinutes(-2),
                EndTime = parameters.TestTime.AddMinutes(-1), Profit = 100000, ItemsCollected = new() { [item] = 1 } }];
        foreach (var task in new ProfitTask[] { new DailyCrimsonQuestsTask(), new ViperShardNpcFlipTask() })
        {
            var result = await task.Execute(parameters);
            result.IsAccessible.Should().BeTrue();
            result.NextAvailableAt.Should().BeNull();
        }
    }
}
