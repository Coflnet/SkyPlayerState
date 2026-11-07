using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class GardenTaskTests
{
    /// <summary>
    /// Regression: GardenTask listed "Plot 1".."Plot 25", but SkyblockZones.Canonical collapses every plot
    /// ("Plot - 8", "Plot - Left Farm 2", ...) to the single "Plot" zone, so a period on a plot never matched.
    /// </summary>
    [TestCase("Plot - 8")]
    [TestCase("Plot - Left Farm 2")]
    [TestCase("Plot - CARROTS")]
    [TestCase("The Garden")]
    [TestCase("The Garden  x3")]
    public void CoversLocation_MatchesGardenZonesThatOccur(string zone)
    {
        new GardenTask().CoversLocation(zone).Should().BeTrue();
    }

    [TestCase("Hub")]
    [TestCase("Plot 1")]
    [TestCase("Smoldering Tomb")]
    public void CoversLocation_IgnoresOtherZones(string zone)
    {
        new GardenTask().CoversLocation(zone).Should().BeFalse();
    }
}
