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
/// = 10 is assumed here, so rates derived from coverage.json are approximate.
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
        var classifier = new TaskClassifier(new TaskRegistry());
        var lineRe = new Regex(@"^Profit summary for (\S+) at (.*?): (-?\d+) coins from (.*)$");
        var itemRe = new Regex(@"(-?\d+)x ([^,]+)");
        var output = new List<object>();
        foreach (var e in entries)
        {
            var m = lineRe.Match(e["line"]);
            if (!m.Success) continue;
            var items = new Dictionary<string, int>();
            foreach (Match im in itemRe.Matches(m.Groups[4].Value))
                items[im.Groups[2].Value] = int.Parse(im.Groups[1].Value);
            var loc = m.Groups[2].Value;
            var c = classifier.Classify(loc, items, 10);
            output.Add(new
            {
                ts = e["timestamp"],
                player = m.Groups[1].Value,
                location = loc,
                island = SkyblockZones.IslandOf(loc),
                profit = long.Parse(m.Groups[3].Value),
                items,
                task = c?.TaskName,
                itemMatched = c?.ItemMatched
            });
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output));
    }
}
