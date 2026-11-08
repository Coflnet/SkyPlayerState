using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tasks.Gear;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>The public measured setups of one task as of one derivation.</summary>
public sealed record MeasuredTaskGear(string TaskName, string Category, DateTime ComputedAt,
    List<GearCluster> Clusters, List<GearModifierEffect> Effects);

/// <summary>
/// Measured gear setups per task: loads the last <see cref="WindowWeeks"/> weeks of a task's observations as slim rows
/// (strings and flag sets interned, stats only of "base+gear" rows) and their gear (fingerprint rows are immutable, so
/// they are kept in a size limited LRU cache), computes each row's setup keys once (cached per fingerprint, flags and
/// category), derives the clusters and modifier effects
/// (<see cref="GearClustering"/>, <see cref="GearModifierEffects"/>) and keeps the public result in memory. Refreshed
/// hourly by <see cref="GearClusterRefresher"/>; every pod derives its own copy.
/// </summary>
public class GearClusterService
{
    public const int WindowWeeks = 13;
    /// <summary>Most observations read per task (newest first), bounding memory for very popular tasks.</summary>
    public const int MaxObservationsPerTask = 100_000;
    public const int DefaultGearCacheSize = 50_000;
    private const int MaxConsecutiveFailures = 5;

    private readonly IGearObservationReader reader;
    private readonly TaskRegistry registry;
    private readonly GearSetupService setups;
    private readonly ILogger<GearClusterService> logger;
    private readonly GearClusterOptions options;
    private readonly ConcurrentDictionary<string, MeasuredTaskGear> results = new(StringComparer.OrdinalIgnoreCase);
    private readonly MemoryCache gearCache;
    private static readonly MemoryCacheEntryOptions GearEntry = new() { Size = 1 };

    public GearClusterService(IGearObservationReader reader, TaskRegistry registry, GearSetupService setups,
        ILogger<GearClusterService> logger) : this(reader, registry, setups, logger, GearClusterOptions.Default)
    {
    }

    internal GearClusterService(IGearObservationReader reader, TaskRegistry registry, GearSetupService setups,
        ILogger<GearClusterService> logger, GearClusterOptions options, int gearCacheSize = DefaultGearCacheSize)
    {
        gearCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = gearCacheSize });
        this.reader = reader;
        this.registry = registry;
        this.setups = setups;
        this.logger = logger;
        this.options = options;
    }

    /// <summary>The canonical name of a public method task, null for unknown or hidden tasks.</summary>
    public string? CanonicalTaskName(string taskName)
        => registry.GetByName(taskName) is MethodTask { IsHidden: false } task ? TaskEstimator.GetTaskName(task) : null;

    /// <summary>The last derivation of the task, null before the first one.</summary>
    public MeasuredTaskGear? GetMeasured(string taskName) => results.GetValueOrDefault(taskName);

    /// <summary>The endpoint response: measured clusters plus the curated estimates; null for an unknown task.</summary>
    public async Task<TaskGearClusters?> GetResponse(string taskName, CancellationToken cancellationToken = default)
    {
        var name = CanonicalTaskName(taskName);
        if (name == null)
            return null;
        var measured = GetMeasured(name);
        return new TaskGearClusters
        {
            TaskName = name,
            Category = measured?.Category ?? GearCategories.ForTask(name) ?? GearModifierTable.Other,
            ComputedAt = measured?.ComputedAt,
            WindowWeeks = WindowWeeks,
            MinPlayers = options.MinPlayers,
            MinHours = options.MinHours,
            Clusters = measured?.Clusters ?? new(),
            ModifierEffects = measured?.Effects ?? new(),
            Estimates = await TryEstimates(name, cancellationToken)
        };
    }

    private async Task<TaskGearSetups?> TryEstimates(string taskName, CancellationToken cancellationToken)
    {
        try
        {
            return await setups.Get(taskName, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Gear setup estimates of {Task} failed, returning the measured clusters only", taskName);
            return null;
        }
    }

    /// <summary>Derives every public method task once; stops a cycle after repeated failures (store down).</summary>
    public async Task RefreshAll(CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var task in registry.PublicMethodTasks.Select(TaskEstimator.GetTaskName).Distinct())
        {
            if (await TryRefresh(task, cancellationToken))
            {
                failures = 0;
                continue;
            }
            if (++failures < MaxConsecutiveFailures)
                continue;
            logger.LogWarning("Gear cluster refresh stopped after {Failures} failed tasks in a row", failures);
            return;
        }
    }

    private async Task<bool> TryRefresh(string task, CancellationToken cancellationToken)
    {
        try
        {
            await Refresh(task, DateTime.UtcNow, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Gear clusters of {Task} could not be derived: {Error}", task, e.Message);
            return false;
        }
    }

    public async Task<MeasuredTaskGear> Refresh(string task, DateTime now, CancellationToken cancellationToken = default)
    {
        var inputs = await Load(task, now, cancellationToken);
        var result = Compute(task, inputs, now, options);
        results[task] = result;
        return result;
    }

    /// <summary>
    /// Derives the public result (pure). Observations filtered with another category (the task's binding changed since)
    /// are left out: their pieces were narrowed to other modifiers.
    /// </summary>
    public static MeasuredTaskGear Compute(string task, IReadOnlyList<GearClusterInput> inputs, DateTime now, GearClusterOptions? options = null)
    {
        options ??= GearClusterOptions.Default;
        var category = GearCategories.ForTask(task) ?? GearModifierTable.Other;
        var matching = inputs.Where(i => i.Observation.Category == category).ToList();
        var clusters = GearClusterPublisher.Clusters(GearClustering.Derive(matching, options), options);
        var effects = GearModifierEffects.Derive(matching, category, options)
            .Select(e => GearClusterPublisher.Effect(e, options)).OfType<GearModifierEffect>().ToList();
        return new MeasuredTaskGear(task, category, now, clusters, effects);
    }

    private async Task<List<GearClusterInput>> Load(string task, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await ReadWindow(task, now, cancellationToken);
        var gear = await GearOf(rows.Select(o => o.GearFingerprint), cancellationToken);
        var keys = new GearSetupKeyCache();
        return rows.Where(o => gear.ContainsKey(o.GearFingerprint))
            .Select(o => keys.Input(o, gear[o.GearFingerprint]))
            .ToList();
    }

    private async Task<List<GearObservationRow>> ReadWindow(string task, DateTime now, CancellationToken cancellationToken)
    {
        var oldest = now.AddDays(-7 * WindowWeeks);
        var week = GearObservationBuilder.WeekStart(now);
        var interner = new GearRowInterner();
        var rows = new List<GearObservationRow>();
        for (var i = 0; i < WindowWeeks && rows.Count < MaxObservationsPerTask; i++)
        {
            var read = await reader.ReadWeek(task, week.AddDays(-7 * i), MaxObservationsPerTask - rows.Count, cancellationToken);
            rows.AddRange(read.Where(o => o.FoldedAt >= oldest).Select(interner.Slim));
        }
        return rows;
    }

    /// <summary>
    /// The gear of the fingerprints: cache hits first, then one read for the rest. The result is assembled before the
    /// cache can evict anything, so a full cache never drops gear of the task being derived.
    /// </summary>
    private async Task<Dictionary<string, ObservedGear>> GearOf(IEnumerable<string> fingerprints, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ObservedGear>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var fingerprint in fingerprints.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal))
        {
            if (gearCache.TryGetValue(fingerprint, out ObservedGear? cached) && cached != null)
                result[fingerprint] = cached;
            else
                missing.Add(fingerprint);
        }
        if (missing.Count == 0)
            return result;
        foreach (var (fingerprint, gear) in await reader.ReadGear(missing, cancellationToken))
        {
            result[fingerprint] = gear;
            gearCache.Set(fingerprint, gear, GearEntry);
        }
        return result;
    }
}

