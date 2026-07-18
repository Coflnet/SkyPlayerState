using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Folds a classified period into the per player stats and the shared stat-bucket
/// aggregates. Applies the confidence ramp, AFK and short window filtering,
/// rare drop separation, winsorizing, and subtraction of externally acquired items.
/// </summary>
public class TaskPeriodFolder
{
    private readonly TaskAggregateService aggregates;
    private readonly StatScoreService statScore;
    private readonly TaskRegistry registry;
    private readonly CoinValueRegistry coinValues;
    private readonly ITransactionService transactions;
    private readonly ILogger<TaskPeriodFolder> logger;

    private const double RarePriceThreshold = 1_000_000;
    private const double RareAppearanceRatio = 0.05;
    private const double WinsorFactor = 4;
    private const double WinsorMinBucketHours = 2;

    public TaskPeriodFolder(TaskAggregateService aggregates, StatScoreService statScore, TaskRegistry registry,
        CoinValueRegistry coinValues, ITransactionService transactions, ILogger<TaskPeriodFolder> logger)
    {
        this.aggregates = aggregates;
        this.statScore = statScore;
        this.registry = registry;
        this.coinValues = coinValues;
        this.transactions = transactions;
        this.logger = logger;
    }

    /// <summary>
    /// Confidence ramp: 0 below 2 minutes, 0.5 at 5 minutes, 1.0 at 30 minutes.
    /// </summary>
    public static double Ramp(double minutes)
    {
        if (minutes < 2) return 0;
        if (minutes <= 5) return minutes * 0.1;
        return Math.Min(1.0, 0.5 + (minutes - 5) * 0.02);
    }

