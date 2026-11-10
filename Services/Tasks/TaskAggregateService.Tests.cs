using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Row trimming by value: cheap bulk blocks must not push out the valuable low count drops.</summary>
[NonParallelizable]
public class TaskAggregateTrimTests
{
    [TearDown]
    public void ResetOverrides() => CoinValueRegistry.ResetOverridesForTest();

    private const double RefinedMineralPrice = 98_000;

    // RowItemLimit cheap blocks mined by the thousand, each far more often than the 8 Refined Minerals
    private static Dictionary<string, double> MithrilSessionCounts()
    {
        var counts = Enumerable.Range(0, TaskAggregateService.RowItemLimit).ToDictionary(i => $"CHEAP_BLOCK_{i}", i => 1000.0 + i);
        counts["REFINED_MINERAL"] = 8;
        return counts;
    }

    private static Dictionary<string, double> Prices()
    {
        var prices = Enumerable.Range(0, TaskAggregateService.RowItemLimit).ToDictionary(i => $"CHEAP_BLOCK_{i}", _ => 2.0);
        prices["REFINED_MINERAL"] = RefinedMineralPrice;
        return prices;
    }

    private static TaskBucketAggregateRow NewRow(Dictionary<string, double> counts)
        => new() { ItemCounts = counts, ItemRefValues = TaskEstimator.ItemValues(counts, Prices()) };

    private static TrimRules PowderRules() => new(tag => tag == "MITHRIL_POWDER" ? 0.2 : null, _ => null);

    [Test]
    public void KeepCostsAndTop_WithValues_KeepsTheValuableLowCountDrop()
    {
        var counts = MithrilSessionCounts();
        counts["SUSPICIOUS_SCRAP"] = -3;
        var values = TaskEstimator.ItemValues(counts, Prices());

        var kept = TaskAggregateService.KeepCostsAndTop(counts, 12, tag => values.GetValueOrDefault(tag));

        kept.Should().ContainKey("REFINED_MINERAL").And.ContainKey("SUSPICIOUS_SCRAP");
        kept.Keys.Count(k => k.StartsWith("CHEAP_BLOCK_")).Should().Be(11);
        kept.Should().NotContainKey("CHEAP_BLOCK_0", "the cheapest, least mined block is the one dropped");
    }

    [Test]
    public void MergeIntoRow_KeepsRefinedMineral_AndTheValuesOfTheKeptItems()
    {
        var row = NewRow(MithrilSessionCounts());
        row.RefItemValue = 5_000_000;

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit);

