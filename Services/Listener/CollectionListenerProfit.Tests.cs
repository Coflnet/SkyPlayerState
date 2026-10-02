using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Regression tests for <see cref="CollectionListener.ComputeProfit"/> - the profit valuation
/// StoreLocationProfit applies to a flushed location fragment, routed through
/// <see cref="Tasks.PseudoItems"/> so purchase evidence never inflates profit and a fixed cost
/// reduces it by exactly what was spent.
/// </summary>
public class CollectionListenerProfitTests
{
    [Test]
    public void AuctionPurchaseEvidence_DoesNotChangeProfit()
    {
        var collected = new Dictionary<string, int>
        {
            { "HYPERION", 1 },
            { Tasks.PseudoItems.AUCTION_PURCHASE, 970_000_000 }
        };
        var prices = new Dictionary<string, double> { { "HYPERION", 970_000_000 } };

        var profit = CollectionListener.ComputeProfit(collected, prices);

        profit.Should().Be(970_000_000, "an AUCTION_PURCHASE evidence tag has coin value 0 - buying is not profit");
    }

    [Test]
    public void DungeonChestCost_ReducesProfitByExactlyWhatWasSpent()
    {
        var collected = new Dictionary<string, int>
        {
            { Tasks.PseudoItems.DUNGEON_CHEST_COST, -2_000_000 },
            { "RECOMBOBULATOR_3000", 1 }
        };
        var prices = new Dictionary<string, double> { { "RECOMBOBULATOR_3000", 9_000_000 } };

        var profit = CollectionListener.ComputeProfit(collected, prices);

        profit.Should().Be(7_000_000, "DUNGEON_CHEST_COST is worth exactly 1 coin/unit and its count is already negative");
    }

    [TestCase("Wyld Woods", "Wyld Woods")]
    [TestCase("Wizard Tower", "The Rift")]
    [TestCase("Some Unregistered Rift Zone", "The Rift")]
    public async Task RiftScoreboard_UsesRegisteredRiftZoneOrLiteralTheRift(string riftArea, string expectedLocation)
    {
        var state = new Models.StateObject();
        state.ExtractedInfo.CurrentLocation = "Wizard Tower";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddMinutes(-3);
        var args = new Tests.MockedUpdateArgs
        {
            currentState = state,
            msg = new Models.UpdateMessage
            {
                Kind = Models.UpdateMessage.UpdateKind.Scoreboard,
                Scoreboard = new[] { "[SKYBLOCK]", $" {ScoreboardParser.AreaGlyphRift} {riftArea}", "Motes: 12,345" }
            }
        };

        await new CollectionListener().Process(args);

        state.ExtractedInfo.CurrentLocation.Should().Be(expectedLocation);
        state.ExtractedInfo.LastLocationChange.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1), "the Wizard Tower period was flushed");
    }
}
