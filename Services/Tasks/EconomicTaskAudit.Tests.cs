using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
