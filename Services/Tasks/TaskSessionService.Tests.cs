using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskSessionServiceTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);
    private static readonly TaskSessionService Service = new(Classifier);

    private const string Player = "00000000000000000000000000000001";

    /// <summary>
    /// A task whose detection spans two locations, used to prove fragments in
    /// different areas accumulate into one session.
    /// </summary>
    private static (string task, string locA, string locB, Dictionary<string, int> items) MultiLocationTask()
    {
        foreach (var t in Registry.MethodTasks)
        {
            var sig = t.GetDetectionSignature();
            if (sig.Locations.Count < 2 || sig.DetectionItems.Count == 0 || sig.RequireShardItems)
                continue;
            var locs = sig.Locations.ToList();
            var items = sig.DetectionItems.Take(1).ToDictionary(k => k, _ => 3);
            // make sure this fixture unambiguously classifies to this task
            if (Classifier.Classify(locs[0], items, 10)?.TaskName != sig.MethodName)
                continue;
            if (Classifier.Classify(locs[1], items, 10)?.TaskName != sig.MethodName)
                continue;
            return (sig.MethodName, locs[0], locs[1], items);
        }
        throw new InvalidOperationException("no multi-location task fixture found in registry");
    }

    private static Period Fragment(string location, Dictionary<string, int> items, DateTime start, DateTime end) => new()
    {
        PlayerUuid = Player,
        Location = location,
        Server = "test",
        StartTime = start,
        EndTime = end,
        ItemsCollected = items == null ? new() : new(items)
    };

    [Test]
    public void MovingBetweenLocations_AccumulatesIntoOneSession()
    {
        var (task, locA, locB, items) = MultiLocationTask();
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 07, 15, 12, 00, 00, DateTimeKind.Utc);

        // two sub-window fragments in different areas, each below the 3 min gate on its own
        var f1 = Service.Accumulate(info, Player, Fragment(locA, items, t0, t0.AddMinutes(2)), null, null, t0.AddMinutes(2));
        var f2 = Service.Accumulate(info, Player, Fragment(locB, items, t0.AddMinutes(2), t0.AddMinutes(4)), null, null, t0.AddMinutes(4));

        // neither fragment flushed anything, they are still being accumulated
        f1.Should().BeNull();
        f2.Should().BeNull();
        info.CurrentSession.Should().NotBeNull();
        // once the accumulated window crossed 3 min it classifies to the task
        info.CurrentSession!.DetectedTask.Should().Be(task);
        // items from both areas are combined
        info.CurrentSession.Items[items.First().Key].Should().Be(items.First().Value * 2);

        // going idle past the flush window finalizes the single spanning session
        var flush = Service.Accumulate(info, Player, Fragment(locB, null, t0.AddMinutes(4), t0.AddMinutes(10)),
            null, null, t0.AddMinutes(10));
        flush.Should().NotBeNull();
        flush!.DetectedTask.Should().Be(task);
        (flush.EndTime - flush.StartTime).TotalMinutes.Should().BeGreaterThanOrEqualTo(3);
        info.CurrentSession.Should().BeNull();
    }

    [Test]
    public void Idle_FinalizesSessionAfterFlushWindow()
    {
        var (task, locA, _, items) = MultiLocationTask();
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 07, 15, 12, 00, 00, DateTimeKind.Utc);

        Service.Accumulate(info, Player, Fragment(locA, items, t0, t0.AddMinutes(4)), null, null, t0.AddMinutes(4));
        info.CurrentSession.Should().NotBeNull();

        // an idle tick within the window keeps the session
        var kept = Service.Accumulate(info, Player, Fragment(locA, null, t0.AddMinutes(4), t0.AddMinutes(6)),
            null, null, t0.AddMinutes(6));
        kept.Should().BeNull();
        info.CurrentSession.Should().NotBeNull();

        // an idle tick past the 5 min window finalizes it
        var flush = Service.Accumulate(info, Player, Fragment(locA, null, t0.AddMinutes(6), t0.AddMinutes(12)),
            null, null, t0.AddMinutes(12));
        flush.Should().NotBeNull();
        flush!.DetectedTask.Should().Be(task);
        info.CurrentSession.Should().BeNull();
    }

    [Test]
    public void MaxDuration_CutsSessionAndKeepsFolding()
    {
        var (_, locA, _, items) = MultiLocationTask();
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 07, 15, 12, 00, 00, DateTimeKind.Utc);

        Service.Accumulate(info, Player, Fragment(locA, items, t0, t0.AddMinutes(10)), null, null, t0.AddMinutes(10));
        // a fragment that pushes the session past the max length flushes it
        var flush = Service.Accumulate(info, Player,
            Fragment(locA, items, t0.AddMinutes(10), t0.AddMinutes(TaskSessionService.MaxSessionMinutes + 1)),
            null, null, t0.AddMinutes(TaskSessionService.MaxSessionMinutes + 1));
        flush.Should().NotBeNull();
        (flush!.EndTime - flush.StartTime).TotalMinutes
            .Should().BeGreaterThanOrEqualTo(TaskSessionService.MaxSessionMinutes);
        info.CurrentSession.Should().BeNull();
    }

    [Test]
    public void NoActivity_DoesNotStartSession()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 07, 15, 12, 00, 00, DateTimeKind.Utc);
        var flush = Service.Accumulate(info, Player, Fragment("Hub", null, t0, t0.AddMinutes(6)), null, null, t0.AddMinutes(6));
        flush.Should().BeNull();
        info.CurrentSession.Should().BeNull();
    }

    [Test]
    public void NegativeCountsSurviveTheSessionAndTheFlush()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 10, 20, 12, 00, 00, DateTimeKind.Utc);
        var items = new Dictionary<string, int> { ["DUNGEON_CHEST_COST"] = -100_000_000, ["NECRON_HANDLE"] = 1 };

        Service.Accumulate(info, Player, Fragment("The Catacombs (M7)", items, t0, t0.AddMinutes(1)), null, null, t0.AddMinutes(1));
        var flush = Service.Accumulate(info, Player, Fragment("The Catacombs (M7)", null, t0.AddMinutes(1), t0.AddMinutes(7)), null, null, t0.AddMinutes(7));

        flush.Should().NotBeNull();
        flush!.ItemsCollected.Should().Contain("DUNGEON_CHEST_COST", -100_000_000);
        flush.ItemsCollected.Should().Contain("NECRON_HANDLE", 1);
    }

    [Test]
    public void TagThatNetsToZeroDisappears()
    {
        var (_, locA, _, items) = MultiLocationTask();
        var tag = items.Keys.First();
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 10, 20, 12, 00, 00, DateTimeKind.Utc);

        Service.Accumulate(info, Player, Fragment(locA, new() { [tag] = 5, ["KISMET_FEATHER"] = 2 }, t0, t0.AddMinutes(1)), null, null, t0.AddMinutes(1));
        Service.Accumulate(info, Player, Fragment(locA, new() { ["KISMET_FEATHER"] = -2, ["NECRON_HANDLE"] = 1 }, t0.AddMinutes(1), t0.AddMinutes(2)), null, null, t0.AddMinutes(2));

        info.CurrentSession!.Items.Should().NotContainKey("KISMET_FEATHER");
        info.CurrentSession.Items.Should().ContainKey(tag);
    }

    [Test]
    public void AddedNegativeEntry_DoesNotChangeClassification()
    {
        var (task, locA, _, items) = MultiLocationTask();
        var t0 = new DateTime(2026, 10, 20, 12, 00, 00, DateTimeKind.Utc);
        var plain = new ExtractedInfo();
        var withCost = new ExtractedInfo();
        var costItems = new Dictionary<string, int>(items) { ["DUNGEON_CHEST_COST"] = -100_000_000 };

        Service.Accumulate(plain, Player, Fragment(locA, items, t0, t0.AddMinutes(10)), null, null, t0.AddMinutes(10));
        Service.Accumulate(withCost, Player, Fragment(locA, costItems, t0, t0.AddMinutes(10)), null, null, t0.AddMinutes(10));

        withCost.CurrentSession!.DetectedTask.Should().Be(plain.CurrentSession!.DetectedTask).And.Be(task);
        withCost.CurrentSession.Items.Should().Contain("DUNGEON_CHEST_COST", -100_000_000);
    }

    [Test]
    public void Cap_KeepsLargestByAbsoluteCount()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 10, 20, 12, 00, 00, DateTimeKind.Utc);
        var items = Enumerable.Range(0, TaskSessionService.MaxDistinctItems + 10).ToDictionary(i => "ITEM_" + i, i => i + 1);
        items["DUNGEON_CHEST_COST"] = -100_000_000;

        Service.Accumulate(info, Player, Fragment("Hub", items, t0, t0.AddMinutes(1)), null, null, t0.AddMinutes(1));

        info.CurrentSession!.Items.Should().HaveCount(TaskSessionService.MaxDistinctItems);
        info.CurrentSession.Items.Should().ContainKey("DUNGEON_CHEST_COST");
    }
}
