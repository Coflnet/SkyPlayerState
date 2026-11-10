using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: with full value rates the rare pool counted in full however thin its evidence, so one outlier set a
/// task's rate (Kuudra T5: 6 hours, one drop, 116M/h). The pool now counts in full from 3 distinct players who found rare
/// drops, however few hours they played, and n / 3 of it below that. It is not a per player cap.
/// </summary>
[NonParallelizable]
public class RarePoolEvidenceTests
{
    private const string Task = "Mithril Mining";
    private static readonly Dictionary<string, double> Prices = new() { ["MITHRIL_ORE"] = 1000, ["TITANIUM_ORE"] = 5000 };

    [TearDown]
    public void Reset() => TaskRateOptions.FullValueRates = false;

    private static HashSet<string> Players(int count) => Enumerable.Range(0, count).Select(i => "p" + i).ToHashSet();

    /// <summary>Common income of 3M/h plus one rare pool of <paramref name="rareCoins"/>.</summary>
    private static BucketAggregate Aggregate(double hours, double rareCoins, int rareContributors, int contributors = 14) => new()
    {
        TaskName = Task,
        WSeconds = hours * 3600,
        WPeriods = hours * 2,
        ItemCounts = new() { ["MITHRIL_ORE"] = hours * 3000 },
        ItemRefValue = hours * 3000 * 1000,
        RefItemValue = hours * 3000 * 1000 + rareCoins,
        RareCoins = rareCoins,
        Contributors = Players(contributors),
        RareContributors = Players(rareContributors)
    };

    private static double RarePerHour(TaskEstimator.AggregateRateParts parts, double hours)
        => parts.ScaledRareCoins * parts.RareEvidenceScale / hours;

    [Test]
    public void SingleOutlier_OneHundredMillionPerHour_CountsAThird()
    {
        TaskRateOptions.FullValueRates = true;
        // Kuudra T5 style: 14 players over 6 hours, one of them found a 600M drop: 100M/h if counted in full
        var parts = TaskEstimator.RateParts(null, Aggregate(6, 600_000_000, 1), Prices);

        RarePerHour(parts, 6).Should().BeApproximately(100_000_000 / 3.0, 1);
        parts.Rate.Should().BeApproximately(3_000_000 + 100_000_000 / 3.0, 1);
    }

    [Test]
    public void ManyRarePlayers_FewHours_CountInFull()
    {
        TaskRateOptions.FullValueRates = true;
        // Kuudra T2 style: 87 players, 5.5 hours, 20 of them found tentacles
        var parts = TaskEstimator.RateParts(null, Aggregate(5.5, 55_000_000, 20, 87), Prices);

        parts.RareEvidenceScale.Should().Be(1);
        RarePerHour(parts, 5.5).Should().BeApproximately(10_000_000, 1e-3);
    }

    [Test]
    public void WellSupportedTask_RareDropsCountInFull()
    {
        TaskRateOptions.FullValueRates = true;
        // 50 hours, five players found rare drops: 10M/h of rare loot
        var parts = TaskEstimator.RateParts(null, Aggregate(50, 500_000_000, 5), Prices);

        parts.RareEvidenceScale.Should().Be(1);
        parts.Rate.Should().BeApproximately(3_000_000 + 10_000_000, 1e-3);
    }

    [TestCase(0, 0.0)]
    [TestCase(1, 1 / 3.0)]
    [TestCase(2, 2 / 3.0)]
    [TestCase(3, 1.0)]
    [TestCase(40, 1.0)]
    public void Scale_IsTheShareOfThreeRarePlayers(int rareContributors, double expected)
    {
        TaskRateOptions.FullValueRates = true;

        TaskEstimator.RareEvidenceScale(rareContributors).Should().BeApproximately(expected, 1e-12);
    }

    [Test]
    public void SwitchOff_RarePoolIsNotGated()
    {
        var parts = TaskEstimator.RateParts(null, Aggregate(6, 600_000_000, 1), Prices);

        parts.RareEvidenceScale.Should().Be(1, "switch off keeps today's public numbers");
    }

    [Test]
    public async Task SingleOutlier_PublishedCommunityRate_StaysFarBelowTheOutlier()
    {
        TaskRateOptions.FullValueRates = true;
        var thin = await PublishedRate(Aggregate(6, 600_000_000, 1));
        var supported = await PublishedRate(Aggregate(50, 500_000_000, 5));

        // 6 h of 3M/h common + a third of 100M/h rare, blended with 2 pseudo hours of the 4M/h formula; ungated it was 78M/h
        thin.Should().BeApproximately((6 * (3e6 + 100e6 / 3) + 2 * 4e6) / 8, 1e3).And.BeLessThan(30_000_000);
        // 50 h (all counted) blended with 2 pseudo hours of the 4M/h formula
        supported.Should().BeApproximately((50 * 13e6 + 2 * 4e6) / 52, 1e3);
    }

    private static async Task<double> PublishedRate(BucketAggregate global)
    {
        var registry = new TaskRegistry();
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.SetSnapshotForTest(new() { [(Task, (byte)1)] = global });
        var estimator = new TaskEstimator(registry, aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance), NullLogger<TaskEstimator>.Instance);
        var estimates = await estimator.EstimateCommunity(Prices, default);
        return estimates.Single(e => e.TaskName == Task).CoinsPerHour;
    }

    [Test]
    public void Contribution_WithRareCoins_RecordsItsPlayer_ThroughFlushAndMerge()
    {
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.AddContribution(Task, 1, "lucky", 3600, new() { ["MITHRIL_ORE"] = 3000 }, 0, 50_000_000, 53_000_000, null, null);
        aggregates.AddContribution(Task, 1, "plain", 3600, new() { ["MITHRIL_ORE"] = 3000 }, 0, 0, 3_000_000, null, null);
        var row = aggregates.PendingRowForTest(Task, 1);
        var earlier = new TaskBucketAggregateRow { WSeconds = 3600, Players = ["old"], RarePlayers = ["older"] };

        TaskAggregateService.MergeIntoRow(row, earlier, TaskAggregateService.RowItemLimit);
        var day = new BucketAggregate();
        TaskAggregateService.AddRow(day, row, 1);
        TaskAggregateService.AddRow(day, new TaskBucketAggregateRow { WSeconds = 3600, Players = ["legacy"], RarePlayers = null }, 1);
        var merged = TaskEstimator.MergeBuckets(new() { [(Task, (byte)1)] = day, [(Task, (byte)2)] = Aggregate(1, 1, 1) }, Task);

        row.RarePlayers.Should().BeEquivalentTo(new[] { "lucky", "older" });
        day.RareContributors.Should().BeEquivalentTo(new[] { "lucky", "older" }, "a row from before the column adds none");
        merged.RareContributors.Should().BeEquivalentTo(new[] { "lucky", "older", "p0" });
    }
}
