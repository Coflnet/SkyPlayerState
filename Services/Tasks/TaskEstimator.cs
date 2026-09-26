using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

// TaskEstimate/TaskDropRate live in TaskEstimate.cs, since MethodTask.ComputeFromServerEstimate
// renders them and TaskParams.ServerEstimates is serialized to SkyModCommands via the generated
// PlayerState.Client - its shape must stay in sync with the OpenAPI schema.

/// <summary>
/// Produces the blended, stat aware, saturation adjusted coins per hour estimate
/// for every task and one player. This is the tier that replaces the mod side
/// in-process global averages.
/// </summary>
public class TaskEstimator
{
    private readonly TaskRegistry registry;
    private readonly TaskAggregateService aggregates;
    private readonly StatScoreService statScore;
    private readonly TaskActivityService activityService;
    private readonly CoinValueRegistry coinValues;
    private readonly ILogger<TaskEstimator> logger;

    // pseudo-count shrinkage strengths (in effective hours)
    private const double GlobalPseudoHours = 2;
    private const double BucketPseudoHours = 4;
    private const double GlobalHoursCap = 500;
    private const int MinContributors = 3;
    private const double SaturationFloor = 0.75;
    private const double PriceDriftClampLow = 0.25;
    private const double PriceDriftClampHigh = 4;
    private static readonly TimeSpan OptionalReadTimeout = TimeSpan.FromSeconds(1);

    public TaskEstimator(TaskRegistry registry, TaskAggregateService aggregates, StatScoreService statScore,
        TaskActivityService activityService, CoinValueRegistry coinValues, ILogger<TaskEstimator> logger)
    {
        this.registry = registry;
        this.aggregates = aggregates;
        this.statScore = statScore;
        this.activityService = activityService;
        this.coinValues = coinValues;
        this.logger = logger;
    }

    /// <summary>
    /// Estimate coins per hour for every method task for this player.
    /// </summary>
    public async Task<List<TaskEstimate>> EstimateAll(StateObject state, Dictionary<string, double> prices,
        CancellationToken cancellationToken = default)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-estimate-all");
        var snapshot = aggregates.GetSnapshot();
        var coinValuesTask = RefreshCoinValues(cancellationToken);
        var countsTask = LoadOptional(activityService.GetCounts(), new(), "task activity counts", cancellationToken);
        var deltasTask = LoadOptional(activityService.GetChange20m(), new(), "task activity changes", cancellationToken);
        var playerStatsTask = LoadPlayerStats(state?.McInfo?.Uuid ?? Guid.Empty,
            aggregates.GetPlayerStats, OptionalReadTimeout, logger, cancellationToken);
        await Task.WhenAll(coinValuesTask, countsTask, deltasTask, playerStatsTask);
        var counts = countsTask.Result;
        var deltas = deltasTask.Result;
        var playerStats = playerStatsTask.Result;

