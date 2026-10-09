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
/// Regression coverage for the folder-path changes that let a real dungeon chest cost (a negative
/// item count) reach aggregates/player stats instead of being silently dropped by a plain
/// "positive counts only" filter - see <see cref="TaskPeriodFolder.FilterOwnedItems"/> and
/// <see cref="TaskPeriodFolder.SplitRareAndCommon"/>, both pulled out of
/// <see cref="TaskPeriodFolder.Fold"/> as pure statics specifically so they are unit testable without
/// the Cassandra-backed <see cref="TaskAggregateService"/>/<see cref="CoinValueRegistry"/>/
/// <see cref="ITransactionService"/> the rest of Fold needs (there is no existing TaskPeriodFolder
/// test fixture in this repo to build on - those dependencies are why).
/// </summary>
public class TaskPeriodFolderTests
{
    // ── FilterOwnedItems ──

    [Test]
    public void FilterOwnedItems_KeepsPositiveCounts()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 100 } };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, null);

        owned.Should().ContainKey("ESSENCE_WITHER").WhoseValue.Should().Be(100);
    }

    [Test]
    public void FilterOwnedItems_KeepsPseudoCostTagRegardlessOfMethodTask()
    {
        var items = new Dictionary<string, int> { { PseudoItems.DUNGEON_CHEST_COST, -2_000_000 } };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, null);

        owned.Should().ContainKey(PseudoItems.DUNGEON_CHEST_COST).WhoseValue.Should().Be(-2_000_000,
            "a COST pseudo tag is real economic activity and must survive even with no classified task");
    }

    [Test]
    public void FilterOwnedItems_KeepsNegativeFormulaCostTagOfTheClassifiedTask()
    {
        var items = new Dictionary<string, int> { { "KISMET_FEATHER", -2 }, { "ESSENCE_WITHER", 600 } };
        var task = new M7KismetTask();

        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

        owned.Should().ContainKey("KISMET_FEATHER").WhoseValue.Should().Be(-2,
            "M7KismetTask declares KISMET_FEATHER in FormulaCosts, so its negative count is a real cost, not noise");
        owned.Should().ContainKey("ESSENCE_WITHER").WhoseValue.Should().Be(600);
    }

    [Test]
    public void FilterOwnedItems_DropsNegativeCountNotDeclaredAsACost()
    {
        // A negative count of a real item tag the classified task does NOT declare in FormulaCosts
        // is not recognized as a cost - dropped, same as before this change (never rendered as a
        // negative-rate drop, never counted as a mystery cost either).
        var items = new Dictionary<string, int> { { "SOME_RANDOM_ITEM", -5 }, { "ESSENCE_WITHER", 600 } };
        var task = new M7Task(); // FormulaCosts empty

        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

        owned.Should().NotContainKey("SOME_RANDOM_ITEM");
        owned.Should().ContainKey("ESSENCE_WITHER");
    }

    [Test]
    public void FilterOwnedItems_NegativeCountOfATagNotInThisTasksOwnFormulaCosts_IsDropped()
    {
        // KISMET_FEATHER only counts as a declared cost for the task that actually declares it -
        // the same negative count under a different (unrelated) classified task is just dropped.
        var items = new Dictionary<string, int> { { "KISMET_FEATHER", -2 }, { "ESSENCE_WITHER", 300 } };
        var task = new M4Task(); // does not declare KISMET_FEATHER as a FormulaCost

        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

        owned.Should().NotContainKey("KISMET_FEATHER");
    }

    // ── SplitRareAndCommon ──

    [Test]
    public void SplitRareAndCommon_NegativeCostNeverEntersTheRarePool_EvenAtAHighUnitPrice()
    {
        var owned = new Dictionary<string, int> { { PseudoItems.DUNGEON_CHEST_COST, -2_000_000 } };

        // pseudo cost tags are always worth 1/unit, but pretend a real cost tag could be priced
        // above the rare threshold too - the count>0 guard must still keep it out of the rare pool.
        var (commonCounts, rareCoins, itemValue) = TaskPeriodFolder.SplitRareAndCommon(
            owned, _ => 5_000_000, _ => true);

        rareCoins.Should().Be(0, "a negative count must never be shunted into the rare/EV pool regardless of its unit price");
        commonCounts.Should().ContainKey(PseudoItems.DUNGEON_CHEST_COST);
        itemValue.Should().BeLessThan(0, "the cost must still reduce the period's total item value");
    }

    [Test]
    public void SplitRareAndCommon_PositiveRareDrop_StillGoesToTheRarePool()
    {
        var owned = new Dictionary<string, int> { { "NECRON_HANDLE", 1 } };

        var (commonCounts, rareCoins, itemValue) = TaskPeriodFolder.SplitRareAndCommon(
            owned, _ => 300_000_000, _ => true);

        rareCoins.Should().Be(300_000_000);
        commonCounts.Should().NotContainKey("NECRON_HANDLE");
        itemValue.Should().Be(300_000_000);
    }

    [Test]
    public void SplitRareAndCommon_CostReducesItemValue_ByExactlyItsCoinAmount()
    {
        var owned = new Dictionary<string, int>
        {
            { "ESSENCE_WITHER", 100 },
            { PseudoItems.DUNGEON_CHEST_COST, -2_000_000 }
        };
        double PriceOf(string tag) => tag == "ESSENCE_WITHER" ? 20 : 1;

        var (_, _, itemValue) = TaskPeriodFolder.SplitRareAndCommon(owned, PriceOf, _ => false);

        itemValue.Should().Be(100 * 20 - 2_000_000);
    }
}

