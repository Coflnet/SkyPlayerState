using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Debugging harness: replays logged "Profit summary for ..." lines (same input format as
/// <see cref="TaskCoverageDiscoveryTests"/>) through the real <see cref="TaskSessionService"/> and
/// <see cref="TaskClassifier"/> to show which fragments each task's sessions absorb.
/// Run: TASK_SESSION_REPLAY_INPUT=/path/lines.json [TASK_COVERAGE_PRICES=/path/prices.json]
///   dotnet test --filter FullyQualifiedName~TaskSessionReplayTests.DumpSessions
/// Writes sessions.json and session_summary.json next to the input.
/// Fragment timing: EndTime = line timestamp, StartTime = previous fragment's EndTime of that player when
/// within 5 minutes, else EndTime - 60s. Idle ticks are fed like in production (before a fragment after a
/// >5 min gap, and 10 min after the last fragment).
/// </summary>
public class TaskSessionReplayTests
{
    [Test, Explicit]
    public void DumpSessions()
    {
        var input = Environment.GetEnvironmentVariable("TASK_SESSION_REPLAY_INPUT");
        if (string.IsNullOrEmpty(input) || !File.Exists(input))
        {
            Assert.Ignore("Set TASK_SESSION_REPLAY_INPUT to a JSON file of {\"timestamp\",\"line\"} entries (see TaskCoverageDiscoveryTests).");
            return;
        }
        Dictionary<string, double> prices = null;
        var pricesPath = Environment.GetEnvironmentVariable("TASK_COVERAGE_PRICES");
        if (!string.IsNullOrEmpty(pricesPath))
            prices = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(pricesPath));

        var entries = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(input));
        var lines = entries.Select(e => (Timestamp: ParseTimestamp(e["timestamp"]), Line: e["line"])).ToList();
        var sessions = TaskSessionReplay.Replay(lines, prices);
        var dir = Path.GetDirectoryName(Path.GetFullPath(input));
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, "sessions.json"), JsonSerializer.Serialize(sessions, opts));
        File.WriteAllText(Path.Combine(dir, "session_summary.json"), JsonSerializer.Serialize(TaskSessionReplay.Summarize(sessions), opts));
    }

    [Test]
    public void Replay_SynthesizesSessionsAndAbsorption()
    {
        var t0 = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        string Ts(int min) => new DateTimeOffset(t0.AddMinutes(min)).ToUnixTimeMilliseconds().ToString() + "000000";
        var lines = new List<(DateTime, string)>
        {
            (t0.AddMinutes(1), "Profit summary for Bob at Ruins: 100 coins from 3x GRIFFIN_FEATHER"),
            (t0.AddMinutes(4), "Profit summary for Bob at Ruins: 100 coins from 3x GRIFFIN_FEATHER"),
            (t0.AddMinutes(5), "Profit summary for Bob at Auction House: 0 coins from 1x AUCTION_PURCHASE, 1x HYPERION"),
            (t0.AddMinutes(6), "garbage line is ignored"),
        };
        var prices = new Dictionary<string, double> { ["GRIFFIN_FEATHER"] = 100, ["HYPERION"] = 1_000_000 };

        var sessions = TaskSessionReplay.Replay(lines, prices);

        sessions.Should().NotBeEmpty();
        sessions.Sum(s => s.Fragments.Count).Should().Be(3);
        var diana = sessions.Single(s => s.Fragments.Any(f => f.Zone == "Ruins"));
        diana.Player.Should().Be("Bob");
        diana.Fragments.Count(f => f.Zone == "Ruins").Should().Be(2);
        diana.Items["GRIFFIN_FEATHER"].Should().Be(6);

        var summary = TaskSessionReplay.Summarize(sessions);
        summary.Sum(s => s.Sessions).Should().Be(sessions.Count);
        diana.Task.Should().Be("Diana");
        diana.Value.Should().BeLessThan(1_000_000, "the auction house purchase must not be merged into the Diana session");
        diana.Fragments.Should().NotContain(f => f.Zone == "Auction House");
        sessions.Should().Contain(x => x.Fragments.Any(f => f.Zone == "Auction House"), "the purchase starts its own session");
        var absorbed = summary.Single(x => x.Task == "Diana").Absorbed;
        absorbed.Should().NotContain(x => x.Zone == "Auction House");
    }

    private static DateTime ParseTimestamp(string ts)
    {
        if (DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        if (long.TryParse(ts, out var nanos))
            return DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000).UtcDateTime;
        return DateTime.MinValue;
    }
}

