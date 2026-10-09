using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using AwesomeAssertions;
using Moq;
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

    /// <summary>
    /// Production 2026-10: Kuudra runs of ~313 s holding only "FISH_BAIT:0" were stored as a period, so the empty
    /// run time was never handed to the claim period and the tier task lost the run.
    /// </summary>
    [Test]
    public async Task ZeroCountEntryDoesNotHideAnEmptyKuudraRun()
    {
        var state = new Models.StateObject();
        state.ExtractedInfo.CurrentLocation = "Kuudra's Hollow (T2)";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddMinutes(-5);
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow.AddSeconds(-5);
        state.ExtractedInfo.LastKuudraTierAt = DateTime.UtcNow.AddMinutes(-5);
        state.ItemsCollectedRecently["FISH_BAIT"] = 0;
        var args = new Tests.MockedUpdateArgs
        {
            currentState = state,
            msg = new Models.UpdateMessage
            {
                Kind = Models.UpdateMessage.UpdateKind.Scoreboard,
                Scoreboard = new[] { "[SKYBLOCK]", $" {ScoreboardParser.AreaGlyphPrivateUse} Forgotten Skull", "Purse: 1" }
            }
        };

        await new CollectionListener().Process(args);

        state.ExtractedInfo.KuudraPendingRunSeconds.Should().BeGreaterThan(200, "the empty run's time waits for the claim period");
        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    private static (Models.StateObject, Tests.MockedUpdateArgs, Moq.Mock<IRunLengthRecorder>) RunLeaveHarness(string zone, bool completed)
    {
        var state = new Models.StateObject();
        var now = DateTime.UtcNow;
        state.ExtractedInfo.CurrentLocation = zone;
        state.ExtractedInfo.CurrentLocationSince = now.AddMinutes(-8);
        state.ExtractedInfo.CurrentLocationSeenAt = now.AddSeconds(-2);
        state.ExtractedInfo.LastLocationChange = now.AddMinutes(-2);
        if (completed)
        {
            state.ExtractedInfo.LastDungeonRunCompletedAt = now.AddMinutes(-1);
            state.ExtractedInfo.LastKuudraRunCompletedAt = now.AddMinutes(-1);
        }
        var recorder = new Moq.Mock<IRunLengthRecorder>();
        var args = new Tests.MockedUpdateArgs
        {
            currentState = state,
            msg = new Models.UpdateMessage
            {
                Kind = Models.UpdateMessage.UpdateKind.Scoreboard,
                Scoreboard = new[] { "[SKYBLOCK]", $" {ScoreboardParser.AreaGlyphPrivateUse} Dungeon Hub", "Purse: 1" }
            }
        };
        args.AddService(recorder.Object);
        return (state, args, recorder);
    }

    [Test]
    public async Task CompletedDungeonRun_IsRecordedWhenLeavingTheFloor()
    {
        var (_, args, recorder) = RunLeaveHarness("The Catacombs (M7)", completed: true);

        await new CollectionListener().Process(args);

        recorder.Verify(r => r.Record("dungeon:M7", It.Is<TimeSpan>(t => t > TimeSpan.FromMinutes(7)), RunLengthBounds.Dungeon), Moq.Times.Once);
    }

    [Test]
    public async Task AbandonedDungeonRun_WithoutResultsHeader_IsNotRecorded()
    {
        var (_, args, recorder) = RunLeaveHarness("The Catacombs (F7)", completed: false);

        await new CollectionListener().Process(args);

        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Never);
    }

    [Test]
    public async Task CompletedKuudraRun_IsRecordedUnderItsTier()
    {
        var (_, args, recorder) = RunLeaveHarness("Kuudra's Hollow (T2)", completed: true);

        await new CollectionListener().Process(args);

        recorder.Verify(r => r.Record("kuudra:T2", It.IsAny<TimeSpan>(), RunLengthBounds.Kuudra), Moq.Times.Once);
    }

    [Test]
    public async Task KuudraStayWithoutKuudraDown_IsNotRecorded()
    {
        // production: failed runs and lobby hops dragged the median down; a dungeon completion is no Kuudra completion
        var (state, args, recorder) = RunLeaveHarness("Kuudra's Hollow (T5)", completed: false);
        state.ExtractedInfo.LastDungeonRunCompletedAt = DateTime.UtcNow.AddMinutes(-1);

        await new CollectionListener().Process(args);

        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Never);
    }

    [Test]
    public async Task KuudraDownFromAnEarlierStay_DoesNotCompleteTheNextOne()
    {
        var (state, args, recorder) = RunLeaveHarness("Kuudra's Hollow (T5)", completed: false);
        state.ExtractedInfo.LastKuudraRunCompletedAt = state.ExtractedInfo.CurrentLocationSince.AddMinutes(-3);

        await new CollectionListener().Process(args);

        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Never);
    }

    private static Tests.MockedUpdateArgs ChatArgs(Models.StateObject state, Moq.Mock<IRunLengthRecorder> recorder, params string[] lines)
    {
        var args = new Tests.MockedUpdateArgs
        {
            currentState = state,
            msg = new Models.UpdateMessage { Kind = Models.UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ChatBatch = lines.ToList() }
        };
        args.AddService(recorder.Object);
        return args;
    }

    [TestCase("                        KUUDRA DOWN!", true)]
    [TestCase("§r                        §a§lKUUDRA DOWN!§r", true)]
    [TestCase("   §c§lDEFEAT", false)]
    [TestCase("DEFEAT!", null)]
    [TestCase("Kuudra down", null)]
    [TestCase("", null)]
    public void KuudraEndBanner_IsParsedAfterStrippingColourAndWhitespace(string line, bool? expected)
    {
        CollectionListener.ParseKuudraEnd(line).Should().Be(expected);
    }

    [Test]
    public async Task KuudraDownInTheTierZone_CompletesTheStay_AndDefeatDoesNot()
    {
        var recorder = new Moq.Mock<IRunLengthRecorder>();
        var state = new Models.StateObject();
        state.ExtractedInfo.CurrentLocation = "Kuudra's Hollow (T3)";
        state.ExtractedInfo.CurrentLocationSince = DateTime.UtcNow.AddMinutes(-8);

        await new CollectionListener().Process(ChatArgs(state, recorder, "                        DEFEAT"));
        state.ExtractedInfo.LastKuudraRunCompletedAt.Should().Be(default, "a defeat is no completion");

        await new CollectionListener().Process(ChatArgs(state, recorder, "                        KUUDRA DOWN!"));
        state.ExtractedInfo.LastKuudraRunCompletedAt.Should().BeAfter(DateTime.UtcNow.AddSeconds(-5));
        CollectionListener.IsCompletedKuudraRun(state.ExtractedInfo).Should().BeTrue();
    }

    [Test]
    public async Task KuudraDownJustAfterLeavingTheTier_StillCreditsTheRunThatEnded()
    {
        var (state, args, recorder) = RunLeaveHarness("Kuudra's Hollow (T4)", completed: false);
        await new CollectionListener().Process(args);
        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Never);
        state.ExtractedInfo.CurrentLocation.Should().Be("Dungeon Hub");

        await new CollectionListener().Process(ChatArgs(state, recorder, "                        KUUDRA DOWN!"));

        recorder.Verify(r => r.Record("kuudra:T4", It.Is<TimeSpan>(t => t > TimeSpan.FromMinutes(7)), RunLengthBounds.Kuudra), Moq.Times.Once);
        state.ExtractedInfo.UnrecordedRunKey.Should().BeNull();
        // and it is credited once only
        await new CollectionListener().Process(ChatArgs(state, recorder, "                        KUUDRA DOWN!"));
        recorder.Verify(r => r.Record("kuudra:T4", It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Once);
    }

    [Test]
    public async Task KuudraDownLongAfterLeaving_CreditsNothing()
    {
        var (state, args, recorder) = RunLeaveHarness("Kuudra's Hollow (T4)", completed: false);
        await new CollectionListener().Process(args);
        state.ExtractedInfo.UnrecordedRunEndedAt = DateTime.UtcNow - RunLengthCredit.LateCompletionWindow - TimeSpan.FromSeconds(5);

        await new CollectionListener().Process(ChatArgs(state, recorder, "                        KUUDRA DOWN!"));

        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds>()), Moq.Times.Never);
    }

    [TestCase("Kuudra's Hollow (T1)", "kuudra:T1")]
    [TestCase("Kuudra's Hollow (T5)", "kuudra:T5")]
    [TestCase("Dungeon Hub", null)]
    [TestCase(null, null)]
    public void KuudraKeys(string zone, string expected)
    {
        RunLengthKeys.ForKuudraZone(zone).Should().Be(expected);
    }
}
