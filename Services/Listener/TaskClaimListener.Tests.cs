using System;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class TaskClaimListenerTests
{
    private static MockedUpdateArgs Args(string claim) => new()
    {
        currentState = new StateObject(),
        msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.TaskClaim, ClaimedTask = claim }
    };

    [Test]
    public void SetsClaimedTask()
    {
        var args = Args("Bayou Fishing");
        new TaskClaimListener().Process(args);
        args.currentState.ExtractedInfo.ClaimedTask.Should().Be("Bayou Fishing");
        args.currentState.ExtractedInfo.ClaimedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NullOrBlankClaimClearsIt()
    {
        var args = Args(null);
        args.currentState.ExtractedInfo.ClaimedTask = "Old Task";
        new TaskClaimListener().Process(args);
        args.currentState.ExtractedInfo.ClaimedTask.Should().BeNull();

        var blank = Args("   ");
        blank.currentState.ExtractedInfo.ClaimedTask = "Old Task";
        new TaskClaimListener().Process(blank);
        blank.currentState.ExtractedInfo.ClaimedTask.Should().BeNull();
    }
}