public class ReplayFragment
{
    public string Zone { get; set; }
    public DateTime End { get; set; }
    /// <summary>Task the fragment classifies to on its own (null = unclassified).</summary>
    public string Task { get; set; }
    public double Value { get; set; }
    public Dictionary<string, int> Items { get; set; }
}

public class ReplaySession
{
    public string Player { get; set; }
    public string Task { get; set; }
    public string Location { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double Minutes { get; set; }
    public double FoldedMinutes { get; set; }
    public bool TooShort { get; set; }
    public Dictionary<string, int> Items { get; set; }
    public double Value { get; set; }
    public List<ReplayFragment> Fragments { get; set; } = [];
}

public class AbsorbedEntry
{
    public string FragmentTask { get; set; }
    public string Zone { get; set; }
    public int Count { get; set; }
    public double Value { get; set; }
}

public class TaskSessionSummary
{
    public string Task { get; set; }
    public int Sessions { get; set; }
    public double FoldedHours { get; set; }
    public double Value { get; set; }
    public double CoinsPerHour { get; set; }
    public List<AbsorbedEntry> Absorbed { get; set; }
}

public static class TaskSessionReplay
{
    private static readonly Regex LineRe = new(@"^Profit summary for (\S+) at (.*?): (-?\d+) coins from (.*)$");
    private static readonly Regex ItemRe = new(@"(-?\d+)x ([^,]+)");
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);

    public static List<ReplaySession> Replay(IEnumerable<(DateTime Timestamp, string Line)> lines, Dictionary<string, double> prices)
    {
        var registry = new TaskRegistry();
        var classifier = new TaskClassifier(registry);
        var service = new TaskSessionService(classifier);
        var result = new List<ReplaySession>();

        var byPlayer = lines
            .Select(l => (l.Timestamp, M: LineRe.Match(l.Line)))
            .Where(x => x.M.Success)
            .GroupBy(x => x.M.Groups[1].Value);
        foreach (var group in byPlayer)
        {
            var player = group.Key;
            var info = new ExtractedInfo();
            var merged = new List<ReplayFragment>();
            DateTime? prevEnd = null;

            void Emit(Period flushed, List<ReplayFragment> fragments)
            {
                result.Add(BuildRecord(registry, player, flushed, fragments, prices));
            }

            foreach (var (end, m) in group.OrderBy(x => x.Timestamp))
            {
                if (prevEnd != null && end - prevEnd.Value > Gap)
                {
                    var tick = service.Accumulate(info, player, EmptyPeriod(player, end), null, prices, end);
                    if (tick != null)
                    {
                        Emit(tick, merged);
                        merged = [];
                    }
                }
                var start = prevEnd != null && end - prevEnd.Value <= Gap ? prevEnd.Value : end.AddSeconds(-60);
                prevEnd = end;

                var zone = m.Groups[2].Value;
                var items = new Dictionary<string, int>();
                foreach (Match im in ItemRe.Matches(m.Groups[4].Value))
                    items[im.Groups[2].Value] = int.Parse(im.Groups[1].Value);
                var period = new Period
                {
                    PlayerUuid = player,
                    Location = zone,
                    StartTime = start,
                    EndTime = end,
                    ItemsCollected = new(items)
                };
                var own = classifier.Classify(zone, items.Where(e => e.Value > 0).ToDictionary(e => e.Key, e => e.Value),
                    (end - start).TotalMinutes, prices: prices)?.TaskName;
                var fragment = new ReplayFragment { Zone = zone, End = end, Task = own, Items = items, Value = Value(items, prices) };

                var before = info.CurrentSession;
                var flushed = service.Accumulate(info, player, period, null, prices, end);
                var hasItems = items.Values.Any(v => v > 0);
                if (flushed == null)
                {
                    if (hasItems && before != null && ReferenceEquals(before, info.CurrentSession))
                        merged.Add(fragment);
                    else if (hasItems)
                        merged = [fragment]; // started a fresh session
                }
                else if (info.CurrentSession == null)
                {
                    // flushed with this fragment merged in (max session length) or an idle flush of the old one
                    if (hasItems) merged.Add(fragment);
                    Emit(flushed, merged);
                    merged = [];
                }
                else
                {
                    // classification shifted: old session flushed, fragment started a new one
                    Emit(flushed, merged);
                    merged = [fragment];
                }
            }

            if (prevEnd != null)
            {
                var finalTick = prevEnd.Value.AddMinutes(10);
                var flushed = service.Accumulate(info, player, EmptyPeriod(player, finalTick), null, prices, finalTick);
                if (flushed != null)
                    Emit(flushed, merged);
            }
        }
        return result.OrderBy(s => s.Start).ToList();

        static Period EmptyPeriod(string player, DateTime at) => new()
        {
            PlayerUuid = player,
            StartTime = at,
            EndTime = at,
            ItemsCollected = new()
        };
    }

