using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using MessagePack;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Per zone time and value accounting of a task session, the input of the "best spot" aggregate.</summary>
public class TaskSessionServiceZoneTests
{
    private static readonly TaskSessionService Service = new(new TaskClassifier(new TaskRegistry()));
    private const string Player = "00000000000000000000000000000001";
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, double> Prices = new() { ["X"] = 100 };

    private static Period Fragment(string location, Dictionary<string, int> items, int fromMinute, int toMinute) => new()
    {
        PlayerUuid = Player, Location = location, Server = "test",
        StartTime = T0.AddMinutes(fromMinute), EndTime = T0.AddMinutes(toMinute),
        ItemsCollected = items
    };

    private static Period Run(ExtractedInfo info, params Period[] fragments)
    {
        foreach (var f in fragments)
            Service.Accumulate(info, Player, f, null, Prices, f.EndTime);
        // an idle tick past the flush window finalizes the session
        var last = fragments[^1].EndTime;
        return Service.Accumulate(info, Player, Fragment(fragments[^1].Location, null, 0, 0), null, Prices, last.AddMinutes(10));
    }

    [Test]
    public void TwoZones_SplitDurationAndValue()
    {
        var info = new ExtractedInfo();
        var flush = Run(info,
            Fragment("Ruins", new() { ["X"] = 10 }, 0, 10),
            // a cost (negative count, 1 coin per unit) lowers the zone value
            Fragment("Forest", new() { ["X"] = 4, [PseudoItems.DUNGEON_CHEST_COST] = -200 }, 10, 15));

        flush.Should().NotBeNull();
        flush.ZoneSeconds.Should().BeEquivalentTo(new Dictionary<string, double> { ["Ruins"] = 600, ["Forest"] = 300 });
        flush.ZoneValue.Should().BeEquivalentTo(new Dictionary<string, double> { ["Ruins"] = 1000, ["Forest"] = 200 });
    }

    [Test]
    public void GardenZoneVariants_AreNormalizedToOneZone()
    {
        var info = new ExtractedInfo();
        var flush = Run(info,
            Fragment("The Garden  x3", new() { ["X"] = 1 }, 0, 5),
            Fragment("The Garden  x1", new() { ["X"] = 1 }, 5, 10),
            Fragment("Plot - 7", new() { ["X"] = 1 }, 10, 15));

        flush.ZoneSeconds.Should().BeEquivalentTo(new Dictionary<string, double> { ["The Garden"] = 900 });
        flush.ZoneValue["The Garden"].Should().Be(300);
    }

    [Test]
    public void IdleTick_DoesNotAddZoneTime()
    {
        var info = new ExtractedInfo();
        var flush = Run(info, Fragment("Ruins", new() { ["X"] = 1 }, 0, 4));

        flush.ZoneSeconds.Should().BeEquivalentTo(new Dictionary<string, double> { ["Ruins"] = 240 });
    }

    [Test]
    public void ZoneAccountingSurvivesTheStateRoundTripAndCopy()
    {
        var session = new TaskSession
        {
            ZoneSeconds = new() { ["Ruins"] = 600 },
            ZoneValue = new() { ["Ruins"] = 1000 }
        };

        var copy = MessagePackSerializer.Deserialize<TaskSession>(MessagePackSerializer.Serialize(session));
        var cloned = new TaskSession(session);
        session.ZoneSeconds["Ruins"] = 1;

        copy.ZoneSeconds["Ruins"].Should().Be(600);
        copy.ZoneValue["Ruins"].Should().Be(1000);
        cloned.ZoneSeconds["Ruins"].Should().Be(600);
    }

    [Test]
    public void TaskSessionMessagePackKeysAreUnique()
    {
        var duplicates = typeof(TaskSession).GetProperties()
            .Select(p => (p.Name, Key: p.GetCustomAttribute<KeyAttribute>()?.IntKey))
            .Where(p => p.Key.HasValue)
            .GroupBy(p => p.Key.Value)
            .Where(g => g.Count() > 1);

        duplicates.Should().BeEmpty();
    }
}
