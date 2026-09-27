using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskRegistryTests
{
    private static readonly string[] HiddenTaskNames = ["Bazaar Purchases", "Auction Purchases", "Minion Collection"];

    [Test]
    public void PublicMethodTasks_ExcludesHiddenTasks()
    {
        var registry = new TaskRegistry();

        var names = registry.PublicMethodTasks.Select(t => t.GetDetectionSignature().MethodName).ToList();

        names.Should().NotContain(HiddenTaskNames, "hidden accounting tasks must never be offered to players");
        names.Should().NotBeEmpty();
    }

    [Test]
    public void PublicTasks_ExcludesHiddenMethodTasks_ButKeepsNonMethodTasks()
    {
        var registry = new TaskRegistry();

        var names = registry.PublicTasks.Select(t => t.Name).ToList();

        names.Should().NotContain(HiddenTaskNames);
        // Kat/Forge/Composter etc. have no Hidden concept and always pass through.
        names.Should().Contain("Kat");
    }

    [Test]
    public void Tasks_And_MethodTasks_StillIncludeHiddenTasks()
    {
        // Hidden tasks must still fully participate in classification/aggregation - only the
        // user-facing accessors (PublicTasks/PublicMethodTasks) filter them out.
        var registry = new TaskRegistry();

        var allNames = registry.MethodTasks.Select(t => t.GetDetectionSignature().MethodName).ToList();

        allNames.Should().Contain(HiddenTaskNames);
    }

    [Test]
    public void GetByName_StillResolvesHiddenTasks()
    {
        var registry = new TaskRegistry();

        var task = registry.GetByName("Minion Collection");

        task.Should().NotBeNull("ExecuteOne by name deliberately keeps returning hidden tasks for debugging");
        (task as MethodTask)?.IsHidden.Should().BeTrue();
    }
}
