using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Volume test for a period: "mostly mining" is told from "killed a few mobs next to some stray blocks" by how many
/// blocks (or their compacted form) the period yields per hour, not by whether a mob drop is present at all. Rates, not
/// counts: periods are merged into sessions of up to 30 minutes, and a long fight must not cross a threshold only
/// because it lasted longer.
/// </summary>
/// <param name="MinCountsPerHour">tag to the hourly rate at which that item alone makes the period a bulk period</param>
/// <param name="Unless">items that always keep a period out of the bulk class (a clear sign of the competing activity)</param>
public record BulkItemRule(IReadOnlyDictionary<string, int> MinCountsPerHour, IReadOnlySet<string> Unless = null)
{
    /// <summary>Shorter periods are rated as if they lasted this long, so one minute of activity is not extrapolated to a whole hour.</summary>
    public const double MinRatedMinutes = 10;

    public bool IsBulk(Dictionary<string, int> items, double minutes)
    {
        if (items == null || (Unless != null && items.Any(kv => kv.Value > 0 && Unless.Contains(kv.Key))))
            return false;
        var hours = Math.Max(minutes, MinRatedMinutes) / 60;
        return MinCountsPerHour.Any(m => items.GetValueOrDefault(m.Key) / hours >= m.Value);
    }

    /// <summary>One rule holding the thresholds of both, vetoed by the union of what either vetoes.</summary>
    public static BulkItemRule Combine(BulkItemRule first, BulkItemRule second) => new(
        first.MinCountsPerHour.Concat(second.MinCountsPerHour).ToDictionary(kv => kv.Key, kv => kv.Value),
        (first.Unless ?? new HashSet<string>()).Union(second.Unless ?? new HashSet<string>()).ToHashSet());
}