        var results = new List<TaskEstimate>();
        foreach (var task in registry.MethodTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var taskName = GetTaskName(task);
                results.Add(await EstimateOne(task, taskName, state, prices, snapshot, counts, deltas,
                    playerStats.GetValueOrDefault(taskName)));
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to estimate task {task}", task.Name);
            }
        }
        span?.SetTag("tasks", results.Count);
        return results;
    }

    internal static string GetTaskName(MethodTask task) => task.GetDetectionSignature().MethodName;

    private async Task RefreshCoinValues(CancellationToken cancellationToken)
    {
        try
        {
            await coinValues.EnsureFresh().WaitAsync(OptionalReadTimeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            logger.LogWarning(e, "resource coin values timed out; using cached values");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "resource coin values unavailable; using cached values");
        }
    }

    private async Task<T> LoadOptional<T>(Task<T> operation, T fallback, string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(OptionalReadTimeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            logger.LogWarning(e, "{name} timed out; using fallback", name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{name} unavailable; using fallback", name);
        }
        return fallback;
    }

    internal static async Task<Dictionary<string, TaskPlayerStatRow>> LoadPlayerStats(Guid playerUuid,
        Func<string, Task<List<TaskPlayerStatRow>>> load, TimeSpan timeout, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (playerUuid == Guid.Empty)
            return new Dictionary<string, TaskPlayerStatRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = await load(playerUuid.ToString("N")).WaitAsync(timeout, cancellationToken);
            return rows.GroupBy(s => s.TaskName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (TimeoutException e)
        {
            logger.LogWarning(e, "personal task stats timed out for {player}; using community estimates", playerUuid);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "personal task stats unavailable for {player}; using community estimates", playerUuid);
        }
        return new Dictionary<string, TaskPlayerStatRow>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<TaskEstimate> EstimateOne(MethodTask task, string name, StateObject state,
        Dictionary<string, double> prices, Dictionary<(string, byte), BucketAggregate> snapshot,
        Dictionary<string, int> counts, Dictionary<string, int> deltas, TaskPlayerStatRow personal)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-estimate");
        var signature = task.GetDetectionSignature();
        span?.SetTag("task", name);

        var factors = task.StatFactors;
        var bucket = await statScore.GetBucket(factors, state);
        var mB = BucketPriorMultiplier(task, bucket);

        // tier 3: formula
        var rFormula = FormulaRate(task, prices);

        // tier 2a: global (all buckets merged) with min-contributor gating
        var globalAgg = MergeBuckets(snapshot, name);
        var globalContributors = globalAgg?.Contributors.Count ?? 0;
        double hG = 0, rG = 0;
        if (globalAgg != null && globalContributors >= MinContributors)
        {
            hG = Math.Min(globalAgg.WSeconds / 3600.0, GlobalHoursCap);
            rG = RateFromAggregate(task, globalAgg, prices);
        }
        var rGlobalStar = (hG * rG + GlobalPseudoHours * rFormula) / (hG + GlobalPseudoHours);

        // tier 2b: stat bucket, shrunk toward the stat-adjusted global
        var bucketAgg = snapshot.GetValueOrDefault((name, bucket));
        double hB = 0, rB = 0;
        if (bucketAgg != null && bucketAgg.Contributors.Count >= MinContributors)
        {
            hB = bucketAgg.WSeconds / 3600.0;
            rB = RateFromAggregate(task, bucketAgg, prices);
        }
        var rBucketStar = (hB * rB + BucketPseudoHours * mB * rGlobalStar) / (hB + BucketPseudoHours);

        // tier 1: personal, blended by the confidence ramp
        double personalMinutes = 0, rPersonal = 0;
        if (personal != null)
        {
            var ageDays = (DateTime.UtcNow - personal.LastFold).TotalDays;
            var decay = Math.Pow(0.5, ageDays / 7);
            personalMinutes = personal.CumulativeMinutes * decay;
            rPersonal = RateFromPlayerStat(task, personal, prices, decay);
        }
        var p = TaskPeriodFolder.Ramp(personalMinutes);
        var estimate = p * rPersonal + (1 - p) * rBucketStar;

        // saturation penalty, excluding the player themselves
        var doers = counts.GetValueOrDefault(name);
        var others = Math.Max(0, doers - (state?.ExtractedInfo?.CurrentTask == name ? 1 : 0));
        var saturation = Math.Max(Math.Pow(0.99, others), SaturationFloor);
        var displayed = estimate * saturation;

        var source = p > 0.5 ? "personal"
            : hB > 0 ? "stat_bucket"
            : hG > 0 ? "global"
            : "formula";

        span?.SetTag("bucket", bucket);
        span?.SetTag("r_personal", rPersonal);
        span?.SetTag("r_bucket", rBucketStar);
        span?.SetTag("r_global", rGlobalStar);
        span?.SetTag("r_formula", rFormula);
        span?.SetTag("h_bucket", hB);
        span?.SetTag("m_bucket", mB);
        span?.SetTag("ramp", p);
        span?.SetTag("contributors", globalContributors);
        span?.SetTag("doers", doers);
        span?.SetTag("saturation", saturation);
        span?.SetTag("source", source);

        return new TaskEstimate
        {
            TaskName = name,
            Category = signature.Category,
            Source = source,
            StatBucket = bucket,
            CoinsPerHour = displayed,
            RawCoinsPerHour = estimate,
            PersonalTrackedMinutes = personalMinutes,
            CommunityTrackedHours = hB > 0 ? hB : hG,
            Contributors = globalContributors,
            CurrentDoers = doers,
            DoersChange20m = deltas.GetValueOrDefault(name),
            Drops = BuildDrops(task, p > 0.5 && personal != null ? PlayerStatCounts(personal) : bucketAgg?.ItemCounts,
                bucketAgg?.WSeconds ?? personal?.WSeconds ?? 0, prices),
            TraceId = span?.TraceId.ToString() ?? System.Diagnostics.Activity.Current?.TraceId.ToString(),
            WikiUrl = task.WikiUrlForTest,
            Where = task.WhereForTest,
            Island = task.IslandForTest,
            WhereWikiUrl = task.WhereWikiUrlForTest,
            Warp = task.EffectiveWarpCommandForTest,
            Steps = task.StepsForTest
        };
    }

    private static Dictionary<string, double> PlayerStatCounts(TaskPlayerStatRow row) => row.ItemCounts ?? new();

    internal static double FormulaRate(MethodTask task, Dictionary<string, double> prices)
    {
        double total = 0;
        var dropRates = new Dictionary<string, double>();
        foreach (var drop in task.FormulaDropsForTest)
        {
            var price = prices?.GetValueOrDefault(drop.ItemTag) ?? 0;
            if (price > 0)
                total += drop.RatePerHour * price;
            // rates, not counts, but ScaledFormulaCost below is called with hours = 1 so its own
            // "count / hours" math reduces back to these same per-hour rates.
            dropRates[drop.ItemTag] = dropRates.GetValueOrDefault(drop.ItemTag) + drop.RatePerHour;
        }
        // Net out ingredient costs (e.g. the 16 fine gems per Gemstone Mixture) - otherwise the
        // formula baseline (used as the cold-start prior even once community data exists) counts
        // the full output price and silently drops what the recipe consumes to make it.
        var cost = task.ScaledFormulaCost(dropRates, 1.0, tag => prices?.GetValueOrDefault(tag) ?? 0);
        return total - cost;
    }

    /// <summary>
    /// Effects-seeded prior multiplier for a bucket: M(E) = product(1 + (m_j-1)*E),
    /// normalized across bucket midpoints so the three buckets differ even with no data.
    /// </summary>
    private double BucketPriorMultiplier(MethodTask task, byte bucket)
    {
        if (bucket == StatScoreService.UnknownBucket)
            return 1.0;
        var multipliers = task.EffectMultipliersForEstimate;
        if (multipliers.Count == 0)
            return 1.0;
        double[] midpoints = { 1.0 / 6, 0.5, 5.0 / 6 };
        double M(double e) => multipliers.Aggregate(1.0, (acc, m) => acc * (1 + (m - 1) * e));
        var values = midpoints.Select(M).ToArray();
        var mean = values.Average();
        if (mean <= 0)
            return 1.0;
        return values[bucket] / mean;
    }

    private BucketAggregate MergeBuckets(Dictionary<(string, byte), BucketAggregate> snapshot, string task)
    {
        BucketAggregate merged = null;
        for (byte b = 0; b < StatScoreService.BucketCount; b++)
        {
            if (!snapshot.TryGetValue((task, b), out var agg))
                continue;
            merged ??= new BucketAggregate { TaskName = task, Bucket = 255 };
            merged.WSeconds += agg.WSeconds;
            merged.ResidualCoins += agg.ResidualCoins;
            merged.RareCoins += agg.RareCoins;
            merged.WPeriods += agg.WPeriods;
            merged.RefItemValue += agg.RefItemValue;
            foreach (var (tag, count) in agg.ItemCounts)
                merged.ItemCounts[tag] = merged.ItemCounts.GetValueOrDefault(tag) + count;
            merged.Contributors.UnionWith(agg.Contributors);
        }
        return merged;
    }

    /// <summary>
    /// Rate from an aggregate: items re-priced live, ingredient cost netted for recipe tasks
    /// (e.g. Sludge Mining (Gem Mixture) consuming Fine gems via <see
    /// cref="MethodTask.FormulaCosts"/>/<see cref="MethodTask.ScaledFormulaCost"/>), coin pools
    /// scaled for price drift. Netting the cost here - rather than only in <see cref="FormulaRate"/>
    /// - is what makes the community/stat-bucket tiers net too: <see
    /// cref="TaskPeriodFolder.FoldDerivedTasks"/> stores this task's real, gross converted item
    /// counts (<see cref="BucketAggregate.ItemCounts"/>) plus a like-for-like GROSS <see
    /// cref="BucketAggregate.RefItemValue"/> drift reference, never a netted pool, so this is the
    /// only place community/stat-bucket data ever gets charged for the ingredients it consumed.
    /// </summary>
    internal static double RateFromAggregate(MethodTask task, BucketAggregate agg, Dictionary<string, double> prices)
    {
        if (agg == null || agg.WSeconds <= 0)
            return 0;
        var hours = agg.WSeconds / 3600.0;
        double liveItemValue = agg.ItemCounts.Sum(e => (prices?.GetValueOrDefault(e.Key) ?? 0) * e.Value);
        var cost = task?.ScaledFormulaCost(agg.ItemCounts, hours, tag => prices?.GetValueOrDefault(tag) ?? 0) ?? 0;
        var driftScale = agg.RefItemValue > 0 && liveItemValue > 0
            ? Math.Clamp(liveItemValue / agg.RefItemValue, PriceDriftClampLow, PriceDriftClampHigh)
            : 1;
        var pools = (agg.ResidualCoins + agg.RareCoins) * driftScale;
        return (liveItemValue - cost + pools) / hours;
    }

    /// <summary>
    /// Personal-stat mirror of <see cref="RateFromAggregate"/>: same live re-pricing, ingredient
    /// cost netting and price-drift scaling, applied to one player's decayed row instead of a
    /// shared bucket aggregate. Item counts are decayed the same as their coin value so the cost -
    /// computed from those same decayed counts - is discounted by the identical staleness factor.
    /// </summary>
    internal static double RateFromPlayerStat(MethodTask task, TaskPlayerStatRow row, Dictionary<string, double> prices, double decay)
    {
        var wSeconds = row.WSeconds * decay;
        if (wSeconds <= 0)
            return 0;
        var hours = wSeconds / 3600.0;
        var decayedCounts = (row.ItemCounts ?? new()).ToDictionary(e => e.Key, e => e.Value * decay);
        double liveItemValue = decayedCounts.Sum(e => (prices?.GetValueOrDefault(e.Key) ?? 0) * e.Value);
        var cost = task?.ScaledFormulaCost(decayedCounts, hours, tag => prices?.GetValueOrDefault(tag) ?? 0) ?? 0;
        var refValue = row.RefItemValue * decay;
        var driftScale = refValue > 0 && liveItemValue > 0
            ? Math.Clamp(liveItemValue / refValue, PriceDriftClampLow, PriceDriftClampHigh)
            : 1;
        var pools = (row.ResidualCoins + row.RareCoins) * decay * driftScale;
        return (liveItemValue - cost + pools) / hours;
    }

    private List<TaskDropRate> BuildDrops(MethodTask task, Dictionary<string, double> itemCounts, double wSeconds, Dictionary<string, double> prices)
    {
        var drops = new List<TaskDropRate>();
        // Cold-start (formula tier): no tracked item counts yet, so surface the task's
        // declared FormulaDrops as the breakdown instead of returning nothing.
        if (itemCounts == null || itemCounts.Count == 0 || wSeconds <= 0)
        {
            foreach (var drop in task.FormulaDropsForTest.OrderByDescending(d => d.RatePerHour * (prices?.GetValueOrDefault(d.ItemTag) ?? 0)).Take(12))
            {
                var price = prices?.GetValueOrDefault(drop.ItemTag) ?? 0;
                drops.Add(new TaskDropRate
                {
                    ItemTag = drop.ItemTag,
                    RatePerHour = drop.RatePerHour,
                    PriceEach = price,
                    ContributionPerHour = drop.RatePerHour * price
                });
            }
            return drops;
        }
        var hours = wSeconds / 3600.0;
        foreach (var (tag, count) in itemCounts.OrderByDescending(e => e.Value).Take(12))
        {
            var price = prices?.GetValueOrDefault(tag) ?? 0;
            var rate = count / hours;
            drops.Add(new TaskDropRate
            {
                ItemTag = tag,
                RatePerHour = rate,
                PriceEach = price,
                ContributionPerHour = rate * price
            });
        }
        return drops;
    }
}
