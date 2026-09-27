using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Tasks;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Controllers;

public class TaskControllerTests
{
    [Test]
    public void GetTrackedHours_SumsAllBucketsPerTask()
    {
        var snapshot = new Dictionary<(string task, byte bucket), BucketAggregate>
        {
            [("Lotus Atoll", 0)] = new() { WSeconds = 1800 },
            [("Lotus Atoll", 1)] = new() { WSeconds = 5400 },
            [("Another Task", 0)] = new() { WSeconds = 3600 }
        };

        var hours = TaskController.GetTrackedHours(snapshot);

        hours["Lotus Atoll"].Should().Be(2);
        hours["Another Task"].Should().Be(1);
    }

    [Test]
    public void GetMethods_ExcludesHiddenAccountingTasks()
    {
        // Only the registry is needed - GetMethods reads nothing else off the controller.
        var controller = new TaskController(null!, null!, null!, null!, null!, null!, new TaskRegistry(), null!);

        var methods = controller.GetMethods();

        methods.Select(m => m.Name).Should().NotContain(["Bazaar Purchases", "Auction Purchases", "Minion Collection"],
            "hidden accounting tasks (HiddenTasks.cs) must never appear in /Task/methods");
        methods.Should().NotBeEmpty();
    }
}
