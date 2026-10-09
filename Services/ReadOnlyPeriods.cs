using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>A fixed, parameterized SELECT. Only <see cref="ReadOnlyPeriodQueries"/> creates them.</summary>
public sealed record ReadOnlyQuery(string Cql, object[] Values);

/// <summary>
/// The one place that guarantees the period routes only read: the statements are built here from typed parameters
/// (a validated player uuid, an optional time bound, a bounded limit) over an allow-list of table and columns, and
/// <see cref="ReadOnlyCqlRunner"/> refuses to execute anything that is not exactly such a statement. There is no way to
/// pass CQL text in, so writes (and other tables, wildcards, multiple statements) are impossible by construction.
/// </summary>
public static class ReadOnlyPeriodQueries
{
    public const int MaxLimit = 500;
    public static readonly IReadOnlyList<string> Tables = ["historyperiods2"];
    public static readonly IReadOnlyList<string> Columns = ["playeruuid", "server", "location", "profit", "starttime", "endtime", "itemscollected", "detectedtask"];

    private static readonly Regex PlayerUuid = new("^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // the only statement shapes that may be executed, matched against the whole text
    private static readonly Regex Allowed = new(
        "^SELECT (?<cols>[a-z]+(?:, [a-z]+)*) FROM (?<table>[a-z0-9]+) WHERE playeruuid = \\?(?<before> AND endtime < \\?)? LIMIT (?<limit>[0-9]{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A player's periods, newest first (the table is clustered by end time, descending): a single partition read.</summary>
    /// <param name="playerUuid">32 hex characters, without dashes</param>
    public static ReadOnlyQuery HistoryForPlayer(string playerUuid, DateTime? before, int limit)
    {
        var uuid = (playerUuid ?? "").Replace("-", "").ToLowerInvariant();
        if (!PlayerUuid.IsMatch(uuid))
            throw new ArgumentException("player uuid must be 32 hex characters", nameof(playerUuid));
        limit = Math.Clamp(limit, 1, MaxLimit);
        var cols = string.Join(", ", Columns);
        return before.HasValue
            ? new ReadOnlyQuery($"SELECT {cols} FROM historyperiods2 WHERE playeruuid = ? AND endtime < ? LIMIT {limit}", [uuid, before.Value.ToUniversalTime()])
            : new ReadOnlyQuery($"SELECT {cols} FROM historyperiods2 WHERE playeruuid = ? LIMIT {limit}", [uuid]);
    }

    /// <summary>Throws unless <paramref name="query"/> is exactly one of the fixed SELECT shapes over allow-listed table and columns, with matching parameters.</summary>
    public static void EnsureReadOnly(ReadOnlyQuery query)
    {
        var match = Allowed.Match(query?.Cql ?? "");
        if (!match.Success)
            throw new InvalidOperationException("only the fixed read-only period queries may be executed");
        if (!Tables.Contains(match.Groups["table"].Value)
            || match.Groups["cols"].Value.Split(", ").Any(c => !Columns.Contains(c)))
            throw new InvalidOperationException("table or column is not on the read-only allow-list");
        var placeholders = match.Groups["before"].Success ? 2 : 1;
        var values = query!.Values;
        if (values == null || values.Length != placeholders || values[0] is not string uuid || !PlayerUuid.IsMatch(uuid)
            || (placeholders == 2 && values[1] is not DateTime))
            throw new InvalidOperationException("parameters do not match the fixed read-only query");
        if (int.Parse(match.Groups["limit"].Value) is < 1 or > MaxLimit)
            throw new InvalidOperationException("limit out of range");
    }
}

/// <summary>Executes <see cref="ReadOnlyQuery"/> statements, after <see cref="ReadOnlyPeriodQueries.EnsureReadOnly"/>. Every read of the period routes goes through here.</summary>
public class ReadOnlyCqlRunner(ISession session)
{
    public virtual async Task<List<TrackedProfitService.Period>> ReadPeriods(ReadOnlyQuery query)
    {
        ReadOnlyPeriodQueries.EnsureReadOnly(query);
        RowSet rows;
        try
        {
            rows = await session.ExecuteAsync(new SimpleStatement(query.Cql, query.Values));
        }
        catch (global::Cassandra.InvalidQueryException)
        {
            return []; // table not created yet (it is created lazily by the first stored period)
        }
        return rows.Select(r => new TrackedProfitService.Period
        {
            PlayerUuid = r.GetValue<string>("playeruuid"),
            Server = r.GetValue<string>("server"),
            Location = r.GetValue<string>("location"),
            Profit = r.GetValue<long>("profit"),
            StartTime = DateTime.SpecifyKind(r.GetValue<DateTime>("starttime"), DateTimeKind.Utc),
            EndTime = DateTime.SpecifyKind(r.GetValue<DateTime>("endtime"), DateTimeKind.Utc),
            ItemsCollected = r.GetValue<IDictionary<string, int>>("itemscollected")?.ToDictionary(k => k.Key, k => k.Value) ?? new(),
            DetectedTask = r.GetValue<string>("detectedtask")
        }).ToList();
    }
}

/// <summary>A stay: one or more consecutive periods of the same player, server and location.</summary>
public class PeriodStay
{
    public string? Server { get; set; }
    public string? Location { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double DurationSeconds { get; set; }
    public int PeriodCount { get; set; }
    public long Profit { get; set; }
    public List<string> Tasks { get; set; } = [];
    public Dictionary<string, int> ItemsCollected { get; set; } = new();
}

public class LocationDuration
{
    public string Location { get; set; } = "";
    public int Stays { get; set; }
    public double TotalSeconds { get; set; }
    public double MedianSeconds { get; set; }
}

public class DurationSummary
{
    public int Periods { get; set; }
    public int Stays { get; set; }
    public double TotalSeconds { get; set; }
    public List<LocationDuration> ByLocation { get; set; } = [];
}

public static class PeriodStays
{
    /// <summary>The 5 minute flush starts the next period right where the last one ended; this much slack is allowed between two periods of one stay.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(2);

    /// <summary>Every period as a stay of its own, oldest first.</summary>
    public static List<PeriodStay> Single(IEnumerable<TrackedProfitService.Period> periods)
        => periods.OrderBy(p => p.StartTime).Select(p => Merge([p])).ToList();

    /// <summary>
    /// Consecutive periods (by start time) of the same server and location whose gap is at most <see cref="MaxGap"/> become
    /// one stay; a zone change, server change or a longer gap starts a new one. Oldest first.
    /// </summary>
    public static List<PeriodStay> Merge(IEnumerable<TrackedProfitService.Period> periods, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? MaxGap;
        var stays = new List<PeriodStay>();
        var group = new List<TrackedProfitService.Period>();
        foreach (var p in periods.OrderBy(p => p.StartTime))
        {
            if (group.Count > 0)
            {
                var last = group[^1];
                if (last.Server != p.Server || last.Location != p.Location || p.StartTime - group.Max(g => g.EndTime) > gap)
                {
                    stays.Add(Merge(group));
                    group = [];
                }
            }
            group.Add(p);
        }
        if (group.Count > 0)
            stays.Add(Merge(group));
        return stays;
    }

    private static PeriodStay Merge(List<TrackedProfitService.Period> group)
    {
        var start = group.Min(g => g.StartTime);
        var end = group.Max(g => g.EndTime);
        var items = new Dictionary<string, int>();
        foreach (var item in group.SelectMany(g => g.ItemsCollected ?? new()))
            items[item.Key] = items.GetValueOrDefault(item.Key) + item.Value;
        return new PeriodStay
        {
            Server = group[0].Server,
            Location = group[0].Location,
            Start = start,
            End = end,
            DurationSeconds = Math.Max(0, (end - start).TotalSeconds),
            PeriodCount = group.Count,
            Profit = group.Sum(g => g.Profit),
            Tasks = group.Select(g => g.DetectedTask).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList()!,
            ItemsCollected = items
        };
    }

    public static DurationSummary Summarize(IReadOnlyCollection<PeriodStay> stays)
        => new()
        {
            Periods = stays.Sum(s => s.PeriodCount),
            Stays = stays.Count,
            TotalSeconds = stays.Sum(s => s.DurationSeconds),
            ByLocation = stays.GroupBy(s => s.Location ?? "")
                .Select(g => new LocationDuration
                {
                    Location = g.Key,
                    Stays = g.Count(),
                    TotalSeconds = g.Sum(s => s.DurationSeconds),
                    MedianSeconds = RunLengthStats.Percentile(g.Select(s => s.DurationSeconds).Order().ToArray(), 0.5)
                }).OrderByDescending(l => l.TotalSeconds).ToList()
        };
}
