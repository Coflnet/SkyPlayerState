using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class PriceHistoryTests
{
    private static readonly DateTime Start = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, double> Fetch(string tag, double price) => new() { { tag, price } };

    [Test]
    public void FlappingSeries_GivesTheStableValue()
    {
        var history = new PriceHistory();
        history.Add(Fetch("F3", 12_000), Start);
        history.Add(Fetch("F3", 990_000), Start.AddMinutes(2));

        var result = history.Add(Fetch("F3", 12_000), Start.AddMinutes(4));

        result["F3"].Should().Be(12_000);
    }

    [Test]
    public void NewTag_IsUsedImmediately()
    {
        var history = new PriceHistory();
        history.Add(Fetch("A", 5), Start);

        history.Add(Fetch("B", 77), Start.AddMinutes(2))["B"].Should().Be(77);
    }

    [Test]
    public void MissingTag_KeepsItsValueUntilItExpires()
    {
        var history = new PriceHistory();
        history.Add(Fetch("A", 5), Start);

        history.Add(Fetch("B", 1), Start.AddHours(5))["A"].Should().Be(5);
        history.Add(Fetch("B", 1), Start.AddHours(6).AddMinutes(1)).Should().NotContainKey("A");
    }

    [Test]
    public void OnlyTheLastFetchesCount()
    {
        var history = new PriceHistory();
        for (var i = 0; i < PriceHistory.MaxSamples; i++)
            history.Add(Fetch("A", 100), Start.AddMinutes(i * 2));

        Dictionary<string, double> result = null;
        for (var i = 0; i < PriceHistory.MaxSamples; i++)
            result = history.Add(Fetch("A", 200), Start.AddHours(1).AddMinutes(i * 2));

        result["A"].Should().Be(200);
    }

    [Test]
    public void TwoSamples_GiveAnObservedValue()
    {
        var history = new PriceHistory();
        history.Add(Fetch("F3", 12_000), Start);

        history.Add(Fetch("F3", 990_000), Start.AddMinutes(2))["F3"].Should().Be(12_000);
    }

    [Test]
    public void NonPositivePrice_DropsTheTagAtOnce()
    {
        var history = new PriceHistory();
        history.Add(Fetch("A", 5), Start);

        history.Add(Fetch("A", 0), Start.AddMinutes(2)).Should().NotContainKey("A");
    }
}
