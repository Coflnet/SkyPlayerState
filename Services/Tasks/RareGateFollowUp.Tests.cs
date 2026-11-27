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

/// <summary>
/// Review follow-ups of the declared drop gate and the itemized rare pool: unique drop tags through the whole estimate path,
/// the gate limited to rare pool candidates, the player's ledger kept whole, cost-only leftovers dropped, drops sorted by value.
/// </summary>
[NonParallelizable]
public class RareGateFollowUpTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private static readonly TaskRegistry Registry = new();

    private static MethodTask Task(string name) => (MethodTask)Registry.GetByName(name);

    // ── finding 1: a tag that is both a regular and a rare drop ──

    private static BucketAggregate FumingBoth() => new()
    {
        TaskName = "M7", WSeconds = 10 * 3600, WPeriods = 20,
        ItemCounts = new() { ["FUMING_POTATO_BOOK"] = 30, ["ESSENCE_WITHER"] = 6000 },
        ItemRefValue = 30 * 1_100_000 + 6000 * 2000.0, RefItemValue = 30 * 1_100_000 + 6000 * 2000.0,
        RareCoins = 12_000_000, RareItemCoins = new() { ["FUMING_POTATO_BOOK"] = 12_000_000 },
        Contributors = Enumerable.Range(0, 5).Select(i => "p" + i).ToHashSet(),
        RareContributors = Enumerable.Range(0, 5).Select(i => "p" + i).ToHashSet()
    };

    private static readonly Dictionary<string, double> Prices = new() { ["FUMING_POTATO_BOOK"] = 1_100_000, ["ESSENCE_WITHER"] = 2000 };

    private static async Task<TaskEstimate> CommunityEstimate(BucketAggregate agg, string task)
    {
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.SetSnapshotForTest(new() { [(task, (byte)1)] = agg });
        var estimator = new TaskEstimator(Registry, aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, Registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance), NullLogger<TaskEstimator>.Instance);
        return (await estimator.EstimateCommunity(Prices, default)).Single(e => e.TaskName == task);
    }

    /// <summary>Regression: the rare entry was appended next to the regular one, and the /cofl task breakdown threw on the duplicate key.</summary>
    [Test]
    public async Task TagInCommonAndRarePool_IsOneDrop_ThroughTheWholeEstimatePath()
    {
        var estimate = await CommunityEstimate(FumingBoth(), "M7");

        estimate.Drops.Select(d => d.ItemTag).Should().OnlyHaveUniqueItems();
        var fuming = estimate.Drops.Single(d => d.ItemTag == "FUMING_POTATO_BOOK");
        fuming.ContributionPerHour.Should().BeApproximately(30 * 1_100_000 / 10.0 + 12_000_000 / 10.0, 1, "regular plus rare share");

        var result = await Task("M7").Execute(Params(estimate));

        result.Breakdown.Drops.Should().ContainSingle(d => d.ItemTag == "FUMING_POTATO_BOOK");
    }

    [Test]
    public async Task ServerEstimate_WithDuplicateTags_IsSummed_AndRareKindIsCopied()
    {
        var estimate = new TaskEstimate
        {
            CoinsPerHour = 5_000_000, Source = "global",
            Drops =
            [
                new() { ItemTag = "FUMING_POTATO_BOOK", RatePerHour = 1, PriceEach = 1_000_000, ContributionPerHour = 1_000_000 },
                new() { ItemTag = "FUMING_POTATO_BOOK", RatePerHour = 2, PriceEach = 1_000_000, ContributionPerHour = 2_000_000, Kind = TaskDropRate.RareKind },
                new() { ItemTag = "NECRON_HANDLE", RatePerHour = 0.01, PriceEach = 400_000_000, ContributionPerHour = 4_000_000, Kind = TaskDropRate.RareKind }
            ]
        };

        var result = await Task("M7").Execute(Params(estimate));

        var fuming = result.Breakdown.Drops.Single(d => d.ItemTag == "FUMING_POTATO_BOOK");
        fuming.RatePerHour.Should().Be(3);
        fuming.ContributionPerHour.Should().Be(3_000_000);
        result.Breakdown.Drops.Single(d => d.ItemTag == "NECRON_HANDLE").Kind.Should().Be(TaskDropRate.RareKind);
    }

    private static TaskParams Params(TaskEstimate estimate) => new()
    {
        TestTime = new DateTime(2026, 11, 27, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new System.Collections.Concurrent.ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = new Dictionary<string, Period[]>(),
        CleanPrices = new Dictionary<string, long>(),
        BazaarPrices = [],
        Names = new Dictionary<string, string>(),
        ServerEstimates = new() { ["M7"] = estimate }
    };

    // ── finding 6: most valuable drops first ──

    /// <summary>Regression: sorted by count, cheap bulk items pushed the valuable low count drops out of the 20 listed.</summary>
    [Test]
    public void Drops_AreSortedByContribution()
    {
        var counts = Enumerable.Range(0, 25).ToDictionary(i => $"BULK_{i}", i => 10_000.0 + i);
        counts["COMPACTED_WILD_ROSE"] = 40;
        var prices = counts.Keys.ToDictionary(tag => tag, tag => tag.StartsWith("BULK") ? 1.0 : 100_000);

        var drops = TaskEstimator.BuildDrops(Task("Wild Rose Farming"), counts, 3600, prices);

        drops.Should().HaveCount(TaskEstimator.PublishedDropLimit);
        drops[0].ItemTag.Should().Be("COMPACTED_WILD_ROSE");
        drops.Select(d => d.ContributionPerHour).Should().BeInDescendingOrder();
    }

    // ── findings 3 and 4: the fold ──

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public readonly List<long> Realized = new();
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => System.Threading.Tasks.Task.FromResult(Stored);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
        public override Task AddRealized(string playerUuid, string task, DateTime foldTime, long value)
        {
            Realized.Add(value);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private static TaskPeriodFolder Folder(TaskAggregateService aggregates)
    {
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        return new TaskPeriodFolder(aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
    }

    private static Period Session(string task, string location, Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = location, DetectedTask = task,
        StartTime = new DateTime(2026, 11, 27, 12, 0, 0), EndTime = new DateTime(2026, 11, 27, 12, 30, 0), ItemsCollected = items
    };

    [Test]
    public void Gate_KeepsAFrequentExpensiveItem()
    {
        var bucket = new BucketAggregate { WPeriods = 30, ItemCounts = new() { ["HYPERION"] = 10, ["SHARD_HIVETHIEF"] = 40 } };

        TaskPeriodFolder.UndeclaredExpensive(new() { { "HYPERION", 1 } }, Task("Helix Honey Lathering"), _ => 5_000_000,
            tag => TaskPeriodFolder.IsInfrequent(tag, bucket)).Should().BeEmpty("a frequent item is backed by the bucket's counts");
        TaskPeriodFolder.UndeclaredExpensive(new() { { "HELIXIS_X", 1 } }, Task("Helix Honey Lathering"), _ => 5_000_000,
            tag => TaskPeriodFolder.IsInfrequent(tag, bucket)).Should().ContainKey("HELIXIS_X");
    }

    [Test]
    public async Task Fold_RealizedLedger_KeepsTheExcludedItems()
    {
        var aggregates = new FakeAggregates();

        await Folder(aggregates).Fold(Session("Helix Honey Lathering", "Torrhus Canyon",
                new() { { "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 2 }, { "SHARD_HIVETHIEF", 4 } }), new StateObject(),
            new() { ["ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3"] = 67_000_000, ["SHARD_HIVETHIEF"] = 20_000 });

        aggregates.PendingRowForTest("Helix Honey Lathering", StatScoreService.UnknownBucket).RefItemValue
            .Should().BeLessThan(100_000, "the task's rate does not count the books");
        aggregates.Realized.Should().Equal(2 * 67_000_000 + 4 * 20_000);
    }

    /// <summary>A paid chest whose content is undeclared must not fold as a pure cost and push the floor's rate down.</summary>
    [Test]
    public async Task Fold_PaidChestWithUndeclaredContent_IsDropped()
    {
        var aggregates = new FakeAggregates();
        var before = FoldMetrics.CountOf("F7", FoldMetrics.UndeclaredLeftover);

        await Folder(aggregates).Fold(Session("F7", "The Catacombs (F7)",
                new() { { PseudoItems.DUNGEON_CHEST_COST, -6_000_000 }, { "HYPERION", 1 }, { "ESSENCE_WITHER", 40 } }), new StateObject(),
            new() { ["HYPERION"] = 900_000_000, ["ESSENCE_WITHER"] = 2000 });

        aggregates.PendingRowForTest("F7", StatScoreService.UnknownBucket).Should().BeNull();
        FoldMetrics.CountOf("F7", FoldMetrics.UndeclaredLeftover).Should().Be(before + 1);
    }

    [Test]
    public async Task Fold_PaidChestWithDeclaredContent_StillFolds()
    {
        var aggregates = new FakeAggregates();

        await Folder(aggregates).Fold(Session("F7", "The Catacombs (F7)",
                new() { { PseudoItems.DUNGEON_CHEST_COST, -6_000_000 }, { "NECRON_HANDLE", 1 } }), new StateObject(),
            new() { ["NECRON_HANDLE"] = 400_000_000 });

        aggregates.PendingRowForTest("F7", StatScoreService.UnknownBucket).RareItemCoins.Should().ContainKey("NECRON_HANDLE");
    }

    [Test]
    public void ZeroWeightRareCoins_AreNotStored()
    {
        var aggregates = new TaskAggregateService(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance);

        aggregates.AddContribution("F7", 1, "p", 0, new(), 0, 0, 0, new(), null, rareItemCoins: new() { ["NECRON_HANDLE"] = 0 });

        aggregates.PendingRowForTest("F7", 1).RareItemCoins.Should().BeEmpty();
    }
}
