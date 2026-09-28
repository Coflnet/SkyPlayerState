using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Discovery harness for finding unclassified production revenue (used for the 2026-09 "80% of
/// coin value went unclassified" investigation - see SkyblockZones.Canonical, the dungeon/Kuudra
/// floor-tier fixes, and the 5 new tasks added around that date). To re-run it:
/// 1. Pull "Profit summary for" lines from Loki: `kubectl --context talos-eu get --raw
///    "/api/v1/namespaces/loki/services/loki-scalable-gateway:80/proxy/loki/api/v1/query_range?query=&lt;urlencoded {service_name="sky-player-state"} |= "Profit summary for"&gt;&amp;start=&lt;ns&gt;&amp;end=&lt;ns&gt;&amp;limit=5000&amp;direction=backward"`,
///    paged in 30-minute windows (Loki caps results per request), into a JSON array of
///    {"timestamp","line"} objects.
/// 2. Save that array to a file and run: TASK_COVERAGE_INPUT=/path/to/file.json dotnet test
///    --filter FullyQualifiedName~TaskCoverageDiscoveryTests
/// 3. Aggregate the resulting coverage.json (written next to the input file) by location/items to
///    find revenue TaskClassifier still leaves unclassified.
/// Caveats: a Bazaar purchase shows up as a "collected" item in these logs too (an impossibly high
/// rate for an item is a purchase, not a drop) and each period's real duration is unknown - minutes
/// = 10 is assumed here, so rates derived from coverage.json are approximate. Logs pulled from before
/// the dungeon reward-claim attribution rollout still say "Dungeon Hub" for a reward-claim period (the
/// production fix - CollectionListener.StoreLocationProfit - did not exist yet when they were
/// written), so this harness replays the same resolution itself: entries are sorted by timestamp and
/// the last Catacombs floor zone seen per player is tracked as it walks them, exactly like
/// Models.ExtractedInfo.LastDungeonFloor/LastDungeonFloorAt in production - see
/// <see cref="DungeonRewardAttribution.ResolveLocation"/>. Both the raw and resolved location are
/// written to coverage.json (<c>location</c>/<c>resolvedLocation</c>) so the remainder analysis can
/// tell how much of the "Dungeon Hub" bucket was really reward claims. Also writes summary.json - a
/// pre-aggregated overview (period/profit totals by public/hidden/unclassified class, plus the top 25
/// unclassified zones and top 25 tasks by positive profit) so a first read of the run doesn't require
/// re-deriving those numbers from coverage.json by hand.
/// </summary>
[Explicit]
public class TaskCoverageDiscoveryTests
{
    [Test]
    public void DumpCoverage()
    {
        var input = Environment.GetEnvironmentVariable("TASK_COVERAGE_INPUT");
        if (string.IsNullOrEmpty(input) || !File.Exists(input))
        {
            Assert.Ignore("Set TASK_COVERAGE_INPUT to a JSON file of {\"timestamp\",\"line\"} entries (see class doc comment) to run this.");
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(input));
        var outputPath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "coverage.json");

        var entries = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(input));
        var registry = new TaskRegistry();
        var classifier = new TaskClassifier(registry);
        var lineRe = new Regex(@"^Profit summary for (\S+) at (.*?): (-?\d+) coins from (.*)$");
        var itemRe = new Regex(@"(-?\d+)x ([^,]+)");
        var output = new List<object>();
        // Parallel typed accumulator (same rows as `output` above) - summary.json is derived from
        // this instead of re-parsing coverage.json back out of its serialized anonymous-object form.
        var rows = new List<(string ResolvedLocation, long Profit, string Task, bool Hidden)>();
        // Per-player "last confirmed Catacombs floor" tracking, mirroring
        // Models.ExtractedInfo.LastDungeonFloor/LastDungeonFloorAt - see the class doc comment.
        var lastFloorByPlayer = new Dictionary<string, (string Zone, DateTime At)>();
        var parsed = entries
            .Select(e => (Entry: e, Timestamp: ParseTimestamp(e["timestamp"])))
            .OrderBy(x => x.Timestamp);
        foreach (var (e, ts) in parsed)
        {
            var m = lineRe.Match(e["line"]);
            if (!m.Success) continue;
            var items = new Dictionary<string, int>();
            foreach (Match im in itemRe.Matches(m.Groups[4].Value))
                items[im.Groups[2].Value] = int.Parse(im.Groups[1].Value);
            var loc = m.Groups[2].Value;
            var player = m.Groups[1].Value;

            var lastFloor = lastFloorByPlayer.GetValueOrDefault(player);
            var resolvedLocation = DungeonRewardAttribution.ResolveLocation(loc, lastFloor.Zone, lastFloor.At, ts);
            if (DungeonRewardAttribution.IsFloorZone(loc))
                lastFloorByPlayer[player] = (loc, ts);

            var c = classifier.Classify(resolvedLocation, items, 10);
            // separates hidden absorption (bazaar/auction purchases, minion collection - see
            // HiddenTasks.cs) from genuinely unclassified/real-task revenue in the remainder analysis.
            var hidden = c?.TaskName != null
                && (registry.GetByName(c.TaskName) as MethodTask)?.IsHidden == true;
            var profit = long.Parse(m.Groups[3].Value);
            output.Add(new
            {
                ts = e["timestamp"],
                player,
                location = loc,
                resolvedLocation,
                island = SkyblockZones.IslandOf(resolvedLocation),
                profit,
                items,
                task = c?.TaskName,
                itemMatched = c?.ItemMatched,
                hidden
            });
            rows.Add((resolvedLocation, profit, c?.TaskName, hidden));
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output));
        // every registered detection signature, so the remainder analysis can tell "no task exists
        // for this zone/item" apart from "a task exists but its Locations/DetectionItems miss it"
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(outputPath), "signatures.json"),
            JsonSerializer.Serialize(registry.MethodTasks.Select(t =>
            {
                var sig = t.GetDetectionSignature();
                return new
                {
                    task = sig.MethodName,
                    hidden = t.IsHidden,
                    locations = sig.Locations,
                    detectionItems = sig.DetectionItems,
                    sig.RequireShardItems,
                    sig.ExcludeShardItems,
                    sig.Priority,
                    sig.Category
                };
            })));

        // ── summary.json: pre-aggregated overview so a first read doesn't require re-deriving
        // totals from coverage.json by hand. "Class" is public (a real, user-facing task) / hidden
        // (MethodTask.Hidden accounting bucket - HiddenTasks.cs) / unclassified (no task matched).
        // "Positive profit" only sums periods with profit > 0 - a period can have negative profit
        // (e.g. a pure purchase), which would otherwise distort the class totals/shares.
        string ClassOf((string ResolvedLocation, long Profit, string Task, bool Hidden) r) =>
            r.Task == null ? "unclassified" : r.Hidden ? "hidden" : "public";

        var byClass = rows.GroupBy(ClassOf).ToDictionary(g => g.Key, g => g.ToList());
        var totalPositiveProfit = rows.Where(r => r.Profit > 0).Sum(r => r.Profit);
        var classSummaries = new[] { "public", "hidden", "unclassified" }.Select(cls =>
        {
            var classRows = byClass.GetValueOrDefault(cls, []);
            var positiveProfit = classRows.Where(r => r.Profit > 0).Sum(r => r.Profit);
            return new
            {
                @class = cls,
                periodCount = classRows.Count,
                positiveProfit,
                sharePercent = totalPositiveProfit > 0 ? Math.Round(positiveProfit * 100.0 / totalPositiveProfit, 2) : 0
            };
        }).ToList();

        var topUnclassifiedZones = byClass.GetValueOrDefault("unclassified", [])
            .GroupBy(r => r.ResolvedLocation)
            .Select(g => new { zone = g.Key, positiveProfit = g.Where(r => r.Profit > 0).Sum(r => r.Profit), periodCount = g.Count() })
            .OrderByDescending(z => z.positiveProfit)
            .Take(25)
            .ToList();

        var topTasks = rows.Where(r => r.Task != null)
            .GroupBy(r => r.Task)
            .Select(g => new { task = g.Key, positiveProfit = g.Where(r => r.Profit > 0).Sum(r => r.Profit), periodCount = g.Count() })
            .OrderByDescending(t => t.positiveProfit)
            .Take(25)
            .ToList();

        var summary = new
        {
            totalPeriods = rows.Count,
            totalPositiveProfit,
            byClass = classSummaries,
            topUnclassifiedZones,
            topTasks
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(outputPath), "summary.json"), JsonSerializer.Serialize(summary));
    }

    /// <summary>
    /// Loki timestamps come back either as ISO-8601 (whatever pulled the entries already converted)
    /// or as raw nanoseconds-since-epoch (Loki's native format) - accept either rather than assuming
    /// one, since which shape a given TASK_COVERAGE_INPUT file has depends on how it was pulled.
    /// </summary>
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
