using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// One time seeding of the task aggregates from the existing location keyed
/// aggregates, so day one estimates already beat the pure formula tier.
/// Classifies each (location, item) into tasks by their detection rules and
/// writes the data into the Unknown bucket under a synthetic backfill instance.
/// </summary>
public class TaskBackfillService
{
    private readonly MethodAggregateService locationAggregates;
    private readonly TaskAggregateService taskAggregates;
    private readonly TaskRegistry registry;
    private readonly CoinValueRegistry coinValues;
    private readonly ILogger<TaskBackfillService> logger;

    public TaskBackfillService(MethodAggregateService locationAggregates, TaskAggregateService taskAggregates,
        TaskRegistry registry, CoinValueRegistry coinValues, ILogger<TaskBackfillService> logger)
    {
        this.locationAggregates = locationAggregates;
        this.taskAggregates = taskAggregates;
        this.registry = registry;
        this.coinValues = coinValues;
        this.logger = logger;
    }

    /// <summary>
    /// Backfill from the location aggregates for the given locations.
    /// Idempotent per (task) since it writes to a single backfill instance row.
    /// Returns the number of task aggregates seeded.
    /// </summary>
    public async Task<int> Backfill(IEnumerable<string> locations, int days = 7)
    {
        var signatures = registry.MethodTasks.Select(t => t.GetDetectionSignature()).ToList();
        await coinValues.EnsureFresh();
        var seeded = 0;
        foreach (var location in locations.Distinct())
        {
            try
            {
                var aggregates = await locationAggregates.GetLocationAggregates(location, days);
                if (aggregates.Count == 0)
                    continue;
                // group the location's item aggregates by the task their items detect
                var itemsAtLocation = aggregates.Select(a => a.ItemTag).ToHashSet();
                var matchingTasks = signatures.Where(s =>
                    (s.Locations.Count == 0 || s.Locations.Contains(location))
                    && (s.DetectionItems.Count == 0 || s.DetectionItems.Overlaps(itemsAtLocation))).ToList();
                foreach (var sig in matchingTasks)
                {
                    var relevant = sig.DetectionItems.Count > 0
                        ? aggregates.Where(a => sig.DetectionItems.Contains(a.ItemTag)
                            || !signatures.Any(o => o.MethodName != sig.MethodName && o.DetectionItems.Contains(a.ItemTag))).ToList()
                        : aggregates;
                    var totalHours = relevant.Sum(a => a.TotalHours);
                    if (totalHours <= 0)
                        continue;
                    var itemCounts = relevant.GroupBy(a => a.ItemTag)
                        .ToDictionary(g => g.Key, g => (double)g.Sum(a => a.TotalItems));
                    // approximate price for the ref value; drift correction handles staleness at read
                    double refValue = itemCounts.Sum(e => coinValues.Value(e.Key, null) * e.Value);
                    taskAggregates.AddContribution(sig.MethodName, StatScoreService.UnknownBucket,
                        $"backfill:{location}", totalHours * 3600, itemCounts, 0, 0, refValue, null);
                    seeded++;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to backfill location {location}", location);
            }
        }
        await taskAggregates.Flush();
        logger.LogInformation("Backfilled {count} task aggregates from {locations} locations", seeded, locations.Count());
        return seeded;
    }
}
