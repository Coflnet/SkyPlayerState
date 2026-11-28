using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression (S4b): the rare pool was stored as fold time coins only, so a price fix (SCARF_STUDIES sniped at 7.9M) never
/// reached stored data, and the pool was scaled by the drift of the common items: one wrong common price on one pod
/// gave F3 8.42 vs 2.56 M/h from the same aggregate. Rare tags now store their counts and are priced live per tag.
/// </summary>
[NonParallelizable]
public class RareRepricingTests
{
    private const string Task = "Mithril Mining";
    private const string Scarf = "SCARF_STUDIES";
    private const string Relic = "MINOS_RELIC";

    [TearDown]
    public void Reset() => TaskRateOptions.FullValueRates = false;

    private static TaskAggregateService Service() => new(new Moq.Mock<global::Cassandra.ISession>().Object,
        NullLogger<TaskAggregateService>.Instance);

    private static HashSet<string> Players(int count) => Enumerable.Range(0, count).Select(i => "p" + i).ToHashSet();

    /// <summary>One hour of 3M common income (3000 MITHRIL_ORE at 1000) plus the given rare tags.</summary>
    private static BucketAggregate Aggregate(Dictionary<string, double> rareCoins, Dictionary<string, double> rareCounts)
    {
        var agg = new BucketAggregate
        {
            TaskName = Task,
            WSeconds = 3600,
            WPeriods = 2,
            ItemCounts = new() { ["MITHRIL_ORE"] = 3000 },
            ItemRefValue = 3_000_000,
            RefItemValue = 3_000_000 + rareCoins.Values.Sum(),
            RareCoins = rareCoins.Values.Sum(),
            Contributors = Players(5),
            RareContributors = Players(5)
        };
        RareRepricing.AddFresh(RareTags.Of(agg), rareCoins, rareCounts);
        return agg;
    }

    private static Dictionary<string, double> Prices(double scarf, double ore = 1000)
        => new() { ["MITHRIL_ORE"] = ore, [Scarf] = scarf, [Relic] = 20_000_000 };

    // ── storage: write, merge, trim, decay ──

    [Test]
    public void SplitRareAndCommon_ReturnsTheUnitsOfEachRareTag()
    {
        var split = TaskPeriodFolder.SplitRareAndCommon(new() { [Scarf] = 2, ["MITHRIL_ORE"] = 10 },
            tag => tag == Scarf ? 7_900_000 : 1000, _ => true);

        split.RareItemCoins[Scarf].Should().Be(15_800_000);
        split.RareItemCounts.Should().BeEquivalentTo(new Dictionary<string, double> { [Scarf] = 2 });
    }

    [Test]
    public void Counts_RoundTripFromContributionThroughRowToAggregate()
    {
        var aggregates = Service();
        aggregates.AddContribution(Task, 1, "p1", 1800, new() { ["MITHRIL_ORE"] = 10 }, 0, 3_950_000, 3_960_000,
            new() { ["MITHRIL_ORE"] = 10_000 }, null, rareItemCoins: new() { [Scarf] = 3_950_000 },
            rareItemCounts: new() { [Scarf] = 0.5 });
        aggregates.AddContribution(Task, 1, "p2", 1800, new() { ["MITHRIL_ORE"] = 10 }, 0, 7_900_000, 7_910_000,
            new() { ["MITHRIL_ORE"] = 10_000 }, null, rareItemCoins: new() { [Scarf] = 7_900_000 },
            rareItemCounts: new() { [Scarf] = 1 });
        var row = aggregates.PendingRowForTest(Task, 1);
        TaskAggregateService.MergeIntoRow(row, new TaskBucketAggregateRow
        {
            WSeconds = 3600, RareCoins = 7_900_000,
            RareItemCoins = new() { [Scarf] = 7_900_000 }, RareItemCounts = new() { [Scarf] = 1 },
            RareItemCountedCoins = new() { [Scarf] = 7_900_000 }
        }, TaskAggregateService.RowItemLimit);
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, row, 0.8);

