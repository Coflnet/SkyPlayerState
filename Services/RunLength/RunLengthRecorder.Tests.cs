using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class RunLengthRecorderTests
{
    private class MemoryStore : IRunLengthStore
    {
        public readonly Dictionary<string, List<string>> Lists = new();
        public bool Fail;
        public Task Push(string key, string entry, int window)
        {
            if (Fail) throw new InvalidOperationException("redis down");
            var list = Lists.GetValueOrDefault(key) ?? (Lists[key] = []);
            list.Insert(0, entry);
            if (list.Count > window) list.RemoveRange(window, list.Count - window);
            return Task.CompletedTask;
        }
        public Task<List<string>> Range(string key)
        {
            if (Fail) throw new InvalidOperationException("redis down");
            return Task.FromResult(Lists.GetValueOrDefault(key)?.ToList() ?? []);
        }
        public Task<List<string>> Keys() => Task.FromResult(Lists.Keys.Order().ToList());
    }

    private static RunLengthRecorder Create(MemoryStore store) => new(store, NullLogger<RunLengthRecorder>.Instance);

    [Test]
    public async Task RecordedRuns_GiveCountMedianAndQuartiles()
    {
        var store = new MemoryStore();
        var recorder = Create(store);
        foreach (var minutes in new[] { 4, 6, 5, 7, 8 })
            await recorder.Record("dungeon:M7", TimeSpan.FromMinutes(minutes), RunLengthBounds.Dungeon);

        var stats = (await recorder.GetStats("dungeon:M7"))!;

        stats.Count.Should().Be(5);
        stats.MedianSeconds.Should().Be(360);
        stats.P25Seconds.Should().Be(300);
        stats.P75Seconds.Should().Be(420);
        stats.WindowSize.Should().Be(RunLengthRecorder.Window);
        stats.WindowFrom.Should().BeOnOrBefore(stats.WindowTo);
        (await recorder.GetMedian("dungeon:M7")).Should().Be(TimeSpan.FromMinutes(6));
    }

    [TestCase(new double[] { 10 }, 10.0, 10.0, 10.0)]
    [TestCase(new double[] { 10, 20 }, 15.0, 12.5, 17.5)]
    [TestCase(new double[] { 1, 2, 3, 4 }, 2.5, 1.75, 3.25)]
    public void Percentiles_Interpolate(double[] values, double median, double p25, double p75)
    {
        var stats = RunLengthStats.From("k", values.Select(v => (v, DateTime.UtcNow)).ToList(), 200)!;

        stats.MedianSeconds.Should().Be(median);
        stats.P25Seconds.Should().Be(p25);
        stats.P75Seconds.Should().Be(p75);
    }

    [Test]
    public async Task MedianIsNullUntilEnoughRuns_AndForUnknownKeys()
    {
        var recorder = Create(new MemoryStore());
        for (var i = 0; i < RunLengthRecorder.MinSamples - 1; i++)
            await recorder.Record("dungeon:F7", TimeSpan.FromMinutes(5), RunLengthBounds.Dungeon);

        (await recorder.GetMedian("dungeon:F7")).Should().BeNull();
        (await recorder.GetMedian("dungeon:M1")).Should().BeNull();
    }

    [TestCase(0)]
    [TestCase(5)]
    [TestCase(59)]
    [TestCase(45 * 60 + 1)]
    [TestCase(8 * 3600)]
    public async Task ImplausibleDurations_AreDiscarded(int seconds)
    {
        var store = new MemoryStore();

        await Create(store).Record("dungeon:M7", TimeSpan.FromSeconds(seconds), RunLengthBounds.Dungeon);

        store.Lists.Should().BeEmpty();
    }

    [Test]
    public async Task BoundsAreInclusive_AndDefaultBoundsApplyWithoutExplicitOnes()
    {
        var store = new MemoryStore();
        var recorder = Create(store);
        await recorder.Record("a", TimeSpan.FromSeconds(60), RunLengthBounds.Dungeon);
        await recorder.Record("a", TimeSpan.FromMinutes(45), RunLengthBounds.Dungeon);
        await recorder.Record("b", TimeSpan.FromSeconds(10));
        await recorder.Record("b", TimeSpan.FromMinutes(10));

        store.Lists["a"].Should().HaveCount(2);
        store.Lists["b"].Should().HaveCount(1);
    }

    [Test]
    public async Task GetEntries_ReturnsRawEntriesNewestFirst_AndNullForInvalidKey()
    {
        var store = new MemoryStore();
        var recorder = Create(store);
        await recorder.Record("kuudra:T5", TimeSpan.FromSeconds(80), RunLengthBounds.Kuudra);
        await recorder.Record("kuudra:T5", TimeSpan.FromSeconds(95), RunLengthBounds.Kuudra);

        var entries = (await recorder.GetEntries("kuudra:T5"))!;

        entries.Select(e => e.Seconds).Should().Equal(95, 80);
        entries[0].At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        (await recorder.GetEntries("kuudra:T9"))!.Should().BeEmpty();
        (await recorder.GetEntries("has space")).Should().BeNull();
    }

    [Test]
    public async Task KuudraBounds_KeepRealDirectRequeueCyclesOf76To104Seconds()
    {
        var store = new MemoryStore();
        foreach (var seconds in new[] { 76, 83, 90, 104 })
            await Create(store).Record("kuudra:T5", TimeSpan.FromSeconds(seconds), RunLengthBounds.Kuudra);
        await Create(store).Record("kuudra:T5", TimeSpan.FromSeconds(30), RunLengthBounds.Kuudra);

        store.Lists["kuudra:T5"].Should().HaveCount(4, "30 s is a lobby hop, the measured cycles are real runs");
    }

    [TestCase("")]
    [TestCase("has space")]
    [TestCase("a;b")]
    [TestCase("x\ny")]
    public async Task InvalidKeys_AreNotRecorded(string key)
    {
        var store = new MemoryStore();

        await Create(store).Record(key, TimeSpan.FromMinutes(5));

        store.Lists.Should().BeEmpty();
    }

    [Test]
    public async Task WindowIsCapped()
    {
        var store = new MemoryStore();
        var recorder = Create(store);
        for (var i = 0; i < RunLengthRecorder.Window + 20; i++)
            await recorder.Record("dungeon:F1", TimeSpan.FromMinutes(5), RunLengthBounds.Dungeon);

        (await recorder.GetStats("dungeon:F1"))!.Count.Should().Be(RunLengthRecorder.Window);
    }

    [Test]
    public async Task SharedStoreFailure_DoesNotPropagate_FromRecordOrMedian()
    {
        var recorder = Create(new MemoryStore { Fail = true });

        await recorder.Invoking(r => r.Record("dungeon:M7", TimeSpan.FromMinutes(5), RunLengthBounds.Dungeon)).Should().NotThrowAsync();
        (await recorder.GetMedian("dungeon:M7")).Should().BeNull();
    }

    [Test]
    public async Task GetAll_ListsEveryKey()
    {
        var recorder = Create(new MemoryStore());
        await recorder.Record("dungeon:M7", TimeSpan.FromMinutes(5), RunLengthBounds.Dungeon);
        await recorder.Record("dungeon:F7", TimeSpan.FromMinutes(6), RunLengthBounds.Dungeon);

        (await recorder.GetAll()).Select(s => s.Key).Should().Equal("dungeon:F7", "dungeon:M7");
    }

    [TestCase("The Catacombs (M7)", "dungeon:M7")]
    [TestCase("The Catacombs (F1)", "dungeon:F1")]
    [TestCase("The Catacombs (E)", null)]
    [TestCase("Dungeon Hub", null)]
    [TestCase(null, null)]
    public void DungeonKeys_AreFloorBased(string? zone, string? expected)
    {
        RunLengthKeys.ForDungeonZone(zone).Should().Be(expected);
    }

    // ── recording hook in the scoreboard handling ──

    private static string Area(string zone) => $" {ScoreboardParser.AreaGlyphPrivateUse} {zone}";

    private static async Task Tick(StateObject state, IRunLengthRecorder recorder, string zone)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Scoreboard, Scoreboard = ["[SKYBLOCK]", Area(zone), "Purse: 5"] }
        };
        args.AddService(recorder);
        await new CollectionListener().Process(args);
    }

    [Test]
    public async Task EnteringAndLeavingAFloor_RecordsOneRunOfThatLength()
    {
        var recorded = new List<(string Key, TimeSpan Duration, RunLengthBounds? Bounds)>();
        var recorder = new Mock<IRunLengthRecorder>();
        recorder.Setup(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds?>()))
            .Callback<string, TimeSpan, RunLengthBounds?>((k, d, b) => recorded.Add((k, d, b))).Returns(Task.CompletedTask);
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "Dungeon Hub";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow;

        await Tick(state, recorder.Object, "The Catacombs (M7)");
        recorded.Should().BeEmpty("entering is not the end of a run");
        // pretend the run has been going for 9 minutes
        state.ExtractedInfo.CurrentLocationSince = DateTime.UtcNow.AddMinutes(-9);
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow;
        state.ExtractedInfo.LastDungeonRunCompletedAt = DateTime.UtcNow.AddMinutes(-1); // the results header was seen
        await Tick(state, recorder.Object, "Dungeon Hub");

        var run = recorded.Should().ContainSingle().Subject;
        run.Key.Should().Be("dungeon:M7");
        run.Duration.TotalMinutes.Should().BeApproximately(9, 0.1);
        run.Bounds.Should().Be(RunLengthBounds.Dungeon);
    }

    [Test]
    public async Task TheFiveMinuteFlushInsideARun_DoesNotSplitIt()
    {
        var recorder = new Mock<IRunLengthRecorder>();
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Catacombs (M7)";
        var since = DateTime.UtcNow.AddMinutes(-12);
        state.ExtractedInfo.CurrentLocationSince = since;
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow.AddSeconds(-5);
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddMinutes(-6); // due for the periodic flush

        await Tick(state, recorder.Object, "The Catacombs (M7)");

        state.ExtractedInfo.LastLocationChange.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1), "the period was flushed");
        state.ExtractedInfo.CurrentLocationSince.Should().Be(since, "the run start is untouched by the flush");
        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds?>()), Times.Never);
    }

    [Test]
    public async Task LeavingANonDungeonZone_RecordsNothing()
    {
        var recorder = new Mock<IRunLengthRecorder>();
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Garden";
        state.ExtractedInfo.CurrentLocationSince = DateTime.UtcNow.AddMinutes(-9);

        await Tick(state, recorder.Object, "Dungeon Hub");

        recorder.Verify(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds?>()), Times.Never);
    }

    [Test]
    public async Task RecorderThrowing_DoesNotBreakTheScoreboardHandling()
    {
        var recorder = new Mock<IRunLengthRecorder>();
        recorder.Setup(r => r.Record(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<RunLengthBounds?>())).ThrowsAsync(new Exception("boom"));
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Catacombs (F7)";
        state.ExtractedInfo.CurrentLocationSince = DateTime.UtcNow.AddMinutes(-9);
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow;

        await Tick(state, recorder.Object, "Dungeon Hub");

        state.ExtractedInfo.CurrentLocation.Should().Be("Dungeon Hub");
    }
}