        row.ItemCounts.Should().ContainKey("REFINED_MINERAL");
        row.ItemCounts.Count.Should().Be(TaskAggregateService.RowItemLimit);
        row.ItemRefValues.Should().BeEquivalentTo(TaskEstimator.ItemValues(row.ItemCounts, Prices()));
        row.RefItemValue.Should().Be(5_000_000, "the whole fold value reference is only summed, never trimmed");
    }

    [Test]
    public void MergeIntoRow_AddsTheExistingRowsValues()
    {
        var row = NewRow(new() { ["REFINED_MINERAL"] = 2 });
        row.RefItemValue = 200_000;
        var existing = NewRow(new() { ["REFINED_MINERAL"] = 3, ["CHEAP_BLOCK_0"] = 500 });
        existing.RefItemValue = 400_000;

        TaskAggregateService.MergeIntoRow(row, existing, TaskAggregateService.RowItemLimit);

        row.ItemCounts["REFINED_MINERAL"].Should().Be(5);
        row.ItemRefValues["REFINED_MINERAL"].Should().BeApproximately(5 * RefinedMineralPrice, 1e-6);
        row.RefItemValue.Should().Be(600_000);
    }

    /// <summary>Regression: a row without a reference kept every item, so its size was unbounded.</summary>
    [Test]
    public void MergeIntoRow_WithALegacyExistingRow_StaysLegacy_AndTrimsByCount()
    {
        var row = NewRow(MithrilSessionCounts());
        var legacy = new TaskBucketAggregateRow
        {
            ItemCounts = new() { ["MITHRIL_ORE"] = 4000 }, ItemRefValues = null, RefItemValue = 9_000_000
        };

        TaskAggregateService.MergeIntoRow(row, legacy, TaskAggregateService.RowItemLimit);

        row.ItemRefValues.Should().BeNull("the old reference can not be split per item");
        var all = new Dictionary<string, double>(MithrilSessionCounts()) { ["MITHRIL_ORE"] = 4000 };
        row.ItemCounts.Should().BeEquivalentTo(TaskAggregateService.KeepCostsAndTop(all, TaskAggregateService.RowItemLimit));
        TaskAggregateService.IsLegacyRef(row.ItemCounts, row.ItemRefValues).Should().BeTrue();
    }

    [Test]
    public void MergeIntoRow_WithUnknownReference_TrimsByCount()
    {
        var counts = MithrilSessionCounts();
        counts["MITHRIL_ORE"] = 4000;
        var row = new TaskBucketAggregateRow { ItemCounts = new(counts), ItemRefValues = null };

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit);

        row.ItemCounts.Should().BeEquivalentTo(TaskAggregateService.KeepCostsAndTop(counts, TaskAggregateService.RowItemLimit));
        row.ItemRefValues.Should().BeNull();
    }

    [Test]
    public void MergeIntoRow_WithUnknownReference_StillKeepsTheCostReferenceTag()
    {
        var counts = MithrilSessionCounts();
        counts["UMBER"] = 1; // the Mineshaft task's cost reference, the lowest count of all
        var row = new TaskBucketAggregateRow { ItemCounts = counts, ItemRefValues = null };

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit, TrimRules.For(new GlaciteMineshaftTask()));

        row.ItemCounts.Should().ContainKey("UMBER");
        row.ItemCounts.Count.Should().Be(TaskAggregateService.RowItemLimit);
    }

    /// <summary>Regression: powder has no market price, so ranking by the market priced reference trimmed it first.</summary>
    [Test]
    public void MergeIntoRow_RanksPowderByItsOverrideValue()
    {
        var counts = MithrilSessionCounts();
        counts["MITHRIL_POWDER"] = 40_000;
        var row = NewRow(counts);

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit, PowderRules());

        row.ItemCounts.Should().ContainKey("MITHRIL_POWDER").And.ContainKey("REFINED_MINERAL");
        row.ItemRefValues["MITHRIL_POWDER"].Should().Be(0, "the drift reference stays market priced like the live side");
    }

    [Test]
    public void DefaultTrimRules_RankPowderByTheSeedValue()
    {
        var counts = MithrilSessionCounts();
        counts["MITHRIL_POWDER"] = 40_000;
        var row = NewRow(counts);

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit);

        row.ItemCounts.Should().ContainKey("MITHRIL_POWDER");
    }

    /// <summary>Regression: trimming the drop ScaledFormulaCost scales by would silently drop the task's ingredient cost.</summary>
    [Test]
    public void MergeIntoRow_KeepsTheCostReferenceTag()
    {
        var counts = MithrilSessionCounts();
        counts["GEMSTONE_MIXTURE"] = 1; // priced 0 here, so value ranking alone would drop it
        var row = NewRow(counts);
        var task = new SludgeMiningGemMixtureTask();

        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit, TrimRules.For(task));

        task.CostReferenceTag(counts).Should().Be("GEMSTONE_MIXTURE");
        row.ItemCounts.Should().ContainKey("GEMSTONE_MIXTURE");
        row.ItemCounts.Count.Should().Be(TaskAggregateService.RowItemLimit);
    }

    /// <summary>Regression: the kept tag must be the one ScaledFormulaCost uses, the first formula drop that is present.</summary>
    [Test]
    public void CostReferenceTag_IsTheFirstFormulaDropPresent_LikeScaledFormulaCost()
    {
        var task = new GlaciteMineshaftTask();
        var withoutUmber = new Dictionary<string, double> { ["TUNGSTEN"] = 5000, ["HARD_STONE"] = 9000 };

        task.CostReferenceTag(withoutUmber).Should().Be("TUNGSTEN");
        task.CostReferenceTag(new() { ["UMBER"] = 1, ["TUNGSTEN"] = 5 }).Should().Be("UMBER");
        task.ScaledFormulaCost(withoutUmber, 1, _ => 1000).Should().BeGreaterThan(0);
    }

    [Test]
    public void CostReferenceTag_IsNull_ForATaskWithoutFormulaCosts()
        => new MithrilMiningTask().CostReferenceTag(new() { ["MITHRIL_ORE"] = 1 }).Should().BeNull();

    [Test]
    public void AddPlayerItems_RanksPowderByItsOverrideValue()
    {
        var counts = MithrilSessionCounts();
        counts["MITHRIL_POWDER"] = 40_000;
        var row = new TaskPlayerStatRow();

        TaskPeriodFolder.AddPlayerItems(row, counts, 1, Prices(), PowderRules());

        row.ItemCounts.Should().ContainKey("MITHRIL_POWDER");
    }

    [Test]
    public void AddPlayerItems_KeepsRefinedMineral_OverCheapBlocks()
    {
        var row = new TaskPlayerStatRow { RefItemValue = 123 };

        TaskPeriodFolder.AddPlayerItems(row, MithrilSessionCounts(), 1, Prices());

        row.ItemCounts.Should().ContainKey("REFINED_MINERAL");
        row.ItemCounts.Count.Should().Be(TaskPeriodFolder.PlayerItemLimit);
        row.ItemRefValues.Should().BeEquivalentTo(TaskEstimator.ItemValues(row.ItemCounts, Prices()));
        row.RefItemValue.Should().Be(123, "the whole fold value reference is kept by UpdatePlayerStat");
    }

    // ── IsLegacyRef ──

    [Test]
    public void IsLegacyRef_EmptyMap_IsLegacy_EvenWithOnlyCosts()
        => TaskAggregateService.IsLegacyRef(new() { ["SUSPICIOUS_SCRAP"] = -3 }, null).Should().BeTrue();

    [Test]
    public void IsLegacyRef_ACountMissingFromTheMap_IsLegacy()
        => TaskAggregateService.IsLegacyRef(new() { ["A"] = 3, ["B"] = 2 }, new() { ["A"] = 30 }).Should().BeTrue();

    [Test]
    public void IsLegacyRef_EmptyRow_AndCompleteMap_AreNotLegacy()
    {
        TaskAggregateService.IsLegacyRef(new(), null).Should().BeFalse();
        TaskAggregateService.IsLegacyRef(new() { ["A"] = 3, ["COST"] = -1 }, new() { ["A"] = 30, ["COST"] = 0 })
            .Should().BeFalse();
    }
}

