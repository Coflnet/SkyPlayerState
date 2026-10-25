using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tasks;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tests;

public class TaskControllerAggregateTests
{
    [Test]
    public void BuildAggregateInfo_ReturnsBucketsOfTheTaskWithRateParts()
    {
        var task = (MethodTask)new TaskRegistry().GetByName("Diana");
        var prices = new Dictionary<string, double> { ["X"] = 1000 };
        var snapshot = new Dictionary<(string task, byte bucket), BucketAggregate>
        {
            [("Diana", 2)] = new() { WSeconds = 7200, WPeriods = 4, RareCoins = 1_000_000, RefItemValue = 100_000,
                ItemCounts = new() { ["X"] = 100 }, Contributors = new() { "a", "b" } },
            [("Diana", 0)] = new() { WSeconds = 3600, ItemCounts = new() { ["X"] = 10 } },
            [("Other", 0)] = new() { WSeconds = 3600 },
        };

        var result = TaskController.BuildAggregateInfo(task, snapshot, prices);

        result.Should().HaveCount(2);
        result[0].Bucket.Should().Be(0);
        var b = result[1];
        b.Bucket.Should().Be(2);
        b.Hours.Should().Be(2);
        b.Contributors.Should().Be(2);
        b.LiveItemValue.Should().Be(100_000);
        b.DriftScale.Should().Be(1);
        b.ScaledRareCoins.Should().Be(1_000_000);
        b.CoinsPerHour.Should().BeApproximately((100_000 - b.FormulaCost + 1_000_000) / 2.0, 1e-6);
        b.CoinsPerHour.Should().BeApproximately(TaskEstimator.RateFromAggregate(task, snapshot[("Diana", 2)], prices), 1e-6);
    }
}
