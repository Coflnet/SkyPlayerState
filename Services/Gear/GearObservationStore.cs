using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Storage seam of the gear observations (Scylla in production, in-memory in tests).</summary>
public interface IGearObservationStore
{
    /// <summary>Writes the observation and its by-player index row; the gear json when <paramref name="gearJson"/> is set.</summary>
    Task Write(GearObservation observation, string? gearJson, int ttlSeconds);
    /// <summary>Removes every observation of the player key and the index rows.</summary>
    Task DeletePlayer(string playerKey);
}

/// <summary>Read side of the gear observation tables, for the setup derivation (<see cref="GearClusterService"/>).</summary>
public interface IGearObservationReader
{
    /// <summary>The observations of one task week (partition), newest first, at most <paramref name="limit"/>.</summary>
    Task<List<GearObservationRow>> ReadWeek(string taskName, DateTime weekStart, int limit, CancellationToken cancellationToken);
    /// <summary>The stored gear of the fingerprints; unknown or expired fingerprints are left out.</summary>
    Task<Dictionary<string, ObservedGear>> ReadGear(IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken);
}

/// <summary>
/// Gear observation tables, created lazily and additively (CREATE TABLE IF NOT EXISTS after a system_schema check, like
/// <see cref="Bazaar.History.CassandraBazaarHistoryStore"/>), all with the observation TTL:
/// <code>
/// task_gear_observations ((task_name, week_start), folded_at timeuuid DESC): player_key, category, location, seconds,
///     ramp_weight, stat_bucket, common_coins, rare_coins, item_value, gear_fingerprint, tool_evidence,
///     stats frozen&lt;map&lt;text,double&gt;&gt;, stats_source, stats_age_s, flags frozen&lt;set&lt;text&gt;&gt;
/// gear_fingerprints (fingerprint): category, gear (ObservedGear json), updated_at; TTL + 1 day
/// task_gear_obs_by_player (player_key, task_name, week_start, folded_at): deletion index
/// </code>
/// </summary>
public class CassandraGearObservationStore : IGearObservationStore, IGearObservationReader
{
    private readonly ISession session;
    private readonly GearObservationOptions options;
    private readonly ILogger<CassandraGearObservationStore> logger;
    private readonly System.Threading.SemaphoreSlim initLock = new(1, 1);
    private bool initialized;
    private DateTime retryInitAfter;
    private Exception? initFailure;
    private PreparedStatement? insertObservation, insertIndex, insertGear, selectIndex, deleteObservation, deleteIndex, selectWeek, selectGear;
    public static TimeSpan InitRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    private const string ObservationColumns = "task_name, week_start, folded_at, player_key, category, location, seconds, ramp_weight, stat_bucket, common_coins, rare_coins, item_value, gear_fingerprint, tool_evidence, stats, stats_source, stats_age_s, flags";

    public CassandraGearObservationStore(ISession session, GearObservationOptions options, ILogger<CassandraGearObservationStore> logger)
    {
        this.session = session;
        this.options = options;
        this.logger = logger;
    }

    private async Task Init()
    {
        if (initialized)
            return;
        await initLock.WaitAsync();
        try
        {
            if (initialized)
                return;
            if (initFailure != null && DateTime.UtcNow < retryInitAfter)
                throw new InvalidOperationException("gear observation schema unavailable: " + initFailure.Message, initFailure);
            await TryCreateSchema();
        }
        finally
        {
            initLock.Release();
        }
    }

    private async Task TryCreateSchema()
    {
        try
        {
            await CreateTables(options.TtlSeconds);
            await Prepare();
            initialized = true;
            initFailure = null;
        }
        catch (Exception e)
        {
            initFailure = e;
            retryInitAfter = DateTime.UtcNow + InitRetryDelay;
            logger.LogWarning(e, "Gear observation schema setup failed, retrying in {Delay}", InitRetryDelay);
            throw;
        }
    }