    /// <summary>
    /// Fold a flushed, already classified period. No-op when unclassified or filtered out.
    /// </summary>
    public async Task Fold(Period period, StateObject state, Dictionary<string, double> prices)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-period-fold");
        try
        {
            var task = period.DetectedTask;
            span?.SetTag("task", task);
            span?.SetTag("player", period.PlayerUuid);
            if (task == null)
                return;
            var minutes = (period.EndTime - period.StartTime).TotalMinutes;
            span?.SetTag("minutes", minutes);
            if (minutes < 2)
            {
                span?.SetTag("dropped", "too_short");
                return;
            }
            var methodTask = registry.GetByName(task) as MethodTask;
            var factors = methodTask?.StatFactors ?? [];

            await coinValues.EnsureFresh();

            // subtract items received from other players / market during the window
            var owned = await SubtractExternalItems(period, span);
            if (owned.Count == 0)
            {
                span?.SetTag("dropped", "no_owned_items");
                return;
            }

            var bucket = await statScore.GetBucket(factors, state);
            span?.SetTag("bucket", bucket);

            // value each item; separate rare, high value, infrequent drops into an EV pool
            var bucketAgg = aggregates.GetSnapshot().GetValueOrDefault((task, bucket));
            var commonCounts = new Dictionary<string, double>();
            double rareCoins = 0, itemValue = 0;
            foreach (var (tag, count) in owned)
            {
                var unit = coinValues.Value(tag, prices);
                var total = unit * count;
                itemValue += total;
                bool isRare = unit > RarePriceThreshold && IsInfrequent(tag, bucketAgg);
                if (isRare)
                    rareCoins += total;
                else
                    commonCounts[tag] = count;
            }

            // winsorize the common coin rate against the current bucket estimate
            var hours = minutes / 60.0;
            double commonValue = commonCounts.Sum(c => coinValues.Value(c.Key, prices) * c.Value);
            if (bucketAgg != null && bucketAgg.WSeconds / 3600.0 >= WinsorMinBucketHours)
            {
                var bucketRate = EstimateBucketRate(bucketAgg, prices);
                var cap = WinsorFactor * bucketRate * hours;
                if (commonValue > cap && commonValue > 0)
                {
                    var scale = cap / commonValue;
                    foreach (var key in commonCounts.Keys.ToList())
                        commonCounts[key] *= scale;
                    span?.SetTag("winsorized_from", commonValue);
                    span?.SetTag("winsorized_to", cap);
                }
            }

            // AFK contamination: coin rate far below the community rate for the task
            if (bucketAgg != null && bucketAgg.WSeconds / 3600.0 >= WinsorMinBucketHours)
            {
                var bucketRate = EstimateBucketRate(bucketAgg, prices);
                var thisRate = (commonValue + rareCoins) / hours;
                if (bucketRate > 0 && thisRate < 0.05 * bucketRate)
                {
                    span?.SetTag("dropped", "afk");
                    return;
                }
            }

            var seconds = minutes * 60;
            var weight = Ramp(await GetCumulativeMinutes(period.PlayerUuid, task) + minutes);
            span?.SetTag("ramp_weight", weight);

            var weightedItemCounts = commonCounts.ToDictionary(c => c.Key, c => c.Value * weight);
            var residual = 0.0; // reserved for value not covered by tracked items (currently all items tracked)
            aggregates.AddContribution(task, bucket, period.PlayerUuid, seconds * weight,
                weightedItemCounts, residual * weight, rareCoins * weight, itemValue * weight, span?.TraceId.ToString());

            await UpdatePlayerStat(period.PlayerUuid, task, seconds, commonCounts, residual, rareCoins, itemValue, minutes, prices);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to fold period for task {task}", period.DetectedTask);
        }
    }

    private static bool IsInfrequent(string tag, BucketAggregate bucketAgg)
    {
        if (bucketAgg == null || bucketAgg.WPeriods <= 0)
            return true; // no history yet, treat expensive drops as rare EV
        // if the item barely shows up in the aggregate item counts it is infrequent
        var total = bucketAgg.ItemCounts.Values.Sum();
        if (total <= 0)
            return true;
        var share = bucketAgg.ItemCounts.GetValueOrDefault(tag) / total;
        return share < RareAppearanceRatio;
    }

    private static double EstimateBucketRate(BucketAggregate agg, Dictionary<string, double> prices)
    {
        if (agg == null || agg.WSeconds <= 0)
            return 0;
        double itemValue = agg.ItemCounts.Sum(e => (prices?.GetValueOrDefault(e.Key) ?? 0) * e.Value);
        return (itemValue + agg.ResidualCoins + agg.RareCoins) / (agg.WSeconds / 3600.0);
    }

    /// <summary>
    /// Remove items the player received via trade/bazaar/AH during the window so
    /// picking up other players' items does not inflate the estimate.
    /// </summary>
    private async Task<Dictionary<string, int>> SubtractExternalItems(Period period, Activity span)
    {
        var result = new Dictionary<string, int>(period.ItemsCollected.Where(e => e.Value > 0)
            .ToDictionary(e => e.Key, e => e.Value));
        try
        {
            if (!Guid.TryParse(period.PlayerUuid, out var uuid))
                return result;
            var window = period.EndTime - period.StartTime;
            var txns = await transactions.GetTransactions(uuid, window, period.EndTime);
            var received = txns.Where(t =>
                t.Type.HasFlag(Transaction.TransactionType.RECEIVE) &&
                (t.Type.HasFlag(Transaction.TransactionType.TRADE)
                 || t.Type.HasFlag(Transaction.TransactionType.BAZAAR)
                 || t.Type.HasFlag(Transaction.TransactionType.AH)));
            var subtracted = 0;
            // transactions are keyed by numeric item id; only usable when the collected
            // map also carries that id. Best effort: match by item id string form.
            foreach (var t in received)
            {
                var key = t.ItemId.ToString();
                if (result.TryGetValue(key, out var have))
                {
                    var newVal = Math.Max(0, have - (int)t.Amount);
                    if (newVal == 0) result.Remove(key); else result[key] = newVal;
                    subtracted++;
                }
            }
            if (subtracted > 0)
                span?.SetTag("external_subtracted", subtracted);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to subtract external items for {player}", period.PlayerUuid);
        }
        return result;
    }

    private async Task<double> GetCumulativeMinutes(string playerUuid, string task)
    {
        try
        {
            var stat = await aggregates.GetPlayerStat(playerUuid, task);
            if (stat == null)
                return 0;
            // decay the stored cumulative minutes toward now (7 day half life)
            var ageDays = (DateTime.UtcNow - stat.LastFold).TotalDays;
            return stat.CumulativeMinutes * Math.Pow(0.5, ageDays / 7);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to load cumulative minutes for {player}/{task}", playerUuid, task);
            return 0;
        }
    }

    private async Task UpdatePlayerStat(string playerUuid, string task, double seconds,
        Dictionary<string, double> commonCounts, double residual, double rareCoins, double itemValue,
        double minutes, Dictionary<string, double> prices)
    {
        try
        {
            var existing = await aggregates.GetPlayerStat(playerUuid, task);
            var row = existing ?? new TaskPlayerStatRow { PlayerUuid = playerUuid, TaskName = task };
            double decay = existing != null ? Math.Pow(0.5, (DateTime.UtcNow - existing.LastFold).TotalDays / 7) : 1;
            row.WSeconds = row.WSeconds * decay + seconds;
            row.ResidualCoins = row.ResidualCoins * decay + residual;
            row.RareCoins = row.RareCoins * decay + rareCoins;
            row.RefItemValue = row.RefItemValue * decay + itemValue;
            row.CumulativeMinutes = row.CumulativeMinutes * decay + minutes;
            var counts = row.ItemCounts ?? new();
            foreach (var key in counts.Keys.ToList())
                counts[key] *= decay;
            foreach (var (tag, count) in commonCounts)
                counts[tag] = counts.GetValueOrDefault(tag) + count;
            if (counts.Count > 8)
                counts = counts.OrderByDescending(e => e.Value).Take(8).ToDictionary(e => e.Key, e => e.Value);
            row.ItemCounts = counts;
            row.LastFold = DateTime.UtcNow;
            await aggregates.UpsertPlayerStat(row);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to update player stat for {player}/{task}", playerUuid, task);
        }
    }
}

/// <summary>
/// Activity source for task estimation spans. The exporter is subscribed to
/// <see cref="SourceName"/> in Startup so fold and estimate spans reach jaeger.
/// </summary>
public static class TaskTelemetry
{
    public const string SourceName = "sky-player-state";
    public static readonly ActivitySource Source = new(SourceName);
}
