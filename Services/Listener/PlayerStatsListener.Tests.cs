using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class PlayerStatsListenerTests
{
    // production sample, slot 15 and a wisdom item
    private const string MiningDescription = "Stats that influence what you can\nbreak, how quickly you can break it,\nand how many drops you receive\nwhen mining.\n\n §2 Breaking Power §f0\n §6 Mining Speed §f885\n §e Mining Spread §f115\n §e Gemstone Spread §f2.5\n §5 Pristine §f0\n §6 Mining Fortune §f259\n §6 Ore Fortune §f106.35\n §6 Block Fortune §f110.5\n §6 Dwarven Metal Fortune §f0\n §6 Gemstone Fortune §f20\n\nClick for details!";
    private const string WisdomDescription = "Stats that influence wisdom.\n\n §3☯ Combat Wisdom §f90\n §3☯ Mining Wisdom §f1,234.5\n\nClick for details!";

    private static List<Item> Menu()
    {
        var items = Enumerable.Range(0, 54).Select(_ => new Item { Tag = null, ItemName = "" }).ToList();
        items[15] = new Item { ItemName = "§aMining Stats", Description = MiningDescription };
        items[34] = new Item { ItemName = "§aWisdom Stats", Description = WisdomDescription };
        items[40] = new Item { ItemName = "§aClose", Description = "Close 5" };
        return items;
    }

    [Test]
    public void ParsesTheProductionSample()
    {
        var stats = PlayerStatsListener.ParseStats(Menu());

        stats["Mining Speed"].Should().Be(885);
        stats["Mining Spread"].Should().Be(115);
        stats["Gemstone Spread"].Should().Be(2.5);
        stats["Ore Fortune"].Should().Be(106.35);
        stats["Block Fortune"].Should().Be(110.5);
        stats["Dwarven Metal Fortune"].Should().Be(0);
        stats["Breaking Power"].Should().Be(0);
        stats["Combat Wisdom"].Should().Be(90);
        stats["Mining Wisdom"].Should().Be(1234.5);
        stats.Should().NotContainKey("Close");
        stats.Keys.Should().NotContain(k => k.Contains("Click") || k.Contains("mining"));
    }

    [Test]
    public async Task ListenerStoresStats_OnlyForTheStatsMenu()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { Chest = new ChestView { Name = "§aStats & Equipment", Items = Menu() } }
        };
        await new PlayerStatsListener().Process(args);
        args.currentState.ExtractedInfo.PlayerStats["Mining Speed"].Should().Be(885);

        var other = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { Chest = new ChestView { Name = "Something", Items = Menu() } }
        };
        await new PlayerStatsListener().Process(other);
        other.currentState.ExtractedInfo.PlayerStats.Should().BeEmpty();
        new PlayerStatsListener().Optional.Should().BeTrue();
    }

    [Test]
    public void KeepsTheHigherValue_AndReplacesStaleOnes()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        PlayerStatsListener.Merge(info, new() { ["Mining Speed"] = 885, ["Mining Spread"] = 115 }, t0);

        // lower value (no drill in hand) within 14 days does not lower it
        PlayerStatsListener.Merge(info, new() { ["Mining Speed"] = 100, ["Mining Spread"] = 200 }, t0.AddDays(5));
        info.PlayerStats["Mining Speed"].Should().Be(885);
        info.PlayerStats["Mining Spread"].Should().Be(200);

        // Mining Speed is 15 days old now, Mining Spread was raised 10 days ago
        PlayerStatsListener.Merge(info, new() { ["Mining Speed"] = 100, ["Mining Spread"] = 50 }, t0.AddDays(15));
        info.PlayerStats["Mining Speed"].Should().Be(100);
        info.PlayerStats["Mining Spread"].Should().Be(200);
        info.PlayerStatsAt.Should().Be(t0.AddDays(15));
    }

    [Test]
    public void CopyConstructorCopiesStats()
    {
        var info = new ExtractedInfo();
        PlayerStatsListener.Merge(info, new() { ["Mining Speed"] = 5 }, DateTime.UtcNow);
        var copy = new ExtractedInfo(info);
        copy.PlayerStats["Mining Speed"].Should().Be(5);
        copy.PlayerStatSetAt.Should().ContainKey("Mining Speed");
        copy.PlayerStatsAt.Should().Be(info.PlayerStatsAt);
        copy.PlayerStats.Should().NotBeSameAs(info.PlayerStats);
    }

    private static StateObject Player(double speed, double spread)
    {
        var state = new StateObject();
        PlayerStatsListener.Merge(state.ExtractedInfo, new() { ["Mining Speed"] = speed, ["Mining Spread"] = spread }, DateTime.UtcNow);
        state.ExtractedInfo.HeartOfTheMountain = new() { Tier = 7 };
        return state;
    }

    [Test]
    public async Task StatPrefix_ResolvesAndIgnoresMissingOrZero()
    {
        var service = new StatScoreService(null, NullLogger<StatScoreService>.Instance);
        var factors = new List<StatFactor> { new("stat:Mining Speed", 1, 1000) };

        (await service.GetScore(factors, Player(500, 0))).score.Should().BeApproximately(0.5, 1e-9);
        (await service.GetScore(factors, Player(500, 0))).known.Should().BeTrue();
        (await service.GetScore(factors, Player(0, 0))).known.Should().BeFalse();
        (await service.GetScore(factors, new StateObject())).known.Should().BeFalse();
    }

    [Test]
    public async Task ObsidianMining_SeparatesFastDrillsFromSlowOnes()
    {
        var service = new StatScoreService(null, NullLogger<StatScoreService>.Instance);
        var factors = new ObsidianMiningTask().StatFactors;

        var slow = await service.GetBucket(factors, Player(885, 115));
        var fast = await service.GetBucket(factors, Player(3500, 380));

        fast.Should().BeGreaterThan(slow);
        fast.Should().Be(2);
        slow.Should().BeLessThan(2);
    }
}