/// <summary><see cref="TaskPeriodFolder.FoldLateReward"/>: value for the earning task without the hand-in's seconds.</summary>
public class TaskPeriodFolderLateRewardTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates(TaskPlayerStatRow existing) : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskAggregateService>.Instance)
            => Stored = existing;
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => Task.FromResult(Stored);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return Task.CompletedTask;
        }
    }

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make(TaskPlayerStatRow existing)
    {
        var aggregates = new FakeAggregates(existing);
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    private static Period HandIn(string task = "Fig Foraging") => new()
    {
        PlayerUuid = Player, Location = "Murkwater Loch", DetectedTask = task, Profit = 366_330,
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0), EndTime = new DateTime(2026, 10, 7, 12, 0, 0).AddSeconds(16.6),
        ItemsCollected = new Dictionary<string, int> { { "AGATHA_COUPON", 30 } }
    };

    private static readonly Dictionary<string, double> Prices = new() { { "AGATHA_COUPON", 12211 } };

    private static TaskPlayerStatRow Veteran() => new()
    {
        PlayerUuid = Player, TaskName = "Fig Foraging", WSeconds = 36000, CumulativeMinutes = 600, LastFold = DateTime.UtcNow
    };

    [Test]
    public async Task FoldLateReward_AddsTheValue_ButNoSeconds_AndNoMinutes()
    {
        var (folder, aggregates) = Make(Veteran());

        await folder.FoldLateReward(HandIn(), new StateObject(), Prices);

        aggregates.Stored.RefItemValue.Should().BeApproximately(30 * 12211, 1, "the coupons are value for the task");
        aggregates.Stored.ItemCounts["AGATHA_COUPON"].Should().BeApproximately(30, 1e-6);
        aggregates.Stored.WSeconds.Should().BeApproximately(36000, 1, "the hand-in's 16.6 seconds are not work time");
        aggregates.Stored.CumulativeMinutes.Should().BeApproximately(600, 1);
        var (seconds, items) = aggregates.PendingForTest("Fig Foraging");
        seconds.Should().Be(0);
        items["AGATHA_COUPON"].Should().BeApproximately(30, 1e-6);
    }

    [Test]
    public async Task FoldLateReward_SkipsTheTooShortGate_ThatPlainFoldAppliesToTheSamePeriod()
    {
        var (late, lateAggregates) = Make(Veteran());
        var (plain, plainAggregates) = Make(Veteran());

        await plain.Fold(HandIn(), new StateObject(), Prices);
        await late.FoldLateReward(HandIn(), new StateObject(), Prices);

        plainAggregates.Stored.ItemCounts.Should().NotContainKey("AGATHA_COUPON", "a 16 second period is rejected as too short");
        lateAggregates.Stored.ItemCounts.Should().ContainKey("AGATHA_COUPON");
    }

    [Test]
    public async Task FoldLateReward_IsNotWinsorizedAgainstTheBucketRate()
    {
        var (folder, aggregates) = Make(Veteran());
        // a bucket with 3 hours of plain logs worth 100/h: the cap for a 0 hour period would be 0 coins
        var bucket = new BucketAggregate { WSeconds = 3 * 3600, WPeriods = 10, ItemCounts = new() { { "FIG_LOG", 3 } } };
        var snapshot = Enumerable.Range(0, StatScoreService.BucketCount)
            .ToDictionary(b => ("Fig Foraging", (byte)b), b => bucket);
        typeof(TaskAggregateService).GetField("snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(aggregates, snapshot);
        typeof(TaskAggregateService).GetField("snapshotAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(aggregates, DateTime.UtcNow);
        var prices = new Dictionary<string, double>(Prices) { { "FIG_LOG", 100 } };

        await folder.FoldLateReward(HandIn(), new StateObject(), prices);

        aggregates.Stored.ItemCounts["AGATHA_COUPON"].Should().BeApproximately(30, 1e-6, "the late reward is not scaled down by the winsor cap");
    }
}