/// <summary>The backfill writes its own rows, so it never marks a live instance's row as reference-less.</summary>
public class TaskAggregateBackfillInstanceTests
{
    [Test]
    public void BackfillContribution_GoesToItsOwnRow()
    {
        var aggregates = new TaskAggregateService(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance);
        aggregates.AddContribution("Mithril Mining", 3, "p", 3600, new() { ["MITHRIL_ORE"] = 10 }, 0, 0, 100,
            new() { ["MITHRIL_ORE"] = 100 }, null);
        aggregates.AddContribution("Mithril Mining", 3, "backfill:x", 3600, new() { ["MITHRIL_ORE"] = 50 }, 0, 0, 0,
            null, null, TaskAggregateService.BackfillInstanceId);

        var live = aggregates.PendingRowForTest("Mithril Mining", 3);
        var backfill = aggregates.PendingRowForTest("Mithril Mining", 3, TaskAggregateService.BackfillInstanceId);

        live.ItemRefValues.Should().NotBeNull();
        live.ItemCounts["MITHRIL_ORE"].Should().Be(10);
        backfill.InstanceId.Should().Be(TaskAggregateService.BackfillInstanceId);
        backfill.ItemRefValues.Should().BeNull();
    }
}

/// <summary>
/// The two price drift modes. Off (the default, origin/main): live market value over the whole fold time value,
/// rare drops included, so the 0.25 floor usually applies. On: over the market value of exactly the stored counts,
/// so unchanged prices give 1. On must wait for a per player rare cap.
/// </summary>
[NonParallelizable]
public class TaskAggregateDriftReferenceTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "Diana";

    [TearDown]
    public void Reset()
    {
        TaskRateOptions.FullValueRates = false;
        CoinValueRegistry.ResetOverridesForTest();
    }

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task)
            => System.Threading.Tasks.Task.FromResult(Stored);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private static TaskPeriodFolder MakeFolder(FakeAggregates aggregates)
    {
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        return new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
    }

    // a Minos Relic (rare pool) and more common items than a row keeps, so the flush trims
    private static Dictionary<string, int> SessionItems()
    {
        var items = Enumerable.Range(0, TaskAggregateService.RowItemLimit + 3).ToDictionary(i => $"COMMON_{i}", i => 10 + i);
        items["MINOS_RELIC"] = 1;
        return items;
    }

    private static Dictionary<string, double> Prices()
    {
        var prices = Enumerable.Range(0, TaskAggregateService.RowItemLimit + 3).ToDictionary(i => $"COMMON_{i}", i => 100.0 + i);
        prices["MINOS_RELIC"] = 16_000_000;
        return prices;
    }

    private static Period Session() => new()
    {
        PlayerUuid = Player, Location = "Ruins", DetectedTask = Task,
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0), EndTime = new DateTime(2026, 10, 7, 12, 30, 0),
        ItemsCollected = SessionItems()
    };

    private static async Task<BucketAggregate> FoldAndFlush()
    {
        var aggregates = new FakeAggregates();
        await MakeFolder(aggregates).Fold(Session(), new StateObject(), Prices());
        var row = aggregates.PendingRowForTest(Task, StatScoreService.UnknownBucket);
        row.Should().NotBeNull();
        TaskAggregateService.MergeIntoRow(row, null, TaskAggregateService.RowItemLimit);
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, row, 0.8);
        return agg;
    }

    [Test]
    public async Task SwitchOn_FoldedAggregateWithARarePool_HasNoDriftAtUnchangedPrices()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = await FoldAndFlush();

        var parts = TaskEstimator.RateParts(new DianaTask(), agg, Prices());

        agg.RareCoins.Should().BeGreaterThan(0, "the relic is a rare drop");
        agg.ItemCounts.Count.Should().Be(TaskAggregateService.RowItemLimit);
        parts.DriftScale.Should().BeApproximately(1, 1e-9);
        parts.ScaledRareCoins.Should().BeApproximately(agg.RareCoins, 1e-6);
    }

    [Test]
    public async Task SwitchOff_FoldedAggregate_KeepsTheOriginMainDrift()
    {
        var agg = await FoldAndFlush();

        var parts = TaskEstimator.RateParts(new DianaTask(), agg, Prices());

        var live = TaskEstimator.MarketValue(agg.ItemCounts, Prices());
        agg.RefItemValue.Should().BeGreaterThan(16_000_000 * 0.8, "the whole fold value, relic included");
        parts.DriftScale.Should().Be(Math.Clamp(live / agg.RefItemValue, 0.25, 4)).And.Be(0.25);
    }

    [Test]
    public async Task FoldedPlayerStat_KeepsBothReferences()
    {
        var aggregates = new FakeAggregates();
        await MakeFolder(aggregates).Fold(Session(), new StateObject(), Prices());
        var row = aggregates.Stored;
        var live = TaskEstimator.MarketValue(row.ItemCounts, Prices());

        row.RareCoins.Should().BeGreaterThan(0);
        row.RefItemValue.Should().BeGreaterThan(16_000_000, "the whole fold value, relic included");
        row.ItemRefValues.Values.Sum().Should().BeApproximately(live, 1e-6);
        TaskRateOptions.FullValueRates = true;
        TaskEstimator.DriftScale(live, row.RefItemValue, row.ItemRefValues.Values.Sum(), false).Should().BeApproximately(1, 1e-9);
    }

    private static BucketAggregate LegacyAggregate()
    {
        // stored before the fix: the reference also priced the relic, 3000 live vs 16M reference
        var legacy = new TaskBucketAggregateRow
        {
            WSeconds = 3600, ItemCounts = new() { ["COMMON_0"] = 30 }, ItemRefValues = null,
            RefItemValue = 16_003_000, RareCoins = 16_000_000, Players = new() { "a" }
        };
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, legacy, 1);
        return agg;
    }

    [Test]
    public void SwitchOn_LegacyRow_IsNotDriftCorrected()
    {
        TaskRateOptions.FullValueRates = true;
        var agg = LegacyAggregate();

        var parts = TaskEstimator.RateParts(null, agg, Prices());

        agg.LegacyRefValue.Should().BeTrue();
        parts.DriftScale.Should().Be(1);
        parts.ScaledRareCoins.Should().Be(16_000_000);
    }

    [Test]
    public void SwitchOff_LegacyRow_KeepsTheOriginMainDrift()
        => TaskEstimator.RateParts(null, LegacyAggregate(), Prices()).DriftScale.Should().Be(0.25);

    [Test]
    public void SwitchOn_LegacyRowMergedWithNewRows_IsNotDriftCorrected()
    {
        TaskRateOptions.FullValueRates = true;
        var fresh = new TaskBucketAggregateRow
        {
            WSeconds = 3600, ItemCounts = new() { ["COMMON_0"] = 30 }, ItemRefValues = new() { ["COMMON_0"] = 3000 },
            RefItemValue = 3000
        };
        var day0 = new BucketAggregate();
        TaskAggregateService.AddRow(day0, fresh, 1);
        var merged = TaskEstimator.MergeBuckets(new() { [(Task, (byte)0)] = day0, [(Task, (byte)1)] = LegacyAggregate() }, Task);

        TaskEstimator.RateParts(null, merged, Prices()).DriftScale.Should().Be(1);
    }

    [Test]
    public void SwitchOn_NewRow_StillCorrectsRealPriceDrift()
    {
        TaskRateOptions.FullValueRates = true;
        var row = new TaskBucketAggregateRow
        {
            WSeconds = 3600, ItemCounts = new() { ["COMMON_0"] = 30 }, ItemRefValues = new() { ["COMMON_0"] = 1500 },
            RefItemValue = 900_000, RareCoins = 1000
        };
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, row, 1);

        // COMMON_0 went from 50 to 100 since the fold
        TaskEstimator.RateParts(null, agg, Prices()).DriftScale.Should().BeApproximately(2, 1e-9);
    }

    /// <summary>Costs and powder are coins at a fixed value: they must not move the drift ratio in either mode.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void Drift_IgnoresCostsAndPowder(bool useItemRefDrift)
    {
        TaskRateOptions.FullValueRates = useItemRefDrift;
        var agg = new BucketAggregate
        {
            WSeconds = 3600, RefItemValue = 3000, ItemRefValue = 3000,
            ItemCounts = new() { ["COMMON_0"] = 30, ["DUNGEON_CHEST_COST"] = -2_000_000, ["MITHRIL_POWDER"] = 100_000 }
        };

        TaskEstimator.RateParts(null, agg, Prices()).DriftScale.Should().BeApproximately(1, 1e-9);
    }

    private static TaskPlayerStatRow LegacyPlayerRow() => new()
    {
        WSeconds = 3600, ItemCounts = new() { ["COMMON_0"] = 30 }, RefItemValue = 16_003_000, RareCoins = 16_000_000
    };

    [Test]
    public void SwitchOn_LegacyPlayerStat_IsNotDriftCorrected()
    {
        TaskRateOptions.FullValueRates = true;

        // 30 x 100 items + the full 16M pool, not a quarter of it
        TaskEstimator.RateFromPlayerStat(null, LegacyPlayerRow(), Prices(), 1).Should().BeApproximately(16_003_000, 1e-6);
    }

    [Test]
    public void SwitchOff_LegacyPlayerStat_KeepsTheOriginMainDrift()
        => TaskEstimator.RateFromPlayerStat(null, LegacyPlayerRow(), Prices(), 1).Should().BeApproximately(3000 + 4_000_000, 1e-6);

    [Test]
    public void AddPlayerItems_ConvertsALegacyRow_AtTodaysPrices()
    {
        var row = new TaskPlayerStatRow { ItemCounts = new() { ["COMMON_0"] = 30 }, RefItemValue = 16_003_000 };

        TaskPeriodFolder.AddPlayerItems(row, new() { ["COMMON_1"] = 10 }, 0.5, Prices());

        row.ItemRefValues["COMMON_0"].Should().BeApproximately(15 * 100, 1e-6);
        row.ItemRefValues["COMMON_1"].Should().BeApproximately(10 * 101, 1e-6);
    }
}