/// <summary>Setup keys per (fingerprint, flags, category) within one derivation: rows with the same gear share one key array.</summary>
internal sealed class GearSetupKeyCache
{
    private readonly Dictionary<(string Fingerprint, string Flags, string Category), GearSetupKey?[]> keys = new();

    public GearClusterInput Input(GearObservationRow row, ObservedGear gear)
    {
        var id = (row.GearFingerprint, string.Join(",", row.Flags.Order(StringComparer.Ordinal)), row.Category);
        if (!keys.TryGetValue(id, out var cached))
            keys[id] = cached = GearSetupKeys.For(row, gear);
        return new GearClusterInput(row, gear, cached);
    }
}

/// <summary>Shares the repeated strings and flag sets of a task's rows and drops the stats the derivation does not use.</summary>
internal sealed class GearRowInterner
{
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> flagSets = new(StringComparer.Ordinal);

    public GearObservationRow Slim(GearObservationRow row) => row with
    {
        PlayerKey = Intern(row.PlayerKey),
        Category = Intern(row.Category),
        GearFingerprint = Intern(row.GearFingerprint),
        StatsSource = Intern(row.StatsSource),
        Flags = Flags(row.Flags),
        Stats = row.StatsSource == GearObservationBuilder.StatsBaseGear && row.Stats.Count > 0 ? row.Stats : GearObservationRow.NoStats
    };

    private string Intern(string value)
    {
        if (strings.TryGetValue(value, out var shared))
            return shared;
        strings[value] = value;
        return value;
    }

    private IReadOnlySet<string> Flags(IReadOnlySet<string> flags)
    {
        if (flags.Count == 0)
            return GearObservationRow.NoFlags;
        var id = string.Join(",", flags.Order(StringComparer.Ordinal));
        if (!flagSets.TryGetValue(id, out var shared))
            flagSets[id] = shared = flags;
        return shared;
    }
}

/// <summary>
/// Derives the measured gear setups shortly after startup and then hourly (<see cref="GearClusterService.RefreshAll"/>).
/// Disabled with <c>GEAR:CLUSTERS=false</c>.
/// </summary>
public class GearClusterRefresher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    private readonly GearClusterService clusters;
    private readonly IConfiguration config;
    private readonly ILogger<GearClusterRefresher> logger;

    public GearClusterRefresher(GearClusterService clusters, IConfiguration config, ILogger<GearClusterRefresher> logger)
    {
        this.clusters = clusters;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["GEAR:CLUSTERS"] == "false")
            return;
        if (!await Wait(StartDelay, stoppingToken))
            return;
        do
        {
            try
            {
                await clusters.RefreshAll(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Gear cluster refresh failed");
            }
        } while (await Wait(Interval, stoppingToken));
    }

    private static async Task<bool> Wait(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
