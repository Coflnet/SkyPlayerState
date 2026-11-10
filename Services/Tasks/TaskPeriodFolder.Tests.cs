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
    public void FilterOwnedItems_DropsPurchaseCoinsFromActivityTask_ButKeepsThemForThePurchaseTask()
    {
        var items = new Dictionary<string, int> { { PseudoItems.BAZAAR_PURCHASE, 9_269_101 }, { "MANGROVE_LOG", 93 } };

        TaskPeriodFolder.FilterOwnedItems(items, new M7Task()).Should().NotContainKey(PseudoItems.BAZAAR_PURCHASE)
            .And.ContainKey("MANGROVE_LOG");
        TaskPeriodFolder.FilterOwnedItems(items, new BazaarPurchaseTask()).Should().ContainKey(PseudoItems.BAZAAR_PURCHASE);
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
        var items = new Dictionary<string, int> { { "SUSPICIOUS_SCRAP", -2 }, { "ESSENCE_FOSSIL", 600 } };
        var task = new FossilExcavationTask();

        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

        owned.Should().ContainKey("SUSPICIOUS_SCRAP").WhoseValue.Should().Be(-2,
            "FossilExcavationTask declares SUSPICIOUS_SCRAP in FormulaCosts, so its negative count is a real cost, not noise");
        owned.Should().ContainKey("ESSENCE_FOSSIL").WhoseValue.Should().Be(600);
    }

    [Test]
    public void FilterOwnedItems_KeepsNegativeObservedCostItemOfACatacombsFloor_AndDropsOtherNegatives()
    {
        var items = new Dictionary<string, int>
        {
            { "KISMET_FEATHER", -2 }, { "ENDER_PEARL", -3 }, { "ESSENCE_WITHER", 600 }
        };

        foreach (MethodTask task in new MethodTask[] { new M7Task(), new F3Task(), new M4Task() })
        {
            var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

            owned.Should().ContainKey("KISMET_FEATHER").WhoseValue.Should().Be(-2, task.Name);
            owned.Should().NotContainKey("ENDER_PEARL", task.Name);
            owned.Should().ContainKey("ESSENCE_WITHER");
        }
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
        // KISMET_FEATHER only counts as a declared cost for the tasks that actually declare it (Catacombs floors) -
        // the same negative count under a different (unrelated) classified task is just dropped.
        var items = new Dictionary<string, int> { { "KISMET_FEATHER", -2 }, { "ESSENCE_WITHER", 300 } };
        var task = new MangroveForagingTask(); // does not declare KISMET_FEATHER as a cost

        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);

        owned.Should().NotContainKey("KISMET_FEATHER");
    }

    /// <summary>
    /// Regression: forge outputs (78% of Mithril Mining's rare pool) counted as drops of the task around the claim, and the
    /// first fix dropped the keys and plates frozen corpses really drop in a Glacite Mineshaft.
    /// </summary>
    [Test]
    public void FilterOwnedItems_ForgeOutputs_AreLootOnlyOfATaskThatDropsThem()
    {
        var items = new Dictionary<string, int>
        {
            { "MITHRIL_ORE", 3000 }, { "MITHRIL_PLATE", 1 }, { "TUNGSTEN_KEY", 1 }, { "SKELETON_KEY", 1 }, { "UMBER_PLATE", 1 },
            { "REFINED_UMBER", 2 }, { "PERFECT_PLATE", 1 }
        };

        TaskPeriodFolder.FilterOwnedItems(items, new MithrilMiningTask()).Keys.Should().BeEquivalentTo(new[] { "MITHRIL_ORE" });
        // corpse loot: keys, plates, Skeleton Key, refined umber; a Perfect Plate only comes from the Forge
        TaskPeriodFolder.FilterOwnedItems(items, new GlaciteMineshaftTask()).Keys.Should().BeEquivalentTo(new[]
            { "MITHRIL_ORE", "MITHRIL_PLATE", "TUNGSTEN_KEY", "SKELETON_KEY", "UMBER_PLATE", "REFINED_UMBER" });
        TaskPeriodFolder.FilterOwnedItems(items, new ForgeClaimsTask()).Should().HaveCount(7, "the forge's own accounting task keeps them");
    }

    [Test]
    public void FilterOwnedItems_GemstoneMixture_IsCrystalNucleusLoot()
    {
        var items = new Dictionary<string, int> { { "GEMSTONE_MIXTURE", 1 }, { "DIVAN_FRAGMENT", 1 } };

        TaskPeriodFolder.FilterOwnedItems(items, new NucleusMiningTask()).Should().ContainKey("GEMSTONE_MIXTURE");
        TaskPeriodFolder.FilterOwnedItems(items, new RubyMiningTask()).Should().NotContainKey("GEMSTONE_MIXTURE");
    }

    /// <summary>Regression: Summoning Eyes bought during a dragon session counted as dragon loot.</summary>
    [Test]
    public void FilterOwnedItems_InputBoughtInTheSamePeriod_IsNotLoot()
    {
        var bought = new Dictionary<string, int>
        {
            { PseudoItems.BAZAAR_PURCHASE, 4_800_000 }, { "SUMMONING_EYE", 4 }, { "STRONG_FRAGMENT", 8 }
        };

        TaskPeriodFolder.FilterOwnedItems(bought, new EnderDragonTask()).Keys.Should().BeEquivalentTo(new[] { "STRONG_FRAGMENT" });
        TaskPeriodFolder.FilterOwnedItems(new() { { "SUMMONING_EYE", 4 }, { "STRONG_FRAGMENT", 8 } }, new EnderDragonTask())
            .Should().ContainKey("SUMMONING_EYE", "without purchase evidence the eyes may be Zealot drops of the same session");
    }

    /// <summary>Regression: a corpse-droppable key (rare drop) bought for a mineshaft counted as loot, cancelling its cost.</summary>
    [Test]
    public void FilterOwnedItems_KeyBoughtForAMineshaft_IsNotLoot()
    {
        var bought = new Dictionary<string, int> { { PseudoItems.BAZAAR_PURCHASE, 2_000_000 }, { "UMBER_KEY", 1 }, { "REFINED_UMBER", 2 } };

        TaskPeriodFolder.FilterOwnedItems(bought, new GlaciteMineshaftTask()).Should().NotContainKey("UMBER_KEY")
            .And.ContainKey("REFINED_UMBER");
        TaskPeriodFolder.FilterOwnedItems(new() { { "UMBER_KEY", 1 } }, new GlaciteMineshaftTask())
            .Should().ContainKey("UMBER_KEY", "without purchase evidence the key is a corpse drop");
    }

    [Test]
    public void FilterOwnedItems_BoughtSomething_KeepsTheTasksOwnDropsAmongItsInputs()
    {
        var items = new Dictionary<string, int> { { PseudoItems.BAZAAR_PURCHASE, 1_000_000 }, { "CONTROL_SWITCH", 2 } };

        TaskPeriodFolder.FilterOwnedItems(items, new AutomatonTask()).Should().Contain("CONTROL_SWITCH", 2,
            "the parts are Automaton drops (formula) as well as the apparatus' ingredients");
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

/// <summary>
/// Regression: hand-ins, used Skeleton Keys and crafted Automaton parts were dropped as unexplained negatives. They are
/// costs only with full value rates: the switch-off AFK check compares the net value and would drop exactly the sessions
/// that paid them (every Vanguard session, -28M key).
/// </summary>
[NonParallelizable]
public class TaskPeriodFolderFullValueCostTests
{
    [TearDown]
    public void Reset() => TaskRateOptions.FullValueRates = false;

    private static readonly Dictionary<string, int> HandIns = new()
    {
        { "WHEAT", -640 }, { "ENCHANTED_POTATO", -32 }, { "OVERCLOCKER_3000", 1 }, { PseudoItems.COPPER, 50 }
    };

    private static readonly Dictionary<string, int> ApparatusCraft = new()
    {
        { "PRECURSOR_APPARATUS", 1 }, { "CONTROL_SWITCH", -1 }, { "ELECTRON_TRANSMITTER", -1 }, { "FTX_3070", -1 },
        { "ROBOTRON_REFLECTOR", -1 }, { "SUPERLITE_MOTOR", -1 }, { "SYNTHETIC_HEART", -1 }
    };

    private static readonly Dictionary<string, int> VanguardCorpse = new() { { "SKELETON_KEY", -1 }, { "GLACITE_JEWEL", 3 } };

    [Test]
    public void SwitchOn_GardenVisitorHandIns_AreCosts()
    {
        TaskRateOptions.FullValueRates = true;

        TaskPeriodFolder.FilterOwnedItems(HandIns, new GardenVisitorsTask()).Should()
            .Contain("WHEAT", -640).And.Contain("ENCHANTED_POTATO", -32).And.Contain("OVERCLOCKER_3000", 1);
        TaskPeriodFolder.FilterOwnedItems(HandIns, new WheatFarmingTask()).Should().NotContainKey("WHEAT", "only the visitor task pays with any item");
    }

    [Test]
    public void SwitchOn_UsedSkeletonKeyAndCraftedParts_AreCosts()
    {
        TaskRateOptions.FullValueRates = true;

        TaskPeriodFolder.FilterOwnedItems(VanguardCorpse, new GlaciteMineshaftTask()).Should().Contain("SKELETON_KEY", -1);
        TaskPeriodFolder.FilterOwnedItems(ApparatusCraft, new AutomatonTask()).Should().BeEquivalentTo(ApparatusCraft);
    }

    [Test]
    public void SwitchOff_NewCostsAreDropped_AsBefore()
    {
        TaskPeriodFolder.FilterOwnedItems(HandIns, new GardenVisitorsTask()).Keys
            .Should().BeEquivalentTo(new[] { "OVERCLOCKER_3000", PseudoItems.COPPER });
        TaskPeriodFolder.FilterOwnedItems(VanguardCorpse, new GlaciteMineshaftTask()).Should().NotContainKey("SKELETON_KEY");
        TaskPeriodFolder.FilterOwnedItems(ApparatusCraft, new AutomatonTask()).Keys.Should().BeEquivalentTo(new[] { "PRECURSOR_APPARATUS" });
    }

    [Test]
    public void SwitchOff_DeclaredCostsStillCount()
        => TaskPeriodFolder.FilterOwnedItems(new() { { "TUNGSTEN_KEY", -1 }, { "GLACITE_JEWEL", 3 } }, new GlaciteMineshaftTask())
            .Should().Contain("TUNGSTEN_KEY", -1);

    /// <summary>
    /// The switch-off AFK check on the net value: a Vanguard session whose loot is worth less than its key would be dropped
    /// as AFK if the key counted. Off it does not count, on the check uses the gross gains.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void VanguardSession_IsNotDroppedAsAfk(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var prices = new Dictionary<string, double> { ["GLACITE_JEWEL"] = 1_000_000, ["SKELETON_KEY"] = 28_000_000 };
        var task = new GlaciteMineshaftTask();
        var bucket = new BucketAggregate { WSeconds = 10 * 3600, ItemCounts = new() { ["GLACITE_JEWEL"] = 100 } };
        var owned = TaskPeriodFolder.FilterOwnedItems(VanguardCorpse, task);
        var counts = owned.ToDictionary(e => e.Key, e => (double)e.Value);
        var net = counts.Sum(e => prices[e.Key] * e.Value);

        TaskPeriodFolder.IsAfk(task, bucket, counts, net, 0.25, prices).Should().BeFalse();
    }

    /// <summary>The personal view lists the new costs under the same switch.</summary>
    [Test]
    public void IsConsumedCost_FollowsTheSwitch_ForTheNewCostsOnly()
    {
        var task = new GlaciteMineshaftTask();
        var costTags = task.KeepableCostTags();

        task.IsConsumedCost("SKELETON_KEY", -1, costTags).Should().BeFalse();
        task.IsConsumedCost("UMBER_KEY", -1, costTags).Should().BeTrue();
        TaskRateOptions.FullValueRates = true;
        task.IsConsumedCost("SKELETON_KEY", -1, costTags).Should().BeTrue();
    }

    /// <summary>The Automaton parts' cost must not make a consumed part a classification signal.</summary>
    [Test]
    public void AutomatonPartCosts_AreNoClassificationSignal()
    {
        TaskRateOptions.FullValueRates = true;

        new AutomatonTask().GetDetectionSignature().CountsAsSignal("CONTROL_SWITCH", -1).Should().BeFalse();
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
