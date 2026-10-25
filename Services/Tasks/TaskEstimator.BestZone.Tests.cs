using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Best spot selection and its step text.</summary>
public class TaskEstimatorBestZoneTests
{
    private static ZoneAggregate Zone(string name, double hours, double coinsPerHour, int players) => new()
    {
        Zone = name, WSeconds = hours * 3600, Value = hours * coinsPerHour,
        Contributors = Enumerable.Range(0, players).Select(i => $"p{i}").ToHashSet()
    };

    private static Dictionary<string, ZoneAggregate> Zones(params ZoneAggregate[] zones) => zones.ToDictionary(z => z.Zone);

    [Test]
    public void HighestRateAmongQualifiedZonesWins()
    {
        var best = TaskEstimator.PickBestZone(Zones(Zone("A", 5, 10e6, 10), Zone("B", 5, 30e6, 10), Zone("C", 5, 20e6, 10)));

        best.Zone.Should().Be("B");
        best.CoinsPerHour.Should().BeApproximately(30e6, 1);
        best.Players.Should().Be(10);
    }

    [Test]
    public void ZoneWithTooFewPlayers_IsNotACandidate()
    {
        var best = TaskEstimator.PickBestZone(Zones(Zone("Rich", 5, 90e6, 2), Zone("Common", 5, 10e6, 5)));

        best.Zone.Should().Be("Common");
    }

    [Test]
    public void ZoneWithTooLittleTime_IsNotACandidate()
    {
        // 15 minutes < 20 minutes
        var best = TaskEstimator.PickBestZone(Zones(Zone("Brief", 0.25, 90e6, 9), Zone("Common", 5, 10e6, 5)));

        best.Zone.Should().Be("Common");
    }

    [Test]
    public void ZoneBelowTenPercentShare_IsNotACandidate()
    {
        // 0.5h of 10.5h total = 4.8%
        var best = TaskEstimator.PickBestZone(Zones(Zone("Sliver", 0.5, 90e6, 9), Zone("Common", 10, 10e6, 5)));

        best.Zone.Should().Be("Common");
    }

    [Test]
    public void NothingQualifiesOrNotPositive_ReturnsNull()
    {
        TaskEstimator.PickBestZone(null).Should().BeNull();
        TaskEstimator.PickBestZone(Zones(Zone("Few", 5, 10e6, 2))).Should().BeNull();
        TaskEstimator.PickBestZone(Zones(Zone("Loss", 5, -1e6, 9))).Should().BeNull();
    }

    [Test]
    public void BestSpotStep_GoesBeforeTheClosingStep_AndStepsAreRenumbered()
    {
        var steps = new List<TaskStep>
        {
            new() { Number = 1, Text = "Go to Ruins." },
            new() { Number = 2, Text = "Kill things.", OnClick = "/warp hub" },
            new() { Number = 3, Text = "Check /cofl task again to see your real coins/hour." }
        };

        var result = TaskEstimator.WithBestSpotStep(steps, [new("Forest", 378_000_000, 12)]);

        result.Select(s => s.Number).Should().Equal(1, 2, 3, 4);
        result[2].Text.Should().Be("Best spot seen: Forest ~378M/h (12 players).");
        result[3].Text.Should().StartWith("Check /cofl task again");
        result[1].OnClick.Should().Be("/warp hub");
        steps.Should().HaveCount(3); // the task's own list is not mutated
    }

    [Test]
    public void BestSpotStep_AppendsWhenThereIsNoClosingStep()
    {
        var result = TaskEstimator.WithBestSpotStep([new() { Number = 1, Text = "Go." }], [new("Forest", 1_500_000, 3)]);

        result.Select(s => s.Number).Should().Equal(1, 2);
        result[1].Text.Should().Be("Best spot seen: Forest ~1.5M/h (3 players).");
    }

    [Test]
    public void RankBestZones_ReturnsTopThreeBestFirst()
    {
        var ranked = TaskEstimator.RankBestZones(Zones(
            Zone("A", 5, 10e6, 10), Zone("B", 5, 30e6, 10), Zone("C", 5, 20e6, 10), Zone("D", 5, 5e6, 10), Zone("Few", 5, 90e6, 2)));

        ranked.Select(r => r.Zone).Should().Equal("B", "C", "A");
    }

    [Test]
    public void BestSpotStep_ListsSeveralSpots()
    {
        var result = TaskEstimator.WithBestSpotStep([new() { Number = 1, Text = "Go." }],
            [new("Mines of Divan", 28_000_000, 45), new("Royal Mines", 11_000_000, 15), new("Gunpowder Mines", 2_700_000, 3)]);

        result[1].Text.Should().Be("Best spots seen: Mines of Divan ~28M/h (45 players), Royal Mines ~11M/h (15 players), Gunpowder Mines ~2.7M/h (3 players).");
    }
}
