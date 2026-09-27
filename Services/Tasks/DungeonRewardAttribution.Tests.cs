using System;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class DungeonRewardAttributionTests
{
    private static readonly DateTime Base = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void DungeonHub_ShortlyAfterF7_ResolvesToF7()
    {
        var lastFloorAt = Base;
        var periodStart = Base.AddMinutes(4); // production median claim delay ~3.7 minutes

        var resolved = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", "The Catacombs (F7)", lastFloorAt, periodStart);

        resolved.Should().Be("The Catacombs (F7)");
    }

    [Test]
    public void DungeonHub_31MinutesAfterFloor_StaysDungeonHub()
    {
        var lastFloorAt = Base;
        var periodStart = Base.AddMinutes(31);

        var resolved = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", "The Catacombs (F7)", lastFloorAt, periodStart);

        resolved.Should().Be("Dungeon Hub", "a claim more than 30 minutes after the last floor visit is too stale to attribute back to it");
    }

    [Test]
    public void DungeonHub_NoKnownFloor_StaysUnchanged()
    {
        var resolved = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", null, default, Base);

        resolved.Should().Be("Dungeon Hub");
    }

    [Test]
    public void NonHubLocation_NeverResolvedEvenWithARecentFloor()
    {
        var resolved = DungeonRewardAttribution.ResolveLocation("The Garden", "The Catacombs (F7)", Base, Base.AddMinutes(1));

        resolved.Should().Be("The Garden");
    }

    [Test]
    public void DungeonHub_ExactlyAtThe30MinuteBoundary_StillResolves()
    {
        var resolved = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", "The Catacombs (M3)", Base, Base.AddMinutes(30));

        resolved.Should().Be("The Catacombs (M3)");
    }

    [Test]
    public void DungeonHub_PeriodStartSlightlyBeforeFloorReading_StillResolvesWithinTolerance()
    {
        // same-tick ordering: the period start can land a hair before LastDungeonFloorAt was stamped
        var resolved = DungeonRewardAttribution.ResolveLocation(
            "Dungeon Hub", "The Catacombs (F7)", Base, Base.AddSeconds(-2));

        resolved.Should().Be("The Catacombs (F7)");
    }

    [Test]
    public void DungeonHub_PeriodStartWellBeforeFloorReading_StaysUnchanged()
    {
        var resolved = DungeonRewardAttribution.ResolveLocation(
            "Dungeon Hub", "The Catacombs (F7)", Base, Base.AddMinutes(-5));

        resolved.Should().Be("Dungeon Hub");
    }

    [TestCase("The Catacombs (F1)", true, "F1")]
    [TestCase("The Catacombs (F7)", true, "F7")]
    [TestCase("The Catacombs (M1)", true, "M1")]
    [TestCase("The Catacombs (M7)", true, "M7")]
    [TestCase("The Catacombs (E)", false, null)]
    [TestCase("Dungeon Hub", false, null)]
    [TestCase("The Garden", false, null)]
    [TestCase(null, false, null)]
    public void IsFloorZoneAndFloorOf(string zone, bool expectedIsFloor, string expectedFloor)
    {
        DungeonRewardAttribution.IsFloorZone(zone).Should().Be(expectedIsFloor);
        DungeonRewardAttribution.FloorOf(zone).Should().Be(expectedFloor);
    }
}
