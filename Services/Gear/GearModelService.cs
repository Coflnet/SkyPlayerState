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

/// <summary>The public gear model of one task as of one derivation (everything already privacy rounded).</summary>
public sealed record MeasuredTaskGear(string TaskName, string Category, DateTime ComputedAt, GearScoreFormulaInfo Formula,
    GearScoreSummary? Distribution, GearLateGame? LateGame, GearRateCurve? Curve, List<GearCatalogPiece> Pieces,
    GearRecommendedBuild? Recommended, List<GearModifierEffect> Effects);

/// <summary>
/// The measured gear model per task: loads the last <see cref="WindowWeeks"/> weeks of a task's observations as slim rows
/// (strings and flag sets interned, stats only of "base+gear" rows) and their gear (fingerprint rows are immutable, so
/// they are kept in a size limited LRU cache), and derives (<see cref="Compute"/>) the gear score distribution over
/// player-builds, the late game cohort, the rate curve, the piece catalog, the recommended endgame build and the
/// modifier effects; keeps the public result in memory. Refreshed hourly by <see cref="GearModelRefresher"/>; every pod
/// derives its own copy.
/// </summary>
public class GearModelService
{
    public const int WindowWeeks = 13;
    /// <summary>Most observations read per task (newest first), bounding memory for very popular tasks.</summary>
    public const int MaxObservationsPerTask = 100_000;
    public const int DefaultGearCacheSize = 50_000;
    private const int MaxConsecutiveFailures = 5;

    private readonly IGearObservationReader reader;
    private readonly TaskRegistry registry;
    private readonly GearSetupService setups;
    private readonly ILogger<GearModelService> logger;
    private readonly GearModelOptions options;
    private readonly ConcurrentDictionary<string, MeasuredTaskGear> results = new(StringComparer.OrdinalIgnoreCase);
    private readonly MemoryCache gearCache;
    private static readonly MemoryCacheEntryOptions GearEntry = new() { Size = 1 };

    public GearModelService(IGearObservationReader reader, TaskRegistry registry, GearSetupService setups,
        ILogger<GearModelService> logger) : this(reader, registry, setups, logger, GearModelOptions.Default)
    {
    }

    internal GearModelService(IGearObservationReader reader, TaskRegistry registry, GearSetupService setups,
        ILogger<GearModelService> logger, GearModelOptions options, int gearCacheSize = DefaultGearCacheSize)
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

    /// <summary>The endpoint response: the measured model plus the curated estimates; null for an unknown task.</summary>
    public async Task<TaskGearModel?> GetResponse(string taskName, CancellationToken cancellationToken = default)
    {
        var name = CanonicalTaskName(taskName);
        if (name == null)
            return null;
        var measured = GetMeasured(name);
        var category = measured?.Category ?? GearCategories.ForTask(name) ?? GearModifierTable.Other;
        return new TaskGearModel
        {
            TaskName = name,
            Category = category,
            ComputedAt = measured?.ComputedAt,
            WindowWeeks = WindowWeeks,
            MinPlayers = options.MinPlayers,
            MinHours = options.MinHours,
            ScoreFormula = measured?.Formula ?? FormulaInfo(name, category),
            Distribution = measured?.Distribution,
            LateGame = measured?.LateGame,
            Curve = measured?.Curve,
            Pieces = measured?.Pieces ?? new(),
            RecommendedEndgame = measured?.Recommended,
            ModifierEffects = measured?.Effects ?? new(),
            Estimates = await TryEstimates(name, cancellationToken)
        };
    }

    private static GearScoreFormulaInfo FormulaInfo(string task, string category)
        => FormulaInfo(GearScorer.For(task, category));

    private static GearScoreFormulaInfo FormulaInfo(GearScorer? scorer)
        => scorer == null ? GearModelPublisher.NoFormula() : GearModelPublisher.Formula(scorer.Formula, scorer.Effects.Count);

