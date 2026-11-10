using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Fold outcome metrics and the AFK check, which compares common rates only (no rare pool).</summary>
public class TaskPeriodFolderFoldMetricsTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "Diana";

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => System.Threading.Tasks.Task.FromResult(Stored);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make(BucketAggregate bucket = null)
    {
        var aggregates = new FakeAggregates();
        if (bucket != null)
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

    private static Period Session(double minutes, Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "Ruins", DetectedTask = Task,
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0),
        EndTime = new DateTime(2026, 10, 7, 12, 0, 0).AddMinutes(minutes),
        ItemsCollected = items
    };

    private static readonly Dictionary<string, double> Prices = new() { ["X"] = 1000, ["JUNK"] = 1 };

    // 10 hours, common value 100k (10k/h), huge rare pool that inflates the old full bucket rate to ~5M/h
    private static BucketAggregate RareHeavyBucket() => new()
    {
        WSeconds = 36000, WPeriods = 20, RareCoins = 50_000_000, RefItemValue = 100_000,
        ItemCounts = new() { ["X"] = 100 }
    };

    [Test]
    public async Task TooShort_IncrementsTooShortLabels()
    {
        var (folder, _) = Make();
        var count = FoldMetrics.CountOf(Task, FoldMetrics.TooShort);
        var seconds = FoldMetrics.SecondsOf(Task, FoldMetrics.TooShort);
        var value = FoldMetrics.ValueOf(Task, FoldMetrics.TooShort);

        await folder.Fold(Session(1, new() { ["X"] = 3 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.TooShort).Should().Be(count + 1);
        FoldMetrics.SecondsOf(Task, FoldMetrics.TooShort).Should().BeApproximately(seconds + 60, 1e-6);
        FoldMetrics.ValueOf(Task, FoldMetrics.TooShort).Should().BeApproximately(value + 3000, 1e-6);
    }

    [Test]
    public async Task Afk_IncrementsAfkLabels_AndDoesNotFold()
    {
        var (folder, aggregates) = Make(RareHeavyBucket());
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);
        var folded = FoldMetrics.CountOf(Task, FoldMetrics.Folded);

        await folder.Fold(Session(30, new() { ["JUNK"] = 1 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk + 1);
        FoldMetrics.CountOf(Task, FoldMetrics.Folded).Should().Be(folded);
        aggregates.Stored.Should().BeNull();
    }

    [Test]
    public async Task NegativeValue_GoesToTheNegativeCounter()
    {
        var (folder, _) = Make();
        var neg = FoldMetrics.NegativeValueOf(Task, FoldMetrics.TooShort);

        await folder.Fold(Session(1, new() { ["DUNGEON_CHEST_COST"] = -2000 }), new StateObject(), new() { ["DUNGEON_CHEST_COST"] = 1 });

        FoldMetrics.NegativeValueOf(Task, FoldMetrics.TooShort).Should().BeGreaterThan(neg);
    }

    [Test]
    public async Task NormalSessionWithRareHeavyBucket_IsNotAfk_AndFolds()
    {
        var (folder, aggregates) = Make(RareHeavyBucket());
        var folded = FoldMetrics.CountOf(Task, FoldMetrics.Folded);
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);

        // 30 minutes of 3 X = 6k/h, about 60% of the bucket's common 10k/h, far below 5% of the pool inflated rate
        await folder.Fold(Session(30, new() { ["X"] = 3 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Folded).Should().Be(folded + 1);
        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        aggregates.Stored.Should().NotBeNull();
    }

    [Test]
    public void EstimateCommonBucketRate_IgnoresPools()
    {
        TaskPeriodFolder.EstimateCommonBucketRate(null, RareHeavyBucket(), Prices).Should().BeApproximately(10_000, 1e-6);
    }
}

/// <summary>
/// Regression: the AFK check compared the session's net value (chest cost included) with a bucket rate that priced the
/// cost at 0, so a run that bought its chest went negative and was dropped as AFK. Its seconds were lost while its loot
/// arrived later as a claim without seconds.
/// </summary>
[NonParallelizable]
public class TaskPeriodFolderAfkCostTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "M6";

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

    private static readonly Dictionary<string, double> Prices = new() { ["ESSENCE_WITHER"] = 2000, ["GIANTS_SWORD"] = 5_000_000 };

    [TearDown]
    public void ResetSwitch() => TaskRateOptions.FullValueRates = false;

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make()
    {
        var aggregates = new FakeAggregates();
        aggregates.SetSnapshotForTest(new() { [(Task, StatScoreService.UnknownBucket)] = Bucket() });
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    // 200k/h of essence gained, net -3.8M/h after the 2M chest
    private static Dictionary<string, int> RunWithChest() => new() { ["ESSENCE_WITHER"] = 50, ["DUNGEON_CHEST_COST"] = -2_000_000 };

    // 10 hours: 1M/h of essence, chests bought for 2M/h
    private static BucketAggregate Bucket() => new()
    {
        WSeconds = 36000, WPeriods = 20,
        ItemCounts = new() { ["ESSENCE_WITHER"] = 5000, ["DUNGEON_CHEST_COST"] = -20_000_000 }
    };

    private static Period At(DateTime start, double minutes, Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "The Catacombs (M6)", DetectedTask = Task,
        StartTime = start, EndTime = start.AddMinutes(minutes), ItemsCollected = items
    };

    [Test]
    public async Task FullValue_RunThatBoughtItsChest_KeepsItsSeconds_AndTheLateClaimAddsNone()
    {
        TaskRateOptions.FullValueRates = true;
        var (folder, aggregates) = Make();
        var start = new DateTime(2026, 11, 10, 12, 0, 0);
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);

        await folder.Fold(At(start, 30, RunWithChest()), new StateObject(), Prices);
        await folder.FoldLateReward(At(start.AddMinutes(31), 0.2, new() { ["GIANTS_SWORD"] = 1 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        aggregates.Stored.WSeconds.Should().BeApproximately(1800, 1, "the late claim decays the row a little but adds no seconds");
        aggregates.PendingForTest(Task).wSeconds.Should().BeGreaterThan(0);
    }

    /// <summary>Switch off: the origin/main check, the session's net value goes negative and it is dropped as AFK.</summary>
    [Test]
    public async Task SwitchOff_RunThatBoughtItsChest_IsAfk_LikeOriginMain()
    {
        var (folder, aggregates) = Make();
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);

        await folder.Fold(At(new DateTime(2026, 11, 10, 12, 0, 0), 30, RunWithChest()), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk + 1);
        aggregates.Stored.Should().BeNull();
    }

    [Test]
    public void FullValue_IsAfk_ComparesGrossGains()
    {
        TaskRateOptions.FullValueRates = true;
        var withCost = new Dictionary<string, double> { ["ESSENCE_WITHER"] = 50, ["DUNGEON_CHEST_COST"] = -2_000_000 };

        TaskPeriodFolder.IsAfk(null, Bucket(), withCost, -1_900_000, 0.5, Prices).Should().BeFalse();
        TaskPeriodFolder.IsAfk(null, Bucket(), new() { ["ESSENCE_WITHER"] = 1 }, 2000, 0.5, Prices).Should().BeTrue();
        TaskPeriodFolder.EstimateCommonBucketRate(null, Bucket(), Prices).Should().BeApproximately(1_000_000, 1e-6);
    }

    [Test]
    public void SwitchOff_CommonBucketRate_ValuesTheCostAtZero_LikeOriginMain()
        => TaskPeriodFolder.EstimateCommonBucketRate(null, Bucket(), Prices).Should().BeApproximately(1_000_000, 1e-6);
}
