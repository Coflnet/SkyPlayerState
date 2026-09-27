using Coflnet.Sky.PlayerState.Models;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression coverage for <see cref="MethodTask.ComputeFromPlayerData"/>'s pseudo-tag handling
/// (<see cref="PseudoItems"/>): EVIDENCE tags (bazaar/auction purchase) must never render as a drop
/// in a REAL task's own breakdown (e.g. a wheat farmer who also bought seeds), and COST tags (e.g.
/// DUNGEON_CHEST_COST) must render as a cost line instead of a drop.
/// </summary>
public class MethodTaskPseudoBreakdownTests
{
    private static Period MakePeriod(string location, Dictionary<string, int> items, int minutesDuration = 10)
    {
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        return new Period
        {
            PlayerUuid = "test",
            Server = "m1",
            Location = location,
            Profit = 0,
            StartTime = start,
            EndTime = start.AddMinutes(minutesDuration),
            ItemsCollected = items
        };
    }

    private static TaskParams MakeParams(Dictionary<string, long> prices, params Period[] periods)
    {
        return new TaskParams
        {
            TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            MaxAvailableCoins = 1_000_000_000,
            LocationProfit = periods.GroupBy(l => l.Location).ToDictionary(l => l.Key, l => l.ToArray()),
            CleanPrices = prices,
            BazaarPrices = [],
            Names = new Dictionary<string, string>()
        };
    }

    [Test]
    public async Task BazaarPurchaseEvidence_NeverShownAsDropOnARealTasksOwnBreakdown()
    {
        // A wheat farmer who also bought seeds on the Bazaar during the same window - the purchase
        // is real evidence for the classifier, but must never render as one of THIS task's drops.
        var period = MakePeriod("The Garden", new Dictionary<string, int>
        {
            { "WHEAT", 3000 }, { PseudoItems.BAZAAR_PURCHASE, 20_000 }
        });
        var task = new WheatFarmingTask();

        var result = await task.Execute(MakeParams(new Dictionary<string, long> { { "WHEAT", 10 } }, period));

        result.Breakdown.Drops.Should().NotContain(d => d.ItemTag == PseudoItems.BAZAAR_PURCHASE);
        result.Breakdown.Drops.Should().ContainSingle(d => d.ItemTag == "WHEAT");
        result.Breakdown.Costs.Should().BeEmpty("no COST pseudo tag was present in this period");
    }

    [Test]
    public async Task DungeonChestCost_RendersAsACostLine_NotADrop()
    {
        var period = MakePeriod("The Catacombs (M7)", new Dictionary<string, int>
        {
            { "ESSENCE_WITHER", 100 }, { PseudoItems.DUNGEON_CHEST_COST, -200 }
        });
        var task = new M7Task();

        var result = await task.Execute(MakeParams(new Dictionary<string, long> { { "ESSENCE_WITHER", 50 } }, period));

        result.Breakdown.Drops.Should().NotContain(d => d.ItemTag == PseudoItems.DUNGEON_CHEST_COST);
        result.Breakdown.Costs.Should().ContainSingle(d => d.ItemTag == PseudoItems.DUNGEON_CHEST_COST);
        var cost = result.Breakdown.Costs.Single(d => d.ItemTag == PseudoItems.DUNGEON_CHEST_COST);
        cost.PriceEach.Should().Be(1);
        cost.ContributionPerHour.Should().BeLessThan(0, "an already-negative cost count must render as a negative contribution");
    }
}