    private async Task<TaskGearSetups?> TryEstimates(string taskName, CancellationToken cancellationToken)
    {
        try
        {
            return await setups.Get(taskName, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Gear setup estimates of {Task} failed, returning the measured model only", taskName);
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
            logger.LogWarning("Gear model refresh stopped after {Failures} failed tasks in a row", failures);
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
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // a store timeout surfaces as an OperationCanceledException too: only shutdown may end the cycle
            logger.LogWarning("Gear model of {Task} could not be derived: {Error}", task, e.Message);
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
    /// are left out: their pieces were narrowed to other modifiers. A task without a score formula only gets its
    /// modifier effects.
    /// </summary>
    public static MeasuredTaskGear Compute(string task, IReadOnlyList<GearObservationInput> inputs, DateTime now,
        GearModelOptions? options = null, GearEffectTable? effects = null)
    {
        options ??= GearModelOptions.Default;
        var category = GearCategories.ForTask(task) ?? GearModifierTable.Other;
        var matching = inputs.Where(i => i.Observation.Category == category).ToList();
        var modifierEffects = GearModifierEffects.Derive(matching, options)
            .Select(e => GearModelPublisher.Effect(e, options)).OfType<GearModifierEffect>().ToList();
        var scorer = GearScorer.For(task, category, effects);
        if (scorer == null)
            return new MeasuredTaskGear(task, category, now, GearModelPublisher.NoFormula(), null, null, null, new(), null, modifierEffects);
        return Scored(task, category, now, matching, scorer, modifierEffects, options);
    }

    private static MeasuredTaskGear Scored(string task, string category, DateTime now, List<GearObservationInput> matching,
        GearScorer scorer, List<GearModifierEffect> modifierEffects, GearModelOptions options)
    {
        var builds = GearPlayerBuilds.From(matching, scorer);
        var distribution = GearScoreDistribution.Of(builds, options);
        var cohort = distribution == null ? null : GearLateGameCohort.Select(distribution, options);
        var rare = distribution == null ? 0 : distribution.Builds.Sum(b => b.Rare) / distribution.Hours;
        var (curve, publishedCurve) = Curve(distribution, rare, scorer, options);
        var lateGame = cohort == null ? null : GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution!, cohort, curve), options);
        var catalog = GearPieceCatalog.Build(builds, scorer, distribution, options.MinPlayers);
        var recommended = Recommended(catalog, scorer, curve, rare, lateGame, distribution, options);
        return new MeasuredTaskGear(task, category, now, FormulaInfo(scorer),
            distribution == null ? null : GearModelPublisher.Distribution(distribution), lateGame, publishedCurve,
            catalog.Select(GearModelPublisher.Piece).ToList(), recommended, modifierEffects);
    }

    /// <summary>The fitted curve and its public form; both null when it cannot be fit or a published value is not finite.</summary>
    private static (GearCurveModel?, GearRateCurve?) Curve(GearScoreDistribution? distribution, double rare, GearScorer scorer, GearModelOptions options)
    {
        var curve = distribution == null ? null : GearCurveFit.Fit(distribution.Builds, options);
        var published = curve == null ? null : GearModelPublisher.Curve(curve, distribution!, rare, scorer.Formula);
        return published == null ? (null, null) : (curve, published);
    }

    private static GearRecommendedBuild? Recommended(List<GearCatalogEntry> catalog, GearScorer scorer, GearCurveModel? curve,
        double rare, GearLateGame? lateGame, GearScoreDistribution? distribution, GearModelOptions options)
    {
        var build = GearBuildRecommender.Recommend(catalog, scorer);
        if (build == null)
            return null;
        var rate = GearBuildRecommender.Rate(build.Score, curve, rare, lateGame?.CoinsPerHour, scorer.Formula, options);
        return rate == null || !double.IsFinite(rate.CoinsPerHour) ? null : GearModelPublisher.Recommended(build, rate, distribution);
    }

    private async Task<List<GearObservationInput>> Load(string task, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await ReadWindow(task, now, cancellationToken);
        var gear = await GearOf(rows.Select(o => o.GearFingerprint), cancellationToken);
        var keys = new GearChangeKeyCache();
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
/// Derives the gear models shortly after startup and then hourly (<see cref="GearModelService.RefreshAll"/>).
/// Disabled with <c>GEAR:CLUSTERS=false</c> (the flag keeps its name from the earlier setup clusters).
/// </summary>
public class GearModelRefresher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    private readonly GearModelService models;
    private readonly IConfiguration config;
    private readonly ILogger<GearModelRefresher> logger;

    public GearModelRefresher(GearModelService models, IConfiguration config, ILogger<GearModelRefresher> logger)
    {
        this.models = models;
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
                await models.RefreshAll(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // includes an OperationCanceledException of a store timeout: only shutdown ends the loop
                logger.LogError(e, "Gear model refresh failed");
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
