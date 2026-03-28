using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Aggregates item collection rates across all players, partitioned by location.
/// Used to estimate average rates for new players with no personal data.
/// </summary>
public class MethodAggregateService
{
    private Table<LocationItemAggregate> aggregateTable;
    private readonly ISession session;
    private const string TABLE_NAME = "location_item_aggregates";
    private const int TTL_SECONDS = 604800; // 1 week

    public MethodAggregateService(ISession session)
    {
        this.session = session;
    }

    private async Task Setup()
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<LocationItemAggregate>()
                .PartitionKey(a => a.Location)
                .ClusteringKey(a => a.ItemTag)
                .ClusteringKey(a => a.DayBucket, SortOrder.Descending)
            );
        aggregateTable = new Table<LocationItemAggregate>(session, mapping, TABLE_NAME);
        await aggregateTable.CreateIfNotExistsAsync();
    }

    /// <summary>
    /// Record a player's item collection data for aggregation.
    /// Called when a period is stored to build global averages.
    /// </summary>
    public async Task RecordPeriod(TrackedProfitService.Period period)
    {
        if (aggregateTable == null)
            await Setup();

        var duration = (period.EndTime - period.StartTime).TotalHours;
        if (duration <= 0 || period.ItemsCollected == null || period.ItemsCollected.Count == 0)
            return;

        var dayBucket = period.EndTime.Date;

        foreach (var (tag, count) in period.ItemsCollected)
        {
            if (count <= 0) continue;

            // Read-then-write to accumulate (Cassandra LWT would be more correct
            // but expensive; eventual consistency is fine for estimates)
            var existing = await GetAggregate(period.Location, tag, dayBucket);
            var aggregate = existing ?? new LocationItemAggregate
            {
                Location = period.Location,
                ItemTag = tag,
                DayBucket = dayBucket
            };

            aggregate.TotalItems += count;
            aggregate.TotalHours += duration;
            aggregate.SampleCount += 1;

            await aggregateTable!.Insert(aggregate).SetTTL(TTL_SECONDS).ExecuteAsync();
        }
    }

    private async Task<LocationItemAggregate> GetAggregate(string location, string itemTag, DateTime dayBucket)
    {
        if (aggregateTable == null)
            await Setup();
        return (await aggregateTable!
            .Where(a => a.Location == location && a.ItemTag == itemTag && a.DayBucket == dayBucket)
            .ExecuteAsync())
            .FirstOrDefault();
    }

    /// <summary>
    /// Get average items per hour at a location for specific items over the last N days.
    /// </summary>
    public async Task<Dictionary<string, double>> GetAverageRates(string location, IEnumerable<string> itemTags, int days = 7)
    {
        if (aggregateTable == null)
            await Setup();

        var cutoff = DateTime.UtcNow.AddDays(-days).Date;
        var result = new Dictionary<string, double>();

        foreach (var tag in itemTags)
        {
            var aggregates = (await aggregateTable!
                .Where(a => a.Location == location && a.ItemTag == tag && a.DayBucket >= cutoff)
                .ExecuteAsync())
                .ToList();

            if (aggregates.Count == 0) continue;

            var totalItems = aggregates.Sum(a => a.TotalItems);
            var totalHours = aggregates.Sum(a => a.TotalHours);
            if (totalHours > 0)
                result[tag] = totalItems / totalHours;
        }

        return result;
    }

    /// <summary>
    /// Get all aggregated rates for a location over the last N days.
    /// </summary>
    public async Task<List<LocationItemAggregate>> GetLocationAggregates(string location, int days = 7)
    {
        if (aggregateTable == null)
            await Setup();

        var cutoff = DateTime.UtcNow.AddDays(-days).Date;
        return (await aggregateTable!
            .Where(a => a.Location == location && a.DayBucket >= cutoff)
            .AllowFiltering()
            .ExecuteAsync())
            .ToList();
    }
}

public class LocationItemAggregate
{
    public string Location { get; set; }
    public string ItemTag { get; set; }
    /// <summary>
    /// Day bucket (start of day UTC) for time-based partitioning
    /// </summary>
    public DateTime DayBucket { get; set; }
    /// <summary>
    /// Total items collected across all players for this location+item+day
    /// </summary>
    public long TotalItems { get; set; }
    /// <summary>
    /// Total hours spent by all players at this location collecting this item
    /// </summary>
    public double TotalHours { get; set; }
    /// <summary>
    /// Number of period samples contributing to this aggregate
    /// </summary>
    public int SampleCount { get; set; }
}
