using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class StatScoreTests
{
    private static StatScoreService MakeService()
    {
        // These tests avoid skill: factors; a null skill service resolves skills as
        // missing, which the service handles gracefully.
        return new StatScoreService(null, NullLogger<StatScoreService>.Instance);
    }

    [Test]
    public async Task GearLadder_ResolvesHighestIndex()
    {
        var state = new StateObject();
        state.Inventory = new List<Item>
        {
            new() { Tag = "FISHING_ROD" },
            new() { Tag = "ROD_OF_THE_SEA" }, // index 7 in the ladder
        };
        var factors = new List<StatFactor> { new("gear:FISHING_ROD", 1.0, 9) };
        var (score, known) = await MakeService().GetScore(factors, state);
        known.Should().BeTrue();
        // ladder index 8 (1-based) / max 9 ~ 0.888
        score.Should().BeApproximately(8.0 / 9, 1e-6);
    }

    [Test]
    public async Task MissingSignal_MarkedUnknown_WhenMostWeightUnresolved()
    {
        var state = new StateObject(); // no attributes, no gear
        var factors = new List<StatFactor>
        {
            new("attr:Mining Speed", 0.6, 10),
            new("gear:GAUNTLET", 0.4, 6),
        };
        var bucket = await MakeService().GetBucket(factors, state);
        bucket.Should().Be(StatScoreService.UnknownBucket);
    }

    [Test]
    public async Task HotmTier_DrivesBucket()
    {
        var state = new StateObject();
        state.ExtractedInfo.HeartOfTheMountain = new HeartOfThe { Tier = 10 };
        state.ExtractedInfo.AttributeLevel = new Dictionary<string, int> { { "Mining Speed", 10 } };
        var factors = new List<StatFactor>
        {
            new("hotm:tier", 0.6, 10),
            new("attr:Mining Speed", 0.4, 10),
        };
        var (score, known) = await MakeService().GetScore(factors, state);
        known.Should().BeTrue();
        score.Should().BeApproximately(1.0, 1e-6);
        StatScoreService.ScoreToBucket(score).Should().Be(2);
    }

    [Test]
    public async Task LowStats_LandLowBucket()
    {
        var state = new StateObject();
        state.ExtractedInfo.HeartOfTheMountain = new HeartOfThe { Tier = 1 };
        state.ExtractedInfo.AttributeLevel = new Dictionary<string, int> { { "Mining Speed", 1 } };
        var factors = new List<StatFactor>
        {
            new("hotm:tier", 0.6, 10),
            new("attr:Mining Speed", 0.4, 10),
        };
        var bucket = await MakeService().GetBucket(factors, state);
        bucket.Should().Be(0);
    }
}
