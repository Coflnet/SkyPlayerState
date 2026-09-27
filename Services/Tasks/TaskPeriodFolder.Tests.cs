using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

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
