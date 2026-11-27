using System;
using System.Collections.Concurrent;
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

/// <summary>
/// Fold and breakdown level regressions of the Kuudra key cost:
/// <list type="bullet">
/// <item>A confirmed paid chest open books -2.328M DUNGEON_CHEST_COST (1 coin/unit at fold time) beside its essence; on the
/// old net AFK check such a run went negative and was dropped as AFK with the flag off.</item>
/// <item>The result breakdowns priced costs from the raw price dict, so the key (no market price) had no cost line and the
/// formula result rescaled its drops to ~6% to match the estimator's net figure.</item>
/// </list>
/// </summary>
[NonParallelizable]
public class KuudraCostsFoldTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "Kuudra T5";
    private const string InfernalKey = "KUUDRA_INFERNAL_TIER_KEY";

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ESSENCE_CRIMSON"] = 1_000, ["KUUDRA_TEETH"] = 10_000, ["SHARD_KRAKEN"] = 100_000, ["TOXIC_ARROW_POISON"] = 2_000,
        ["ENCHANTED_RED_SAND"] = 1_000, ["ENCHANTED_MYCELIUM"] = 2_000, ["CORRUPTED_NETHER_STAR"] = 50_000,
    };

    private const double InfernalKeyValue = 2_328_000 + 80 * 1_000 + 2 * 50_000;

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task)
            => System.Threading.Tasks.Task.FromResult(Stored);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    [TearDown]
    public void ResetSwitch() => TaskRateOptions.FullValueRates = false;

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make()
    {
        // claim inflated bucket: 10 h holding 200M/h of essence, so a normal run is far below 5% of it
        var bucket = new BucketAggregate { WSeconds = 36000, WPeriods = 20, ItemCounts = new() { ["ESSENCE_CRIMSON"] = 2_000_000 } };
        var aggregates = new FakeAggregates();
        aggregates.SetSnapshotForTest(new() { [(Task, StatScoreService.UnknownBucket)] = bucket });
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmedT5Run_WithChestCostAndEssence_IsFoldedNotAfk(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var (folder, aggregates) = Make();
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);
        var start = new DateTime(2026, 11, 20, 12, 0, 0);
        var run = new Period
        {
            PlayerUuid = Player, Location = "Kuudra's Hollow (T5)", DetectedTask = Task,
            StartTime = start, EndTime = start.AddMinutes(8),
            ItemsCollected = new() { [PseudoItems.DUNGEON_CHEST_COST] = -2_328_000, ["ESSENCE_CRIMSON"] = 2_000, ["TOXIC_ARROW_POISON"] = -60 }
        };

        await folder.Fold(run, new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        aggregates.Stored.WSeconds.Should().BeApproximately(480, 1);
        aggregates.Stored.ItemCounts["ESSENCE_CRIMSON"].Should().BeApproximately(2_000, 1e-6);
        aggregates.Stored.ItemCounts[PseudoItems.DUNGEON_CHEST_COST].Should().BeApproximately(-2_328_000, 1e-6);
    }

    private static TaskParams Params() => new()
    {
        TestTime = new DateTime(2026, 11, 27, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = new(),
        CleanPrices = Prices.ToDictionary(p => p.Key, p => (long)p.Value),
        BazaarPrices = [],
        Names = new()
    };

    [Test]
    public async Task FormulaBreakdown_ListsTheKey_AndNetsIt()
    {
        var result = await new KuudraT5Task().Execute(Params());

        result.Breakdown.Source.Should().Be("formula");
        var key = result.Breakdown.Costs.Should().ContainSingle(c => c.ItemTag == InfernalKey).Which;
        key.PriceEach.Should().BeApproximately(InfernalKeyValue, 1);
        key.RatePerHour.Should().BeApproximately(5, 1e-6);
        result.ProfitPerHour.Should().BeCloseTo((int)TaskEstimator.FormulaRate(new KuudraT5Task(), Prices), 2);
    }

    [Test]
    public async Task ServerEstimateBreakdown_ListsTheKey()
    {
        var parameters = Params();
        parameters.ServerEstimates = new()
        {
            [Task] = new TaskEstimate
            {
                TaskName = Task, Source = "global", CoinsPerHour = 5_000_000,
                Drops = [new TaskDropRate { ItemTag = "KUUDRA_TEETH", RatePerHour = 32, PriceEach = 10_000, ContributionPerHour = 320_000 }]
            }
        };

        var result = await new KuudraT5Task().Execute(parameters);

        var key = result.Breakdown.Costs.Should().ContainSingle(c => c.ItemTag == InfernalKey).Which;
        key.RatePerHour.Should().BeApproximately(10, 1e-6, "32 teeth/h are 10 paid chests");
        key.PriceEach.Should().BeApproximately(InfernalKeyValue, 1);
    }

    /// <summary>
    /// Keys are only valued inside the Kuudra tiers' cost: a key gained elsewhere (lava fishing treasure, a purchase seen
    /// at "Unknown") keeps its absent market price instead of turning into 155k-2.4M of loot.
    /// </summary>
    [Test]
    public void GainedKeysElsewhere_HaveNoValue()
    {
        CoinValueRegistry.TryGetNonMarketValue(InfernalKey, out _).Should().BeFalse();
        CoinValueRegistry.TryGetNonMarketValue("KUUDRA_TIER_KEY", out _).Should().BeFalse();
        TaskEstimator.UnitValue("KUUDRA_TIER_KEY", Prices).Should().Be(0);
    }
}
