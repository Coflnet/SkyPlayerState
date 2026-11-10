using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: Crystal Nucleus was selected by its outcome (a Divan Fragment anywhere in the Crystal Hollows, 46M/h).
/// Limited to the zone, a session crossing the nucleus is cut on entering it, and the claim visit itself is too short to
/// classify: no period covers the run that earned the claim. The task is therefore hidden (no measured rate), and the
/// nucleus-only Divan Fragment is not loot of the session the claim merges into.
/// </summary>
public class CrystalNucleusSessionTests
{
    private static readonly TaskSessionService Service = new(new TaskClassifier(new TaskRegistry()));
    private const string Player = "00000000000000000000000000000001";
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, double> Prices = new()
    {
        ["DIVAN_FRAGMENT"] = 1_300_000, ["HARD_STONE"] = 2, ["TREASURITE"] = 3000
    };

    private static Period Fragment(string location, Dictionary<string, int> items, int fromMinute, int toMinute) => new()
    {
        PlayerUuid = Player, Location = location, Server = "test",
        StartTime = T0.AddMinutes(fromMinute), EndTime = T0.AddMinutes(toMinute),
        ItemsCollected = items
    };

    private static Period Accumulate(ExtractedInfo info, Period fragment)
        => Service.Accumulate(info, Player, fragment, null, Prices, fragment.EndTime);

    [Test]
    public void RunThroughTheNucleus_NeverBecomesANucleusPeriod_AndTheFragmentIsNotTheNextTasksLoot()
    {
        var info = new ExtractedInfo();
        Accumulate(info, Fragment("Mines of Divan", new() { ["HARD_STONE"] = 400, ["TREASURITE"] = 2 }, 0, 10))
            .Should().BeNull("the hunt is still running");

        var hunt = Accumulate(info, Fragment("Crystal Nucleus", new() { ["DIVAN_FRAGMENT"] = 1, ["HARD_STONE"] = 5 }, 10, 12));
        var afterClaim = Accumulate(info, Fragment("Jungle", new() { ["HARD_STONE"] = 300 }, 12, 20));
        var next = Service.Accumulate(info, Player, Fragment("Jungle", new(), 0, 0), null, Prices, T0.AddMinutes(30));

        hunt.Should().NotBeNull("entering the nucleus with its loot changes the classification");
        hunt.DetectedTask.Should().NotBe("Crystal Nucleus");
        hunt.ItemsCollected.Should().NotContainKey("DIVAN_FRAGMENT");
        afterClaim.Should().BeNull("the 2 minute claim visit is below the classifier's minimum and merges into the next session");
        next.DetectedTask.Should().NotBe("Crystal Nucleus");
        next.ItemsCollected.Should().ContainKey("DIVAN_FRAGMENT");
        var task = (MethodTask)new TaskRegistry().GetByName(next.DetectedTask);
        TaskPeriodFolder.FilterOwnedItems(next.ItemsCollected, task).Should().NotContainKey("DIVAN_FRAGMENT")
            .And.ContainKey("HARD_STONE");
    }

    [Test]
    public void DivanFragmentDuringAHollowsSession_DoesNotMakeItNucleus()
    {
        var info = new ExtractedInfo();
        Accumulate(info, Fragment("Mines of Divan", new() { ["HARD_STONE"] = 400 }, 0, 10));
        Accumulate(info, Fragment("Mines of Divan", new() { ["DIVAN_FRAGMENT"] = 1, ["HARD_STONE"] = 50 }, 10, 15));

        info.CurrentSession.DetectedTask.Should().NotBe("Crystal Nucleus");
        info.CurrentSession.Items.Should().ContainKey("DIVAN_FRAGMENT");
    }

    [Test]
    public void CrystalNucleus_PublishesNoMeasuredRate()
        => new TaskRegistry().PublicMethodTasks.Select(TaskEstimator.GetTaskName).Should().NotContain("Crystal Nucleus");
}
