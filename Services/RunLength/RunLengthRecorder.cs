using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>Plausible duration of one run; anything outside is dropped (too short: an immediate leave, too long: AFK).</summary>
public record RunLengthBounds(TimeSpan Min, TimeSpan Max)
{
    /// <summary>Generic default for activities that do not state their own.</summary>
    public static readonly RunLengthBounds Default = new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(2));
    /// <summary>
    /// Dungeon floor: the fastest F1 speedruns are about a minute, so under 60 s is an immediate leave or a lobby
    /// hop; the scoreboard-visible stay of a floor is normally 3 to 15 minutes, over 45 minutes is AFK.
    /// </summary>
    public static readonly RunLengthBounds Dungeon = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(45));
    /// <summary>
    /// Kuudra tier: measured full cycles (instance to instance) of a T5 party requeuing directly were 76 to 104 s
    /// (76, 77, 79, 80, 81, 83, 88, 90, 91, 94, 94, 95, 104), so a minimum of 90 s dropped about half of the real runs.
    /// Only completed runs are recorded anyway, so the minimum merely guards against a nonsensical stay; over 20 minutes is AFK.
    /// </summary>
    public static readonly RunLengthBounds Kuudra = new(TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20));
}

/// <summary>One recorded run: its length in seconds and when it was recorded (UTC).</summary>
public record RunLengthEntry(double Seconds, DateTime At);

/// <summary>Aggregate over the recent window of durations recorded under one key.</summary>
public class RunLengthStats
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public double MedianSeconds { get; set; }
    public double P25Seconds { get; set; }
    public double P75Seconds { get; set; }
    /// <summary>The window is the most recent <see cref="WindowSize"/> runs of all players and pods; these are the times of its oldest and newest entry.</summary>
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    public int WindowSize { get; set; }

    /// <summary>Percentiles use linear interpolation between closest ranks (the usual "type 7").</summary>
    public static RunLengthStats? From(string key, IReadOnlyList<(double Seconds, DateTime At)> entries, int windowSize)
    {
        if (entries.Count == 0)
            return null;
        var sorted = entries.Select(e => e.Seconds).Order().ToArray();
        return new RunLengthStats
        {
            Key = key,
            Count = sorted.Length,
            MedianSeconds = Percentile(sorted, 0.5),
            P25Seconds = Percentile(sorted, 0.25),
            P75Seconds = Percentile(sorted, 0.75),
            WindowFrom = entries.Min(e => e.At),
            WindowTo = entries.Max(e => e.At),
            WindowSize = windowSize
        };
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
            return sorted[0];
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}

/// <summary>
/// Records how long one run of an activity really takes, as it happens, independent of the 5 minute period flush and of
/// whether anything was collected. Keyed by activity, see <see cref="RunLengthKeys"/>.
/// </summary>
public interface IRunLengthRecorder
{
    /// <summary>Records one run. Never throws: a store failure or an implausible value is only logged/dropped.</summary>
    Task Record(string key, TimeSpan duration, RunLengthBounds? bounds = null);
    Task<RunLengthStats?> GetStats(string key);
    Task<List<RunLengthStats>> GetAll();
    /// <summary>The raw window entries of a key, newest first; null for an invalid key, empty when nothing was recorded.</summary>
    Task<List<RunLengthEntry>?> GetEntries(string key);
    /// <summary>
    /// The recorded median run length for a key, or null while fewer than <see cref="RunLengthRecorder.MinSamples"/> runs
    /// were recorded (or the store is unavailable). This is the hook for replacing guessed run lengths
    /// (M7Task.RunsPerHour, SlayerBossDrops.Earn).
    /// </summary>
    Task<TimeSpan?> GetMedian(string key);
}

/// <summary>The few operations the recorder needs from shared storage.</summary>
public interface IRunLengthStore
{
    /// <summary>Adds an entry at the head and keeps only the newest <paramref name="window"/>.</summary>
    Task Push(string key, string entry, int window);
    Task<List<string>> Range(string key);
    Task<List<string>> Keys();
}

/// <summary>
/// Redis (the service's existing small shared aux data store, shared by all pods): one capped list per key
/// <c>runlen:{key}</c>, newest first, entries "seconds:unixSeconds", plus the set <c>runlen:keys</c>. No Cassandra table is needed:
/// the data is small (window x keys) and only the recent behaviour matters.
/// </summary>
public class RedisRunLengthStore(IConnectionMultiplexer redis) : IRunLengthStore
{
    private const string KeysKey = "runlen:keys";
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(60);

