using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// A single task's estimated coins per hour for one player, with the inputs that produced it.
/// </summary>
public class TaskEstimate
{
    public string TaskName { get; set; }
    public string Category { get; set; }
    public string Source { get; set; }         // personal | stat_bucket | global | formula
    public byte StatBucket { get; set; }
    public double CoinsPerHour { get; set; }   // after saturation penalty
    public double RawCoinsPerHour { get; set; }// before saturation penalty
    public double PersonalTrackedMinutes { get; set; }
    public double CommunityTrackedHours { get; set; }
    public int Contributors { get; set; }
    public int CurrentDoers { get; set; }
    public int DoersChange20m { get; set; }
    public List<TaskDropRate> Drops { get; set; } = new();
    public string TraceId { get; set; }
}

public class TaskDropRate
{
    public string ItemTag { get; set; }
    public double RatePerHour { get; set; }
    public double PriceEach { get; set; }
    public double ContributionPerHour { get; set; }
}

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
    public async Task<List<TaskEstimate>> EstimateAll(StateObject state, Dictionary<string, double> prices)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-estimate-all");
        await coinValues.EnsureFresh();
        var snapshot = aggregates.GetSnapshot();
        var counts = await activityService.GetCounts();
        var deltas = await activityService.GetChange20m();
        var playerUuid = state?.McInfo?.Uuid.ToString("N");
        var playerStats = playerUuid != null
            ? (await aggregates.GetPlayerStats(playerUuid)).ToDictionary(s => s.TaskName, s => s, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TaskPlayerStatRow>();

        var results = new List<TaskEstimate>();
        foreach (var task in registry.MethodTasks)
        {
            try
            {
                results.Add(await EstimateOne(task, state, prices, snapshot, counts, deltas,
                    playerStats.GetValueOrDefault(task.Name)));
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to estimate task {task}", task.Name);
            }
        }
        span?.SetTag("tasks", results.Count);
        return results;
    }

    private async Task<TaskEstimate> EstimateOne(MethodTask task, StateObject state, Dictionary<string, double> prices,
        Dictionary<(string, byte), BucketAggregate> snapshot, Dictionary<string, int> counts,
        Dictionary<string, int> deltas, TaskPlayerStatRow personal)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-estimate");
        var name = task.Name;
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
            rG = RateFromAggregate(globalAgg, prices);
        }
        var rGlobalStar = (hG * rG + GlobalPseudoHours * rFormula) / (hG + GlobalPseudoHours);

        // tier 2b: stat bucket, shrunk toward the stat-adjusted global
        var bucketAgg = snapshot.GetValueOrDefault((name, bucket));
        double hB = 0, rB = 0;
        if (bucketAgg != null && bucketAgg.Contributors.Count >= MinContributors)
        {
            hB = bucketAgg.WSeconds / 3600.0;
            rB = RateFromAggregate(bucketAgg, prices);
        }
        var rBucketStar = (hB * rB + BucketPseudoHours * mB * rGlobalStar) / (hB + BucketPseudoHours);

        // tier 1: personal, blended by the confidence ramp
        double personalMinutes = 0, rPersonal = 0;
        if (personal != null)
        {
            var ageDays = (DateTime.UtcNow - personal.LastFold).TotalDays;
            var decay = Math.Pow(0.5, ageDays / 7);
            personalMinutes = personal.CumulativeMinutes * decay;
            rPersonal = RateFromPlayerStat(personal, prices, decay);
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
            Category = task.GetDetectionSignature().Category,
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
            TraceId = span?.TraceId.ToString() ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        };
    }

    private static Dictionary<string, double> PlayerStatCounts(TaskPlayerStatRow row) => row.ItemCounts ?? new();

    private double FormulaRate(MethodTask task, Dictionary<string, double> prices)
    {
        double total = 0;
        foreach (var drop in task.FormulaDropsForTest)
        {
            var price = prices?.GetValueOrDefault(drop.ItemTag) ?? 0;
            if (price > 0)
                total += drop.RatePerHour * price;
        }
        return total;
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
    /// Rate from an aggregate: items re-priced live, coin pools scaled for price drift.
    /// </summary>
    private double RateFromAggregate(BucketAggregate agg, Dictionary<string, double> prices)
    {
        if (agg == null || agg.WSeconds <= 0)
            return 0;
        double liveItemValue = agg.ItemCounts.Sum(e => (prices?.GetValueOrDefault(e.Key) ?? 0) * e.Value);
        var driftScale = agg.RefItemValue > 0 && liveItemValue > 0
            ? Math.Clamp(liveItemValue / agg.RefItemValue, PriceDriftClampLow, PriceDriftClampHigh)
            : 1;
        var pools = (agg.ResidualCoins + agg.RareCoins) * driftScale;
        return (liveItemValue + pools) / (agg.WSeconds / 3600.0);
    }

    private double RateFromPlayerStat(TaskPlayerStatRow row, Dictionary<string, double> prices, double decay)
    {
        var wSeconds = row.WSeconds * decay;
        if (wSeconds <= 0)
            return 0;
        double liveItemValue = (row.ItemCounts ?? new()).Sum(e => (prices?.GetValueOrDefault(e.Key) ?? 0) * e.Value * decay);
        var refValue = row.RefItemValue * decay;
        var driftScale = refValue > 0 && liveItemValue > 0
            ? Math.Clamp(liveItemValue / refValue, PriceDriftClampLow, PriceDriftClampHigh)
            : 1;
        var pools = (row.ResidualCoins + row.RareCoins) * decay * driftScale;
        return (liveItemValue + pools) / (wSeconds / 3600.0);
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