    private async Task CreateTables(int ttl)
    {
        await CreateIfMissing("task_gear_observations", $@"CREATE TABLE IF NOT EXISTS task_gear_observations (
            task_name text, week_start date, folded_at timeuuid,
            player_key text, category text, location text, seconds double, ramp_weight double, stat_bucket int,
            common_coins double, rare_coins double, item_value double, gear_fingerprint text, tool_evidence text,
            stats frozen<map<text, double>>, stats_source text, stats_age_s int, flags frozen<set<text>>,
            PRIMARY KEY ((task_name, week_start), folded_at))
            WITH CLUSTERING ORDER BY (folded_at DESC) AND default_time_to_live = {ttl}");
        await CreateIfMissing("gear_fingerprints", $@"CREATE TABLE IF NOT EXISTS gear_fingerprints (
            fingerprint text PRIMARY KEY, category text, gear text, updated_at timestamp)
            WITH default_time_to_live = {GearTtl(ttl)}");
        await CreateIfMissing("task_gear_obs_by_player", $@"CREATE TABLE IF NOT EXISTS task_gear_obs_by_player (
            player_key text, task_name text, week_start date, folded_at timeuuid,
            PRIMARY KEY (player_key, task_name, week_start, folded_at))
            WITH default_time_to_live = {ttl}");
    }

    private async Task Prepare()
    {
        insertObservation = await session.PrepareAsync($"INSERT INTO task_gear_observations ({ObservationColumns}) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?) USING TTL ?");
        insertIndex = await session.PrepareAsync("INSERT INTO task_gear_obs_by_player (player_key, task_name, week_start, folded_at) VALUES (?,?,?,?) USING TTL ?");
        insertGear = await session.PrepareAsync("INSERT INTO gear_fingerprints (fingerprint, category, gear, updated_at) VALUES (?,?,?,?) USING TTL ?");
        selectIndex = await session.PrepareAsync("SELECT task_name, week_start, folded_at FROM task_gear_obs_by_player WHERE player_key = ?");
        deleteObservation = await session.PrepareAsync("DELETE FROM task_gear_observations WHERE task_name = ? AND week_start = ? AND folded_at = ?");
        deleteIndex = await session.PrepareAsync("DELETE FROM task_gear_obs_by_player WHERE player_key = ?");
        selectWeek = await session.PrepareAsync($"SELECT {RowColumns} FROM task_gear_observations WHERE task_name = ? AND week_start = ?");
        selectGear = await session.PrepareAsync("SELECT gear FROM gear_fingerprints WHERE fingerprint = ?");
    }

    private async Task CreateIfMissing(string table, string cql)
    {
        var exists = await session.ExecuteAsync(new SimpleStatement(
            "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ?", session.Keyspace, table));
        if (exists.Any())
            return;
        await session.ExecuteAsync(new SimpleStatement(cql));
        logger.LogInformation("Created table {Table}", table);
    }

    private static LocalDate Date(DateTime d) => new(d.Year, d.Month, d.Day);

    public async Task Write(GearObservation o, string? gearJson, int ttlSeconds)
    {
        await Init();
        var week = Date(o.WeekStart);
        var id = (TimeUuid)o.ObservationId;
        IDictionary<string, double> stats = o.Stats ?? new();
        // the index row first: an observation is never written without the row its deletion finds it by
        await session.ExecuteAsync(insertIndex!.Bind(o.PlayerKey, o.TaskName, week, id, ttlSeconds));
        await session.ExecuteAsync(insertObservation!.Bind(o.TaskName, week, id, o.PlayerKey, o.Category, OrUnset(o.Location),
            o.Seconds, o.RampWeight, o.StatBucket, o.CommonCoins, o.RareCoins, o.ItemValue, o.GearFingerprint, OrUnset(o.ToolEvidence),
            stats, o.StatsSource, OrUnset(o.StatsAgeSeconds), (ISet<string>)(o.Flags ?? new()), ttlSeconds));
        if (gearJson != null)
            await session.ExecuteAsync(insertGear!.Bind(o.GearFingerprint, o.Category, gearJson, new DateTimeOffset(DateTime.UtcNow), GearTtl(ttlSeconds)));
    }

