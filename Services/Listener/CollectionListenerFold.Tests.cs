using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// One flush can return several periods (a closed Crystal Nucleus run, released run sessions, the session flush). They
/// are already out of the persisted state, so a failing fold of one must not lose the others.
/// </summary>
public class CollectionListenerFoldTests
{
    [Test]
    public async Task FoldEach_AFailingFold_DoesNotStopTheOthers()
    {
        var periods = new List<Period>
        {
            new() { DetectedTask = "Jade Mining" }, new() { DetectedTask = "Crystal Nucleus" }, new() { DetectedTask = "Amber Mining" }
        };
        var folded = new List<string>();

        await CollectionListener.FoldEach(periods, period =>
        {
            if (period.DetectedTask == "Crystal Nucleus")
                throw new InvalidOperationException("fold failed");
            folded.Add(period.DetectedTask);
            return Task.CompletedTask;
        }, NullLogger.Instance);

        folded.Should().Equal("Jade Mining", "Amber Mining");
    }
}
