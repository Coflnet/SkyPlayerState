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
/// tell how much of the "Dungeon Hub" bucket was really reward claims.
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
            output.Add(new
            {
                ts = e["timestamp"],
                player,
                location = loc,
                resolvedLocation,
                island = SkyblockZones.IslandOf(resolvedLocation),
                profit = long.Parse(m.Groups[3].Value),
                items,
                task = c?.TaskName,
                itemMatched = c?.ItemMatched,
                hidden
            });
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output));
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