    /// <summary>Null columns are left unset: binding null would write a tombstone.</summary>
    private static object OrUnset(object? value) => value ?? Unset.Value;

    /// <summary>Gear rows are refreshed at most daily, so they outlive the observations referencing them by a day.</summary>
    internal static int GearTtl(int ttlSeconds) => (int)Math.Min(630_720_000L, ttlSeconds + 86_400L);

    public async Task DeletePlayer(string playerKey)
    {
        await Init();
        var rows = await session.ExecuteAsync(selectIndex!.Bind(playerKey));
        foreach (var row in rows)
            await session.ExecuteAsync(deleteObservation!.Bind(row.GetValue<string>("task_name"),
                row.GetValue<LocalDate>("week_start"), row.GetValue<TimeUuid>("folded_at")));
        await session.ExecuteAsync(deleteIndex!.Bind(playerKey));
    }

    private const string RowColumns = "player_key, folded_at, seconds, common_coins, rare_coins, category, gear_fingerprint, flags, stats_source, stats";
    private const int GearReadConcurrency = 8;
    /// <summary>Rows per page of <see cref="ReadWeek"/>; small in the integration test to cover paging.</summary>
    internal int ReadPageSize { get; set; } = 2_000;

    public async Task<List<GearObservationRow>> ReadWeek(string taskName, DateTime weekStart, int limit, CancellationToken cancellationToken)
    {
        await Init();
        var result = new List<GearObservationRow>();
        byte[]? pagingState = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statement = selectWeek!.Bind(taskName, Date(weekStart)).SetPageSize(ReadPageSize).SetAutoPage(false);
            if (pagingState != null)
                statement.SetPagingState(pagingState);
            var rows = await session.ExecuteAsync(statement);
            result.AddRange(rows.Select(MapRow).Take(limit - result.Count));
            pagingState = rows.PagingState;
        } while (pagingState != null && result.Count < limit);
        return result;
    }

    private static GearObservationRow MapRow(Row row)
    {
        var stats = row.GetValue<IDictionary<string, double>>("stats");
        var flags = row.GetValue<IEnumerable<string>>("flags");
        return new GearObservationRow
        {
            PlayerKey = row.GetValue<string>("player_key") ?? string.Empty,
            FoldedAt = row.GetValue<TimeUuid>("folded_at").GetDate().UtcDateTime,
            Seconds = row.GetValue<double?>("seconds") ?? 0,
            CommonCoins = row.GetValue<double?>("common_coins") ?? 0,
            RareCoins = row.GetValue<double?>("rare_coins") ?? 0,
            Category = row.GetValue<string>("category") ?? string.Empty,
            GearFingerprint = row.GetValue<string>("gear_fingerprint") ?? string.Empty,
            Flags = flags == null ? GearObservationRow.NoFlags : flags.ToHashSet(StringComparer.Ordinal),
            StatsSource = row.GetValue<string>("stats_source") ?? GearObservationBuilder.StatsNone,
            Stats = stats == null ? GearObservationRow.NoStats : new Dictionary<string, double>(stats, StringComparer.Ordinal)
        };
    }

    public async Task<Dictionary<string, ObservedGear>> ReadGear(IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken)
    {
        await Init();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, ObservedGear>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(fingerprints, new ParallelOptions { MaxDegreeOfParallelism = GearReadConcurrency, CancellationToken = cancellationToken },
            async (fingerprint, _) =>
            {
                var gear = await ReadOneGear(fingerprint);
                if (gear != null)
                    result[fingerprint] = gear;
            });
        return new Dictionary<string, ObservedGear>(result, StringComparer.Ordinal);
    }

    private async Task<ObservedGear?> ReadOneGear(string fingerprint)
    {
        var row = (await session.ExecuteAsync(selectGear!.Bind(fingerprint))).FirstOrDefault();
        var json = row?.GetValue<string>("gear");
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ObservedGear>(json, GearFingerprint.Json);
        }
        catch (JsonException e)
        {
            logger.LogWarning("Unreadable gear json of fingerprint {Fingerprint}: {Error}", fingerprint, e.Message);
            return null;
        }
    }
}