        row.RareItemCounts.Should().BeEquivalentTo(new Dictionary<string, double> { [Scarf] = 2.5 });
        row.RareItemCountedCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [Scarf] = 19_750_000 });
        agg.RareItemCounts[Scarf].Should().BeApproximately(2.0, 1e-9);
        agg.RareItemCountedCoins[Scarf].Should().BeApproximately(19_750_000 * 0.8, 1e-3);
        agg.RareItemCoins[Scarf].Should().BeApproximately(19_750_000 * 0.8, 1e-3);
    }

    [Test]
    public void MergeIntoRow_ExistingCoinsWithoutCounts_StayAtFaceValue()
    {
        var aggregates = Service();
        aggregates.AddContribution(Task, 1, "p1", 3600, new(), 0, 30_000_000, 30_000_000, new(), null,
            rareItemCoins: new() { [Relic] = 20_000_000, [Scarf] = 10_000_000 },
            rareItemCounts: new() { [Relic] = 1, [Scarf] = 1 });
        var row = aggregates.PendingRowForTest(Task, 1);
        // the deploy day: this instance's row of the day was written before the counts existed
        var legacy = new TaskBucketAggregateRow { WSeconds = 3600, RareCoins = 20_000_000,
            RareItemCoins = new() { [Relic] = 20_000_000 }, RareItemCounts = null };

        TaskAggregateService.MergeIntoRow(row, legacy, TaskAggregateService.RowItemLimit);

        row.RareItemCoins[Relic].Should().Be(40_000_000);
        row.RareItemCounts.Should().BeEquivalentTo(new Dictionary<string, double> { [Relic] = 1, [Scarf] = 1 });
        row.RareItemCountedCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [Relic] = 20_000_000, [Scarf] = 10_000_000 });
    }

    /// <summary>
    /// Regression: after a rollback, old code (stable StatefulSet instance names) added 30M of a tag to the day row a new pod
    /// counted 10M of, leaving the units untouched. All 40M were then priced as 1 unit, about 10M.
    /// </summary>
    [Test]
    public void DayRow_CoinsAddedByCodeWithoutCounts_StayAtFaceValue()
    {
        TaskRateOptions.FullValueRates = true;
        var rolledBack = new TaskBucketAggregateRow { WSeconds = 3600, RareCoins = 40_000_000,
            RareItemCoins = new() { [Scarf] = 40_000_000 }, RareItemCounts = new() { [Scarf] = 1 },
            RareItemCountedCoins = new() { [Scarf] = 10_000_000 } };
        var aggregates = Service();
        aggregates.AddContribution(Task, 1, "p1", 3600, new(), 0, 0, 0, new(), null);
        var row = aggregates.PendingRowForTest(Task, 1);
        TaskAggregateService.MergeIntoRow(row, rolledBack, TaskAggregateService.RowItemLimit);
        var agg = new BucketAggregate { RareContributors = Players(5) };
        TaskAggregateService.AddRow(agg, row, 1);
        agg.WSeconds = 3600;

        TaskEstimator.RateParts(null, agg, Prices(scarf: 10_000_000)).ScaledRareCoins
            .Should().BeApproximately(40_000_000, 1e-6);
        TaskEstimator.RateParts(null, agg, Prices(scarf: 2_500_000)).ScaledRareCoins
            .Should().BeApproximately(30_000_000 + 2_500_000, 1e-6);
    }

    [Test]
    public void TrimRareItems_DropsTheCountsWithTheirTags()
    {
        var coins = Enumerable.Range(0, TaskAggregateService.RareItemLimit + 5).ToDictionary(i => "T" + i, i => 1e6 + i);
        var counts = coins.ToDictionary(e => e.Key, _ => 1.0);

        var kept = RareRepricing.Trimmed(new RareTags(coins, counts, new(coins)), TaskAggregateService.RareItemLimit);

        kept.Coins.Should().HaveCount(TaskAggregateService.RareItemLimit);
        kept.Counts.Keys.Should().BeEquivalentTo(kept.Coins.Keys);
        kept.CountedCoins.Keys.Should().BeEquivalentTo(kept.Coins.Keys);
        kept.Counts.Should().NotContainKey("T0");
    }

    [Test]
    public void AddRow_DecaysCountsLikeCoins_AndKeepsLegacyCoinsUncounted()
    {
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, new TaskBucketAggregateRow { RareItemCoins = new() { [Scarf] = 8_000_000 },
            RareItemCounts = new() { [Scarf] = 1 }, RareItemCountedCoins = new() { [Scarf] = 8_000_000 } }, 1);
        TaskAggregateService.AddRow(agg, new TaskBucketAggregateRow { RareItemCoins = new() { [Scarf] = 8_000_000 },
            RareItemCounts = null }, 0.5);

        agg.RareItemCoins[Scarf].Should().Be(12_000_000);
        agg.RareItemCounts[Scarf].Should().Be(1);
        agg.RareItemCountedCoins[Scarf].Should().Be(8_000_000);
    }

    [Test]
    public void MergeBuckets_SumsCountsAndCountedCoins()
    {
        var snapshot = new Dictionary<(string, byte), BucketAggregate>
        {
            [(Task, 0)] = Aggregate(new() { [Scarf] = 7_900_000 }, new() { [Scarf] = 1 }),
            [(Task, 2)] = Aggregate(new() { [Scarf] = 5_000_000, [Relic] = 20_000_000 }, new() { [Scarf] = 1 })
        };

        var merged = TaskEstimator.MergeBuckets(snapshot, Task);

        merged.RareItemCounts.Should().BeEquivalentTo(new Dictionary<string, double> { [Scarf] = 2 });
        merged.RareItemCountedCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [Scarf] = 12_900_000 });
        merged.RareItemCoins[Relic].Should().Be(20_000_000);
    }

    [Test]
    public void AddPlayerRareItems_DecaysAddsAndTrims_LegacyRowStaysUntraced()
    {
        var legacy = new TaskPlayerStatRow { RareCoins = 50_000_000 };
        TaskPeriodFolder.AddPlayerRareItems(legacy, new(new() { [Scarf] = 7_900_000 }, new() { [Scarf] = 1 }), 0.5);
        TaskPeriodFolder.AddPlayerRareItems(legacy, new(new() { [Scarf] = 7_900_000 }, new() { [Scarf] = 1 }), 0.5);

        legacy.RareItemCoins[Scarf].Should().BeApproximately(7_900_000 * 1.5, 1e-6);
        legacy.RareItemCounts[Scarf].Should().BeApproximately(1.5, 1e-9);
        legacy.RareItemCountedCoins[Scarf].Should().BeApproximately(7_900_000 * 1.5, 1e-6);
    }

    /// <summary>A player row as an old pod leaves it 7 days after a new pod wrote 40M of tags: the pool decayed, the tags not.</summary>
    private static TaskPlayerStatRow StaleTagsRow() => new()
    {
        WSeconds = 3600,
        RareCoins = 20_000_000,
        RareItemCoins = new() { [Scarf] = 40_000_000 },
        RareItemCounts = new() { [Scarf] = 2 },
        RareItemCountedCoins = new() { [Scarf] = 40_000_000 }
    };

    /// <summary>
    /// Regression: an old pod decayed a player row's pool but not its tags (it does not map them), so a 4x price drop
    /// re-priced 40M of tags against a 20M pool: -10M instead of 5M, and it never healed.
    /// </summary>
    [Test]
    public void PlayerRow_TagsBeyondThePool_AreBoundedByIt_AtRead()
    {
        TaskRateOptions.FullValueRates = true;

        TaskEstimator.PlayerRareCoins(StaleTagsRow(), Prices(scarf: 5_000_000), 1).Should().BeApproximately(5_000_000, 1e-6);
        TaskEstimator.RateFromPlayerStat(null, StaleTagsRow(), Prices(scarf: 1), 1).Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void PlayerRow_TagsBeyondThePool_AreBoundedByIt_AtFold()
    {
        var row = StaleTagsRow();

        TaskPeriodFolder.AddPlayerRareItems(row, null, 0.5);

        row.RareItemCoins[Scarf].Should().BeApproximately(10_000_000, 1e-6);
        row.RareItemCounts[Scarf].Should().BeApproximately(0.5, 1e-9);
        row.RareItemCountedCoins[Scarf].Should().BeApproximately(10_000_000, 1e-6);
    }

    [Test]
    public void PlayerRareCoins_IsNeverNegative()
    {
        TaskRateOptions.FullValueRates = true;
        var row = new TaskPlayerStatRow { RareCoins = -1, RareItemCoins = new() { [Scarf] = 1 } };

        TaskEstimator.PlayerRareCoins(row, Prices(scarf: 1), 1).Should().Be(0);
    }

    // ── read: live re-pricing per tag ──

    [TestCase(2_500_000, 2_500_000)] // a sniped 7.9M outlier fixed to 2.5M reaches the stored data
    [TestCase(10_000_000, 10_000_000)]
    [TestCase(1_000_000, 7_900_000 / 4.0)] // a price crash counts at most down to a quarter
    [TestCase(50_000_000, 7_900_000 * 4.0)] // a price spike counts at most four times
    [TestCase(0, 7_900_000)] // no live price: unknown, not worthless
    public void Reprice_CountTimesLivePrice_ClampedToTheFoldCoins(double livePrice, double expected)
        => RareRepricing.Reprice(7_900_000, 1, livePrice).Should().BeApproximately(expected, 1e-6);

    [Test]
    public void RateParts_FullValue_PricesEachCountedRareTagLive_NotByCommonDrift()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = Aggregate(new() { [Scarf] = 7_900_000, [Relic] = 20_000_000 }, new() { [Scarf] = 1, [Relic] = 1 });

        var parts = TaskEstimator.RateParts(null, agg, Prices(scarf: 2_500_000));

        parts.ScaledRareCoins.Should().BeApproximately(2_500_000 + 20_000_000, 1e-6);
        parts.Rate.Should().BeApproximately(3_000_000 + 22_500_000, 1e-6);
    }

    [Test]
    public void RateParts_FullValue_LegacyCoinsWithoutCounts_CountAtFaceValue()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = Aggregate(new() { [Scarf] = 7_900_000 }, null);
        agg.RareCoins += 4_000_000; // untraced: written before the pool was itemized

        TaskEstimator.RateParts(null, agg, Prices(scarf: 2_500_000)).ScaledRareCoins
            .Should().BeApproximately(11_900_000, 1e-6);
    }

    [Test]
    public void RateParts_FullValue_MixedTag_RepricesOnlyItsCountedPart()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = new BucketAggregate { WSeconds = 3600, RareContributors = Players(5) };
        TaskAggregateService.AddRow(agg, new TaskBucketAggregateRow { RareCoins = 7_900_000,
            RareItemCoins = new() { [Scarf] = 7_900_000 }, RareItemCounts = new() { [Scarf] = 1 },
            RareItemCountedCoins = new() { [Scarf] = 7_900_000 } }, 1);
        TaskAggregateService.AddRow(agg, new TaskBucketAggregateRow { RareCoins = 7_900_000,
            RareItemCoins = new() { [Scarf] = 7_900_000 }, RareItemCounts = null }, 1);

        TaskEstimator.RateParts(null, agg, Prices(scarf: 2_500_000)).ScaledRareCoins
            .Should().BeApproximately(7_900_000 + 2_500_000, 1e-6);
    }

    [Test]
    public void PodDivergence_OneBadCommonPrice_DoesNotScaleTheRarePool()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = Aggregate(new() { [Relic] = 20_000_000 }, new() { [Relic] = 1 });

        var good = TaskEstimator.RateParts(null, agg, Prices(scarf: 0));
        var bad = TaskEstimator.RateParts(null, agg, Prices(scarf: 0, ore: 10_000)); // one pod: a 10x wrong ore price

        bad.DriftScale.Should().Be(4); // the common items still drift ...
        bad.ScaledRareCoins.Should().Be(good.ScaledRareCoins); // ... the rare pool does not follow them
        (bad.Rate - good.Rate).Should().BeApproximately(bad.LiveItemValue - good.LiveItemValue, 1e-6);
    }

    [Test]
    public void FlagOff_RarePool_StaysDriftScaledFoldCoins()
    {
        var agg = Aggregate(new() { [Scarf] = 7_900_000 }, new() { [Scarf] = 1 });

        var parts = TaskEstimator.RateParts(null, agg, Prices(scarf: 2_500_000));

        parts.DriftScale.Should().BeApproximately(3_000_000 / 10_900_000.0, 1e-9);
        parts.ScaledRareCoins.Should().BeApproximately(7_900_000 * parts.DriftScale, 1e-6);
    }

    [Test]
    public void BuildRareDrops_FullValue_ExplainTheLiveRarePool()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = Aggregate(new() { [Scarf] = 7_900_000, [Relic] = 20_000_000 }, new() { [Scarf] = 1 });
        var prices = Prices(scarf: 2_500_000);

        var parts = TaskEstimator.RateParts(null, agg, prices);
        var drops = TaskEstimator.BuildRareDrops(null, agg, prices);

        drops.Sum(d => d.ContributionPerHour).Should().BeApproximately(parts.ScaledRareCoins * parts.RareEvidenceScale, 1e-6);
        drops.Single(d => d.ItemTag == Scarf).RatePerHour.Should().BeApproximately(1, 1e-9);
        drops.Single(d => d.ItemTag == Relic).ContributionPerHour.Should().BeApproximately(20_000_000, 1e-6);
    }

    [Test]
    public void BuildRareDrops_FullValue_PromotedUndeclaredCoinsAtFaceValue()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = Aggregate(new() { [Scarf] = 7_900_000 }, new() { [Scarf] = 1 });
        agg.PromotedUndeclaredItemCoins = new() { [Relic] = 30_000_000 };

        var drops = TaskEstimator.BuildRareDrops(null, agg, Prices(scarf: 2_500_000));

        drops.Single(d => d.ItemTag == Relic).ContributionPerHour.Should().BeApproximately(30_000_000, 1e-6);
    }

    [Test]
    public void RateFromPlayerStat_FullValue_RepricesRareTags_UntracedAtFaceValue()
    {
        TaskRateOptions.FullValueRates = true;
        var row = new TaskPlayerStatRow
        {
            WSeconds = 3600,
            RareCoins = 7_900_000 + 4_000_000, // 4M untraced (legacy or promoted undeclared)
            RareItemCoins = new() { [Scarf] = 7_900_000 },
            RareItemCounts = new() { [Scarf] = 1 },
            RareItemCountedCoins = new() { [Scarf] = 7_900_000 }
        };

        TaskEstimator.RateFromPlayerStat(null, row, Prices(scarf: 2_500_000, ore: 10_000), 0.5)
            .Should().BeApproximately((2_500_000 + 4_000_000) * 0.5 / 0.5, 1e-6);
    }

    [Test]
    public void RateFromPlayerStat_FlagOff_RarePoolStaysDriftScaled()
    {
        var row = new TaskPlayerStatRow
        {
            WSeconds = 3600,
            ItemCounts = new() { ["MITHRIL_ORE"] = 1000 },
            ItemRefValues = new() { ["MITHRIL_ORE"] = 1_000_000 },
            RefItemValue = 8_900_000,
            RareCoins = 7_900_000,
            RareItemCoins = new() { [Scarf] = 7_900_000 },
            RareItemCounts = new() { [Scarf] = 1 }
        };
        var drift = 0.25; // 1M live / 8.9M fold value, at the drift floor

        TaskEstimator.RateFromPlayerStat(null, row, Prices(scarf: 2_500_000), 1)
            .Should().BeApproximately(1_000_000 + 7_900_000 * drift, 1e-6);
    }
}