    public async Task Push(string key, string entry, int window)
    {
        var db = redis.GetDatabase();
        var batch = db.CreateBatch();
        var tasks = new Task[]
        {
            batch.ListLeftPushAsync("runlen:" + key, entry),
            batch.ListTrimAsync("runlen:" + key, 0, window - 1),
            batch.KeyExpireAsync("runlen:" + key, Expiry),
            batch.SetAddAsync(KeysKey, key)
        };
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    public async Task<List<string>> Range(string key)
        => (await redis.GetDatabase().ListRangeAsync("runlen:" + key)).Select(v => v.ToString()).ToList();

    public async Task<List<string>> Keys()
        => (await redis.GetDatabase().SetMembersAsync(KeysKey)).Select(v => v.ToString()).Order().ToList();
}

public class RunLengthRecorder(IRunLengthStore store, ILogger<RunLengthRecorder> logger) : IRunLengthRecorder
{
    /// <summary>Recent runs kept per key (all players, all pods).</summary>
    public const int Window = 200;
    /// <summary>A median is only offered from this many runs on.</summary>
    public const int MinSamples = 5;
    private static readonly Regex ValidKey = new(@"^[A-Za-z0-9_:.\-]{1,64}$", RegexOptions.Compiled);

    public async Task Record(string key, TimeSpan duration, RunLengthBounds? bounds = null)
    {
        try
        {
            bounds ??= RunLengthBounds.Default;
            if (!ValidKey.IsMatch(key ?? "") || duration < bounds.Min || duration > bounds.Max)
                return;
            var entry = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(duration.TotalSeconds)}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            await store.Push(key!, entry, Window);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to record run length for {key}", key);
        }
    }

    public async Task<RunLengthStats?> GetStats(string key)
    {
        if (!ValidKey.IsMatch(key ?? ""))
            return null;
        var entries = (await store.Range(key!)).Select(Parse).Where(e => e != null).Select(e => e!.Value).ToList();
        return RunLengthStats.From(key!, entries, Window);
    }

    public async Task<List<RunLengthEntry>?> GetEntries(string key)
    {
        if (!ValidKey.IsMatch(key ?? ""))
            return null;
        // the store keeps the list newest first
        return (await store.Range(key!)).Select(Parse).Where(e => e != null).Select(e => new RunLengthEntry(e!.Value.Item1, e.Value.Item2)).ToList();
    }

    public async Task<List<RunLengthStats>> GetAll()
    {
        var result = new List<RunLengthStats>();
        foreach (var key in await store.Keys())
        {
            var stats = await GetStats(key);
            if (stats != null)
                result.Add(stats);
        }
        return result;
    }

    public async Task<TimeSpan?> GetMedian(string key)
    {
        try
        {
            var stats = await GetStats(key);
            return stats == null || stats.Count < MinSamples ? null : TimeSpan.FromSeconds(stats.MedianSeconds);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read run length median for {key}", key);
            return null;
        }
    }

    private static (double, DateTime)? Parse(string entry)
    {
        var parts = entry.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !long.TryParse(parts[1], out var unix))
            return null;
        return (seconds, DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);
    }
}

/// <summary>Activity keys for <see cref="IRunLengthRecorder"/>: <c>dungeon:M7</c>, <c>kuudra:T5</c>.</summary>
public static class RunLengthKeys
{
    /// <summary><c>kuudra:T2</c> for a Kuudra tier zone ("Kuudra's Hollow (T2)"), null for anything else.</summary>
    public static string? ForKuudraZone(string? zone)
    {
        if (zone == null || !Tasks.KuudraRewardAttribution.IsTierZone(zone))
            return null;
        var canonical = Tasks.SkyblockZones.Canonical(zone)!;
        return "kuudra:" + canonical[(canonical.LastIndexOf('(') + 1)..^1];
    }

    /// <summary><c>dungeon:M7</c> for a Catacombs floor zone ("The Catacombs (M7)"), null for anything else (including the Entrance).</summary>
    public static string? ForDungeonZone(string? zone)
    {
        var floor = zone == null ? null : Tasks.DungeonRewardAttribution.FloorOf(zone);
        return floor == null ? null : "dungeon:" + floor;
    }
}