    private static ReplaySession BuildRecord(TaskRegistry registry, string player, Period flushed,
        List<ReplayFragment> fragments, Dictionary<string, double> prices)
    {
        var methodTask = flushed.DetectedTask == null ? null : registry.GetByName(flushed.DetectedTask) as MethodTask;
        var actual = flushed.EndTime - flushed.StartTime;
        var folded = methodTask?.EffectiveDuration(flushed) ?? actual;
        var kept = TaskPeriodFolder.FilterOwnedItems(flushed.ItemsCollected, methodTask);
        return new ReplaySession
        {
            Player = player,
            Task = flushed.DetectedTask,
            Location = flushed.Location,
            Start = flushed.StartTime,
            End = flushed.EndTime,
            Minutes = actual.TotalMinutes,
            FoldedMinutes = folded.TotalMinutes,
            TooShort = folded.TotalMinutes < 2,
            Items = kept,
            Value = Value(kept, prices),
            Fragments = fragments
        };
    }

    private static double Value(Dictionary<string, int> items, Dictionary<string, double> prices)
        => prices == null ? 0 : items.Sum(e => prices.GetValueOrDefault(e.Key) * e.Value);

    public static List<TaskSessionSummary> Summarize(List<ReplaySession> sessions)
        => sessions.GroupBy(s => s.Task ?? "(unclassified)").Select(g =>
        {
            var counted = g.Where(s => !s.TooShort).ToList();
            var hours = counted.Sum(s => s.FoldedMinutes) / 60;
            var value = counted.Sum(s => s.Value);
            return new TaskSessionSummary
            {
                Task = g.Key,
                Sessions = g.Count(),
                FoldedHours = hours,
                Value = value,
                CoinsPerHour = hours > 0 ? value / hours : 0,
                Absorbed = g.SelectMany(s => s.Fragments)
                    .Where(f => f.Task != g.First().Task)
                    .GroupBy(f => (f.Task, f.Zone))
                    .Select(a => new AbsorbedEntry { FragmentTask = a.Key.Task, Zone = a.Key.Zone, Count = a.Count(), Value = a.Sum(f => f.Value) })
                    .OrderByDescending(a => a.Value).ToList()
            };
        }).OrderByDescending(s => s.Value).ToList();
}
