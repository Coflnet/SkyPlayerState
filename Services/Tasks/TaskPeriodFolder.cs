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
    private readonly Services.Gear.IGearObservationSink gearObservations;
    private readonly UndeclaredDropLog undeclaredLog;

    private const double RarePriceThreshold = 1_000_000;
    private const double RareAppearanceRatio = 0.05;
    private const double WinsorFactor = 4;
    private const double WinsorMinBucketHours = 2;

    public TaskPeriodFolder(TaskAggregateService aggregates, StatScoreService statScore, TaskRegistry registry,
        CoinValueRegistry coinValues, ITransactionService transactions, ILogger<TaskPeriodFolder> logger,
        Services.Gear.IGearObservationSink gearObservations = null)
    {
        this.gearObservations = gearObservations;
        this.aggregates = aggregates;
        this.statScore = statScore;
        this.registry = registry;
        this.coinValues = coinValues;
        this.transactions = transactions;
        this.logger = logger;
        undeclaredLog = new UndeclaredDropLog(logger);
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
    public Task Fold(Period period, StateObject state, Dictionary<string, double> prices)
        => FoldCore(period, state, prices, false);

    /// <summary>
    /// Fold a reward-only period the listener credited to the task that earned it (see
    /// <see cref="LateRewardAttribution"/>): its items add value to the task's aggregate and the player's
    /// stat, but no seconds - the work time was already folded with the work, and the hand-in's own few
    /// seconds must not count. The too-short gate, winsorizing and the AFK check are skipped: they
    /// compare a coin rate against a duration this period deliberately does not have.
    /// </summary>
    public Task FoldLateReward(Period period, StateObject state, Dictionary<string, double> prices)
        => FoldCore(period, state, prices, true);

    private async Task FoldCore(Period period, StateObject state, Dictionary<string, double> prices, bool lateReward)
    {
        using var span = TaskTelemetry.Source.StartActivity("task-period-fold");
        var task = period.DetectedTask;
        double metricMinutes = 0;
        try
        {
            span?.SetTag("task", task);
            span?.SetTag("player", period.PlayerUuid);
            if (task == null)
                return;
            var methodTask = registry.GetByName(task) as MethodTask;
            if (lateReward && methodTask == null)
            {
                // island trackers (Galatea fishing/diving) read the period list directly, there is no aggregate to fold into
                span?.SetTag("dropped", "not_a_method_task");
                FoldMetrics.Record(task, FoldMetrics.NotAMethodTask, 0, 0);
                return;
            }
            // one folded session/period = one unit of the task's minimum duration (claim tasks)
            var minutes = lateReward ? 0 : (methodTask?.EffectiveDuration(period) ?? period.EndTime - period.StartTime).TotalMinutes;
            metricMinutes = minutes;
            span?.SetTag("minutes", minutes);
            span?.SetTag("late_reward", lateReward);
            if (!lateReward && minutes < 2)
            {
                span?.SetTag("dropped", "too_short");
                await RecordWithoutOwned(task, FoldMetrics.TooShort, minutes * 60, period, methodTask, prices);
                return;
            }
            var factors = methodTask?.StatFactors ?? [];

            await coinValues.EnsureFresh();

            // subtract items received from other players / market during the window
            var owned = await SubtractExternalItems(period, span, methodTask);
            if (owned.Count == 0)
            {
                span?.SetTag("dropped", "no_owned_items");
                await RecordWithoutOwned(task, FoldMetrics.NoOwnedItems, minutes * 60, period, methodTask, prices);
                return;
            }

            var bucket = await statScore.GetBucket(factors, state);
            span?.SetTag("bucket", bucket);
            var snapshot = aggregates.GetSnapshot();
            var bucketAgg = snapshot.GetValueOrDefault((task, bucket));

            // a rare pool candidate the task does not declare is no evidence of its rate (bought books, foreign claims):
            // it goes to the undeclared pool, which only counts once enough players found the tag (see UndeclaredPool)
            var undeclared = UndeclaredExpensive(owned, methodTask, tag => coinValues.Value(tag, prices),
                tag => IsInfrequent(tag, bucketAgg));
            var promoted = UndeclaredPool.PromotedOf(snapshot, task).Where(undeclared.ContainsKey).ToHashSet();
            owned = WithoutUndeclared(owned, undeclared, promoted, task, period.PlayerUuid, span);
            var gated = new GatedPeriod(task, bucket, period, owned, undeclared, promoted, minutes, prices);
            if (gated.IsUnpromotedLeftover)
            {
                await KeepUndeclaredLeftover(gated, span);
                return;
            }
            // the player's own ledger keeps what they really earned, the gate only guards the task's rate
            var realizedExtra = gated.UndeclaredValue;
            var promotedCoins = gated.PromotedCoins;

            // value each item; separate rare, high value, infrequent drops into an EV pool
            var (commonCounts, rareCoins, itemValue, rareItemCoins, rareItemCounts) = SplitRareAndCommon(
                owned, tag => coinValues.Value(tag, prices), tag => IsInfrequent(tag, bucketAgg));

            // AFK contamination: gross gains far below the community rate, unless the session proves activity (see
            // AfkPolicy). Decided before winsorizing: the cap follows the net rate, so on a cost-heavy task it would
            // scale the best sessions below the gross AFK line
            var hours = minutes / 60.0;
            var isAfk = !lateReward && AfkPolicy.IsAfk(methodTask, bucketAgg, commonCounts, rareCoins + promotedCoins, hours, prices);

            // winsorize the common coin rate against the current bucket estimate
            double commonValue = commonCounts.Sum(c => coinValues.Value(c.Key, prices) * c.Value);
            var winsorScale = 1.0;
            if (!lateReward && bucketAgg != null && bucketAgg.WSeconds / 3600.0 >= WinsorMinBucketHours)
            {
                var bucketRate = EstimateBucketRate(methodTask, bucketAgg, prices);
                var cap = WinsorFactor * bucketRate * hours;
                if (cap > 0 && commonValue > cap)
                {
                    var scale = cap / commonValue;
                    winsorScale = scale;
                    foreach (var key in commonCounts.Keys.ToList())
                        commonCounts[key] *= scale;
                    span?.SetTag("winsorized_from", commonValue);
                    span?.SetTag("winsorized_to", cap);
                    FoldMetrics.Winsorized(task);
                }
            }

            var seconds = minutes * 60;
            var weight = Ramp(await GetCumulativeMinutes(period.PlayerUuid, task) + minutes);
            span?.SetTag("ramp_weight", weight);

            if (isAfk)
            {
                await KeepAfkPeriod(gated, itemValue, weight, span);
                return;
            }

            var weightedItemCounts = commonCounts.ToDictionary(c => c.Key, c => c.Value * weight);
            var residual = 0.0; // reserved for value not covered by tracked items (currently all items tracked)
            // two drift references: the whole item value (used today) and the market value of exactly the stored counts
            aggregates.AddContribution(task, bucket, period.PlayerUuid, seconds * weight, weightedItemCounts,
                residual * weight, rareCoins * weight, itemValue * weight,
                TaskEstimator.ItemValues(weightedItemCounts, prices), span?.TraceId.ToString(),
                rareItemCoins: RareRepricing.Scaled(rareItemCoins, weight),
                undeclaredItemCoins: WeightedValues(undeclared, weight),
                rareItemCounts: RareRepricing.Scaled(rareItemCounts, weight));

            // a promoted undeclared tag counts like a rare drop in the personal rate too (untraced, at face value), the
            // others stay out of it
            await UpdatePlayerStat(period.PlayerUuid, task, seconds, commonCounts, residual, rareCoins + promotedCoins,
                itemValue + promotedCoins, minutes, prices, new RareItems(rareItemCoins, rareItemCounts));

            if (!lateReward)
                gearObservations?.Record(new Services.Gear.GearFoldInput(period, task, bucket, seconds, weight,
                    commonValue * winsorScale, rareCoins + promotedCoins, itemValue + promotedCoins, state?.ExtractedInfo));

            if (!lateReward)
                AddZoneShares(task, period, weight, winsorScale);

            FoldMetrics.Record(task, lateReward ? FoldMetrics.LateReward : FoldMetrics.Folded, seconds, itemValue + promotedCoins);
            await RecordRealized(period.PlayerUuid, task, itemValue + realizedExtra);

            if (!lateReward)
                await FoldDerivedTasks(task, commonCounts, hours, minutes, seconds, weight, period.PlayerUuid, state, prices, span);
        }
        catch (Exception e)
        {
            if (task != null)
                FoldMetrics.Record(task, FoldMetrics.Error, metricMinutes * 60, 0);
            logger.LogError(e, "failed to fold period for task {task}", period.DetectedTask);
        }
    }

    /// <summary>
    /// Appends the period's realized coin value (items kept after external-item subtraction, net of costs,
    /// before ramp weighting or winsorizing, including what the declared drop gate kept out of the task's rate) to the per
    /// player ledger. Only called for the Folded, LateReward, UndeclaredLeftover and Afk outcomes: those are periods the
    /// player really earned the value in (an Afk period is not-played time for the rate, but what it earned was earned).
    /// TooShort, NoOwnedItems and NotAMethodTask carry no counted value. Derived
    /// tasks are never recorded, they re-express the primary's items and would double count. A ledger failure
    /// must not turn a successful fold into an Error outcome, so it is swallowed here.
    /// </summary>
    private async Task RecordRealized(string playerUuid, string task, double itemValue)
    {
        if (!double.IsFinite(itemValue))
            return;
        try
        {
            var key = Guid.TryParse(playerUuid, out var uuid) ? uuid.ToString("N") : playerUuid;
            await aggregates.AddRealized(key, task, DateTime.UtcNow, (long)Math.Round(itemValue));
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to record realized value for {player}/{task}", playerUuid, task);
        }
    }

    /// <summary>
    /// Adds the session's per zone time and coin value to the "best spot" aggregate. Only reached for a
    /// period the estimate itself folded (not too short, not AFK, not a late reward which has no time), with
    /// the same ramp weight and the winsor scale of the common pool, so the zone data stays consistent with it.
    /// </summary>
    private void AddZoneShares(string task, Period period, double weight, double winsorScale)
    {
        if (period.ZoneSeconds == null || weight <= 0)
            return;
        foreach (var (zone, zoneSeconds) in period.ZoneSeconds)
        {
            if (zoneSeconds <= 0)
                continue;
            var zoneValue = period.ZoneValue?.GetValueOrDefault(zone) ?? 0;
            // winsorizing only caps positive value; a negative (cost) share is never scaled
            aggregates.AddZoneContribution(task, zone, period.PlayerUuid, zoneSeconds * weight,
                (zoneValue > 0 ? zoneValue * winsorScale : zoneValue) * weight);
        }
    }

    /// <summary>
    /// Folds a contribution for every task that declares <see cref="MethodTask.DerivedFrom"/> ==
    /// <paramref name="primaryTask"/> (e.g. "Sludge Mining (Gem Mixture)" derives from "Sludge
    /// Mining"). Gemstone Mixture is a Forge recipe, not a Jungle drop, so it would otherwise never
    /// accumulate its own community/personal data - this converts the primary's observed item rate
    /// (already rare-separated and winsorized) through <see cref="MethodTask.ConvertDerivedCounts"/>
    /// so the derived task gets a real, non-formula estimate too. Reuses the primary's confidence
    /// ramp weight since it is the same physical activity.
    /// <para>
    /// Stores the GROSS converted value, both as the aggregate's item counts and as its per item
    /// price-drift reference (see <see cref="BucketAggregate.RefItemValue"/>) - ingredient cost (e.g. the 16
    /// Fine gems a Gemstone Mixture consumes, via <see cref="MethodTask.FormulaCosts"/>) is netted
    /// exactly once, downstream in <see cref="TaskEstimator.RateFromAggregate"/>/<see
    /// cref="TaskEstimator.RateFromPlayerStat"/>, from these same stored item counts. Keeping
    /// the reference gross (not net) keeps the price-drift ratio there (live gross / ref gross)
    /// like-for-like - a net reference compared against a live gross re-pricing would understate
    /// drift and wrongly clamp it.
    /// </para>
    /// </summary>
    private async Task FoldDerivedTasks(string primaryTask, Dictionary<string, double> primaryCommonCounts,
        double hours, double minutes, double seconds, double weight, string playerUuid, StateObject state,
        Dictionary<string, double> prices, Activity span)
    {
        foreach (var derivedTask in registry.MethodTasks.Where(t => t.DerivedFromForTest == primaryTask))
        {
            var derivedName = derivedTask.GetDetectionSignature().MethodName;
            try
            {
                var derivedCounts = derivedTask.ConvertDerivedCountsForTest(primaryCommonCounts, hours);
                if (derivedCounts == null || derivedCounts.Count == 0)
                    continue;

                var derivedBucket = await statScore.GetBucket(derivedTask.StatFactors, state);
                var derivedGrossValue = derivedCounts.Sum(c => coinValues.Value(c.Key, prices) * c.Value);
                var weightedDerivedCounts = derivedCounts.ToDictionary(c => c.Key, c => c.Value * weight);

                aggregates.AddContribution(derivedName, derivedBucket, playerUuid, seconds * weight, weightedDerivedCounts,
                    0, 0, derivedGrossValue * weight, TaskEstimator.ItemValues(weightedDerivedCounts, prices),
                    span?.TraceId.ToString());

                await UpdatePlayerStat(playerUuid, derivedName, seconds, derivedCounts, 0, 0, derivedGrossValue, minutes, prices);
                span?.SetTag($"derived_value_{derivedName}", derivedGrossValue);
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to fold derived task {derived} from primary {primary}", derivedName, primaryTask);
            }
        }
    }

    /// <summary>
    /// Values each owned item and separates it into the common pool (folded like a normal rate) or
    /// the rare/EV pool (a high value, infrequent drop, valued separately so one lucky drop doesn't
    /// blow out the winsorized common rate) - pulled out of <see cref="Fold"/> as a pure static so
    /// this split is unit testable without the Cassandra-backed dependencies the rest of Fold needs.
    /// The rare pool is also returned per tag (<see cref="PoolSplit.RareItemCoins"/>), so every rare coin stays traceable
    /// to the item that earned it, with its units (<see cref="PoolSplit.RareItemCounts"/>) for live re-pricing.
    /// <para>
    /// A negative <paramref name="owned"/> count is a real cost (see <see cref="FilterOwnedItems"/> -
    /// a <see cref="PseudoItems.IsCost"/> tag, or a FormulaCosts consumable like KISMET_FEATHER) - its
    /// value is negative and folds into <c>itemValue</c>/the common pool like anything else (so it
    /// reduces the period's coin value), but it must never be shunted into the rare/EV pool
    /// regardless of its unit price - a cost is not a "rare drop".
    /// </para>
    /// </summary>
    internal static PoolSplit SplitRareAndCommon(
        Dictionary<string, int> owned, Func<string, double> unitPriceOf, Func<string, bool> isInfrequent)
    {
        var split = new PoolSplit();
        foreach (var (tag, count) in owned)
        {
            var unit = unitPriceOf(tag);
            if (count > 0 && unit > RarePriceThreshold && isInfrequent(tag))
                split.AddRare(tag, count, unit * count);
            else
                split.AddCommon(tag, count, unit * count);
        }
        return split;
    }

    /// <summary>
    /// The pools of one period: common counts, the rare/EV coins (in total and per tag, with the units per tag) and the
    /// whole item value.
    /// </summary>
    internal sealed class PoolSplit
    {
        public Dictionary<string, double> CommonCounts { get; } = new();
        public Dictionary<string, double> RareItemCoins { get; } = new();
        public Dictionary<string, double> RareItemCounts { get; } = new();
        public double RareCoins { get; private set; }
        public double ItemValue { get; private set; }

        public void AddCommon(string tag, int count, double value)
        {
            CommonCounts[tag] = count;
            ItemValue += value;
        }

        public void AddRare(string tag, int count, double coins)
        {
            RareItemCoins[tag] = RareItemCoins.GetValueOrDefault(tag) + coins;
            RareItemCounts[tag] = RareItemCounts.GetValueOrDefault(tag) + count;
            RareCoins += coins;
            ItemValue += coins;
        }

        public void Deconstruct(out Dictionary<string, double> commonCounts, out double rareCoins, out double itemValue)
        {
            commonCounts = CommonCounts;
            rareCoins = RareCoins;
            itemValue = ItemValue;
        }

        public void Deconstruct(out Dictionary<string, double> commonCounts, out double rareCoins, out double itemValue,
            out Dictionary<string, double> rareItemCoins)
        {
            Deconstruct(out commonCounts, out rareCoins, out itemValue);
            rareItemCoins = RareItemCoins;
        }

        public void Deconstruct(out Dictionary<string, double> commonCounts, out double rareCoins, out double itemValue,
            out Dictionary<string, double> rareItemCoins, out Dictionary<string, double> rareItemCounts)
        {
            Deconstruct(out commonCounts, out rareCoins, out itemValue, out rareItemCoins);
            rareItemCounts = RareItemCounts;
        }
    }

    /// <summary>
    /// True when <paramref name="tag"/> makes up less than <see cref="RareAppearanceRatio"/> of the bucket's gains. Only
    /// positive counts are gains: a cost in the counts (DUNGEON_CHEST_COST -552k) made the total negative, and then every
    /// expensive item counted as infrequent.
    /// </summary>
    internal static bool IsInfrequent(string tag, BucketAggregate bucketAgg)
    {
        if (bucketAgg == null || bucketAgg.WPeriods <= 0)
            return true; // no history yet, treat expensive drops as rare EV
        // if the item barely shows up in the aggregate item counts it is infrequent
        var total = bucketAgg.ItemCounts.Values.Where(c => c > 0).Sum();
        if (total <= 0)
            return true;
        var share = Math.Max(0, bucketAgg.ItemCounts.GetValueOrDefault(tag)) / total;
        return share < RareAppearanceRatio;
    }

    /// <summary>
    /// The rare pool candidates (worth more than <see cref="RarePriceThreshold"/> a unit and <paramref name="isInfrequent"/>,
    /// see <see cref="SplitRareAndCommon"/>) that <paramref name="task"/> does not declare as a drop
    /// (<see cref="MethodTask.DeclaredDrops"/>), with their coin value. A frequent item is backed by the bucket's own counts
    /// and stays. The candidates are kept out of the common and rare pools and go to the undeclared pool instead (see
    /// <see cref="UndeclaredPool"/>), which counts a tag only once enough players found it: a rate must be
    /// backed by drop data, an unknown expensive item is far more likely bought, moved or another activity's loot (26 bazaar
    /// bought Bobbin' Time books made Helix Honey Lathering 484M coins/h). Hidden accounting tasks keep everything:
    /// absorbing such items is their job and they are never shown.
    /// </summary>
    internal static Dictionary<string, (int Count, double Value)> UndeclaredExpensive(Dictionary<string, int> owned,
        MethodTask task, Func<string, double> unitPriceOf, Func<string, bool> isInfrequent = null)
    {
        var result = new Dictionary<string, (int, double)>();
        if (task == null || task.IsHidden)
            return result;
        foreach (var (tag, count) in owned)
        {
            if (count <= 0 || PseudoItems.IsPseudo(tag) || task.DeclaresDrop(tag))
                continue;
            var unit = unitPriceOf(tag);
            if (unit > RarePriceThreshold && (isInfrequent?.Invoke(tag) ?? true))
                result[tag] = (count, unit * count);
        }
        return result;
    }

    /// <summary>
    /// True when what the declared drop gate left of a period holds a cost or no gain at all: a paid dungeon chest whose
    /// content was undeclared would otherwise fold as a pure cost and push the floor's rate down.
    /// </summary>
    internal static bool IsLeftoverWithoutLoot(Dictionary<string, int> owned)
        => owned.Values.Any(c => c < 0) || !owned.Values.Any(c => c > 0);

    /// <summary>
    /// A period after the declared drop gate: what is left of it (<see cref="Owned"/>), its undeclared expensive gains and
    /// which of them are promoted (see <see cref="UndeclaredPool"/>).
    /// </summary>
    private sealed record GatedPeriod(string Task, byte Bucket, Period Period, Dictionary<string, int> Owned,
        Dictionary<string, (int Count, double Value)> Undeclared, HashSet<string> Promoted, double Minutes,
        Dictionary<string, double> Prices)
    {
        public double UndeclaredValue => Undeclared.Values.Sum(e => e.Value);
        public double PromotedCoins => Undeclared.Where(e => Promoted.Contains(e.Key)).Sum(e => e.Value.Value);
        /// <summary>Gains were gated, none is promoted, and the rest holds a cost or nothing (see <see cref="IsLeftoverWithoutLoot"/>).</summary>
        public bool IsUnpromotedLeftover => Undeclared.Count > 0 && Promoted.Count == 0 && IsLeftoverWithoutLoot(Owned);
    }

    /// <summary>
    /// A period whose only gains went to the undeclared pool and that is left with a cost or nothing: its common/cost
    /// remainder is not folded into the rate (a paid chest would fold as a pure cost), but nothing is lost. The gated
    /// items go to the undeclared pool net of the remainder's cost (see <see cref="NetOfRemainder"/>) with the period's
    /// ramp weight, the player's ledger gets the whole value. Its seconds are not added anywhere: such periods are mostly
    /// claims a few seconds long, and the pool has no time of its own (the rate's hours come from folded periods).
    /// </summary>
    private async Task KeepUndeclaredLeftover(GatedPeriod gated, Activity span)
    {
        span?.SetTag("dropped", FoldMetrics.UndeclaredLeftover);
        FoldMetrics.Record(gated.Task, FoldMetrics.UndeclaredLeftover, gated.Minutes * 60, 0);
        logger.LogInformation("Did not fold a {task} period of {player}: its undeclared expensive gains went to the undeclared pool, leaving {leftover}",
            gated.Task, gated.Period.PlayerUuid, string.Join(", ", gated.Owned.Select(e => $"{e.Value}x {e.Key}")));
        var weight = Ramp(await GetCumulativeMinutes(gated.Period.PlayerUuid, gated.Task) + gated.Minutes);
        var remainder = gated.Owned.Sum(e => coinValues.Value(e.Key, gated.Prices) * e.Value);
        aggregates.AddUndeclared(gated.Task, gated.Bucket, gated.Period.PlayerUuid,
            NetOfRemainder(gated.Undeclared, remainder, weight));
        await RecordRealized(gated.Period.PlayerUuid, gated.Task, remainder + gated.UndeclaredValue);
    }

    /// <summary>
    /// An AFK period is not folded into the rate, but its undeclared gains still go to the pool (gross, ramp weighted)
    /// and its whole value to the player's ledger.
    /// </summary>
    private async Task KeepAfkPeriod(GatedPeriod gated, double itemValue, double weight, Activity span)
    {
        span?.SetTag("dropped", FoldMetrics.Afk);
        FoldMetrics.Record(gated.Task, FoldMetrics.Afk, gated.Minutes * 60, itemValue + gated.PromotedCoins);
        aggregates.AddUndeclared(gated.Task, gated.Bucket, gated.Period.PlayerUuid, WeightedValues(gated.Undeclared, weight));
        await RecordRealized(gated.Period.PlayerUuid, gated.Task, itemValue + gated.UndeclaredValue);
    }

    /// <summary>
    /// The gated gains of a leftover period, net of its <paramref name="remainder"/> value (the chest cost they came with,
    /// negative), shared over the tags by value and times <paramref name="weight"/>. Never above the gross gains (common
    /// gains of the remainder are not the tag's) and never below 0 (the pool holds no negative coins).
    /// </summary>
    internal static Dictionary<string, double> NetOfRemainder(Dictionary<string, (int Count, double Value)> undeclared,
        double remainder, double weight)
    {
        var gross = undeclared.Values.Sum(e => e.Value);
        var scale = gross > 0 ? Math.Clamp((gross + remainder) / gross, 0, 1) : 0;
        return undeclared.ToDictionary(e => e.Key, e => e.Value.Value * scale * weight);
    }

    private static Dictionary<string, double> WeightedValues(Dictionary<string, (int Count, double Value)> items, double weight)
        => items.ToDictionary(e => e.Key, e => e.Value.Value * weight);

    /// <summary>Removes the <paramref name="undeclared"/> items from <paramref name="owned"/> and logs them.</summary>
    private Dictionary<string, int> WithoutUndeclared(Dictionary<string, int> owned,
        Dictionary<string, (int Count, double Value)> undeclared, HashSet<string> promoted, string task, string playerUuid,
        Activity span)
    {
        if (undeclared.Count == 0)
            return owned;
        span?.SetTag("undeclared_excluded", string.Join(",", undeclared.Keys));
        if (promoted.Count > 0)
            span?.SetTag("undeclared_promoted", string.Join(",", promoted));
        foreach (var (tag, (count, value)) in undeclared)
            undeclaredLog.Record(task, tag, count, value, playerUuid, promoted.Contains(tag));
        return owned.Where(e => !undeclared.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);
    }

    /// <summary>
    /// Community rate used for winsorizing a fresh period, mirroring <see
    /// cref="TaskEstimator.RateFromAggregate"/>'s netting so a recipe task's own bucket (if it ever
    /// has <see cref="MethodTask.FormulaCosts"/> directly, not just via a derived task) is not
    /// compared against caps/floors computed from its gross value.
    /// </summary>
    private static double EstimateBucketRate(MethodTask task, BucketAggregate agg, Dictionary<string, double> prices)
    {
        if (agg == null || agg.WSeconds <= 0)
            return 0;
        var hours = agg.WSeconds / 3600.0;
        var itemValue = TaskEstimator.LiveValue(agg.ItemCounts, prices);
        var cost = task?.ScaledFormulaCost(agg.ItemCounts, hours, tag => TaskEstimator.UnitValue(tag, prices)) ?? 0;
        return (itemValue - cost + agg.ResidualCoins + agg.EffectiveRareCoins) / hours;
    }

    /// <summary>Records a dropped outcome for which the owned items were not (or not yet) computed, valuing the filtered raw items.</summary>
    private async Task RecordWithoutOwned(string task, string outcome, double seconds, Period period, MethodTask methodTask, Dictionary<string, double> prices)
    {
        double value = 0;
        try
        {
            await coinValues.EnsureFresh();
            value = FilterOwnedItems(period.ItemsCollected ?? new(), methodTask).Sum(e => coinValues.Value(e.Key, prices) * e.Value);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to value dropped period for {task}", task);
        }
        FoldMetrics.Record(task, outcome, seconds, value);
    }

    /// <summary>
    /// Remove items the player received via trade/bazaar/AH during the window so picking up other
    /// players' items does not inflate the estimate. A plain positive-count filter would also drop
    /// every real COST (a negative count - see PseudoItems.IsCost, e.g. DUNGEON_CHEST_COST, and a
    /// negative count of a tag the classified <paramref name="methodTask"/> declares in its own
    /// FormulaCosts, e.g. KISMET_FEATHER, an observed cost of every Catacombs floor) before it ever reaches the aggregates/
    /// player stats - those must survive so they reduce the folded coin value like any other real
    /// economic activity (see Fold's isRare guard and MethodTask.ComputeFromPlayerData's Costs split
    /// for how the rest of the pipeline keeps them out of the rare-drop pool and user-facing Drops).
    /// </summary>
    private async Task<Dictionary<string, int>> SubtractExternalItems(Period period, Activity span, MethodTask methodTask)
    {
        var result = FilterOwnedItems(period.ItemsCollected, methodTask);
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

    /// <summary>
    /// The positive-count-plus-real-costs filter <see cref="SubtractExternalItems"/> starts from,
    /// pulled out as a pure static so it is unit testable without the Cassandra-backed
    /// <see cref="ITransactionService"/> the rest of that method needs (see the doc comment there):
    /// a plain "keep only positive counts" filter would also drop every real COST before it ever
    /// reaches the aggregates/player stats - keep (a) any <see cref="PseudoItems.IsCost"/> tag
    /// regardless of sign (already negative by construction) and (b) a negative count of a real item
    /// tag <paramref name="methodTask"/> declares in its own <see cref="MethodTask.FormulaCostsForTest"/>
    /// or <see cref="MethodTask.ObservedCostItemsForTest"/> (e.g. KISMET_FEATHER on a Catacombs floor), or any negative count
    /// of a task whose consumed items are all costs (Garden Visitors hand-ins). Any other negative count is dropped.
    /// Some gains are not loot of the task either: a Forge-only output the task's formula does not list as a
    /// drop (<see cref="IsForeignForgeOutput"/>) or Crystal Nucleus loot outside that task (<see cref="IsForeignNucleusLoot"/>),
    /// a supply of the task (<see cref="MethodTask.IsSupplyTag"/>: Pots of Honeycomb bought for lathering, 8 Behemoth pots
    /// booked as 168M of loot) and an input it also drops but that was bought in the same period (<see cref="IsBoughtInput"/>).
    /// </summary>
    internal static Dictionary<string, int> FilterOwnedItems(Dictionary<string, int> itemsCollected, MethodTask methodTask)
    {
        var costTags = methodTask?.KeepableCostTags() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // purchase evidence (coins spent) is accounting of the purchase task, not an item the activity earned
        var dropPurchases = methodTask != null && !methodTask.GetDetectionSignature().IsPurchaseSink;
        var boughtSomething = dropPurchases && itemsCollected.Any(e => e.Value > 0 && PseudoItems.IsPurchase(e.Key));
        return new Dictionary<string, int>(itemsCollected
            .Where(e => !(dropPurchases && PseudoItems.IsPurchase(e.Key)))
            .Where(e => IsOwnedCount(e.Key, e.Value, methodTask, costTags, boughtSomething))
            .ToDictionary(e => e.Key, e => e.Value));
    }

    private static bool IsOwnedCount(string tag, int count, MethodTask task, HashSet<string> costTags, bool boughtSomething)
    {
        // a cost pseudo tag is a cost by construction (negative); a positive count of one is no loot
        if (PseudoItems.IsCost(tag))
            return count < 0;
        if (count <= 0)
            return task?.IsConsumedCost(tag, count, costTags) ?? false;
        if (task == null)
            return true;
        return !task.IsSupplyTag(tag, costTags) && !IsForeignForgeOutput(tag, task) && !IsForeignNucleusLoot(tag, task)
            && !IsBoughtInput(tag, task, costTags, boughtSomething);
    }

    /// <summary>
    /// A Forge-only output (<see cref="ForgeClaimsTask.Outputs"/>, e.g. a Mithril Plate or a Tungsten Key) in a task whose
    /// formula does not list it as a drop: it was claimed at the Forge or bought and booked into the session around it
    /// (production 2026-11-10: 78% of Mithril Mining's rare pool). The Forge Claims task lists none of them as drops,
    /// but it is hidden and only absorbs these periods, so it keeps them.
    /// </summary>
    internal static bool IsForeignForgeOutput(string tag, MethodTask task)
        => task is not ForgeClaimsTask && ForgeClaimsTask.Outputs.Contains(tag) && !task.ProducesItem(tag);

    /// <summary>
    /// Loot only the Crystal Nucleus loot bundle gives (<see cref="NucleusMiningTask.BundleOnlyLoot"/>) in another task: a
    /// claim at the nucleus takes a minute or two, below the classifier's 3 minute minimum, so its fragment merges into
    /// the next Crystal Hollows session and would add a 1.3M Divan Fragment to that task's rare pool.
    /// </summary>
    internal static bool IsForeignNucleusLoot(string tag, MethodTask task)
        => task is not NucleusMiningTask && NucleusMiningTask.BundleOnlyLoot.Contains(tag);

    /// <summary>
    /// A gain of an item the task consumes (its declared cost tags: Summoning Eyes at the dragon, keys in a mineshaft) in a
    /// period with purchase evidence, when the task does not regularly drop it (<see cref="MethodTask.DropsByFormula"/>):
    /// the input was bought to be used, the purchase discount (<see cref="PurchaseDiscount"/>) just did not catch it.
    /// A rare drop (<see cref="MethodTask.RareDrops"/>, corpse keys in a mineshaft) does not outweigh the purchase.
    /// </summary>
    private static bool IsBoughtInput(string tag, MethodTask task, HashSet<string> costTags, bool boughtSomething)
        => boughtSomething && costTags.Contains(tag) && !task.DropsByFormula(tag);

    private async Task<double> GetCumulativeMinutes(string playerUuid, string task)
    {
        try
        {
            var stat = await aggregates.GetPlayerStat(playerUuid, task);
            if (stat == null || TaskDataResets.IsBeforeReset(task, stat.LastFold))
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

    /// <summary>A period's rare pool per tag: fold time coins and their units (see <see cref="RareRepricing"/>).</summary>
    internal sealed record RareItems(Dictionary<string, double> Coins, Dictionary<string, double> Counts);

    private async Task UpdatePlayerStat(string playerUuid, string task, double seconds,
        Dictionary<string, double> commonCounts, double residual, double rareCoins, double itemValue,
        double minutes, Dictionary<string, double> prices, RareItems rareItems = null)
    {
        try
        {
            var existing = await aggregates.GetPlayerStat(playerUuid, task);
            if (existing != null && TaskDataResets.IsBeforeReset(task, existing.LastFold))
                existing = null; // stale data from before the reset: start fresh
            var row = existing ?? new TaskPlayerStatRow { PlayerUuid = playerUuid, TaskName = task };
            double decay = existing != null ? Math.Pow(0.5, (DateTime.UtcNow - existing.LastFold).TotalDays / 7) : 1;
            AddPlayerRareItems(row, rareItems, decay); // before the pool decays: the stored tags are bounded by it
            row.WSeconds = row.WSeconds * decay + seconds;
            row.ResidualCoins = row.ResidualCoins * decay + residual;
            row.RareCoins = row.RareCoins * decay + rareCoins;
            row.RefItemValue = row.RefItemValue * decay + itemValue;
            row.CumulativeMinutes = row.CumulativeMinutes * decay + minutes;
            AddPlayerItems(row, commonCounts, decay, prices, TrimRules.For(registry.GetByName(task) as MethodTask));
            row.LastFold = DateTime.UtcNow;
            await aggregates.UpsertPlayerStat(row);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to update player stat for {player}/{task}", playerUuid, task);
        }
    }

    /// <summary>Items kept per player row besides the costs.</summary>
    internal const int PlayerItemLimit = 20;

    /// <summary>
    /// Decays the row's rare tags and adds the period's (<paramref name="rareItems"/>), bounded like a day row
    /// (<see cref="TaskAggregateService.RareItemLimit"/>). Called while <see cref="TaskPlayerStatRow.RareCoins"/> is still
    /// the stored, undecayed pool: the stored tags are first bounded by it (<see cref="RareRepricing.BoundedBy"/>), older
    /// code decays the pool but not the tags. On a row from before the tags were stored the old rare coins stay untraced
    /// (the pool beyond its tags), at face value.
    /// </summary>
    internal static void AddPlayerRareItems(TaskPlayerStatRow row, RareItems rareItems, double decay)
    {
        var tags = RareTags.Empty();
        RareRepricing.AddStored(tags, RareRepricing.BoundedBy(RareTags.Of(row), row.RareCoins), decay);
        RareRepricing.AddFresh(tags, rareItems?.Coins, rareItems?.Counts);
        RareRepricing.Trimmed(tags, TaskAggregateService.RareItemLimit).WriteTo(row);
    }

    /// <summary>
    /// Decays the row's item counts and their fold time market values, adds the new counts at
    /// <paramref name="prices"/> and keeps the costs plus the most valuable items (see <see cref="TrimRules"/>).
    /// A row from before per item values existed gets its old counts valued at today's prices, i.e. no drift for them.
    /// </summary>
    internal static void AddPlayerItems(TaskPlayerStatRow row, Dictionary<string, double> commonCounts, double decay,
        Dictionary<string, double> prices, TrimRules rules = null)
    {
        rules ??= TrimRules.None;
        var stored = row.ItemCounts ?? new();
        var storedValues = TaskAggregateService.IsLegacyRef(stored, row.ItemRefValues)
            ? TaskEstimator.ItemValues(stored, prices)
            : row.ItemRefValues ?? new();
        var counts = stored.ToDictionary(e => e.Key, e => e.Value * decay);
        var values = stored.Keys.ToDictionary(tag => tag, tag => storedValues.GetValueOrDefault(tag) * decay);
        foreach (var (tag, count) in commonCounts)
        {
            counts[tag] = counts.GetValueOrDefault(tag) + count;
            values[tag] = values.GetValueOrDefault(tag) + TaskEstimator.LivePrice(tag, prices) * count;
        }
        if (counts.Count > PlayerItemLimit)
            counts = TaskAggregateService.KeepCostsAndTop(counts, PlayerItemLimit,
                rules.RankValue(counts, values), rules.KeepTagOf(counts));
        row.ItemCounts = counts;
        row.ItemRefValues = counts.Keys.ToDictionary(tag => tag, tag => values[tag]);
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

/// <summary>
/// Prometheus counters for what <see cref="TaskPeriodFolder"/> did with every classified period, so dropped
/// time can be seen without traces. Prometheus counters cannot decrease, so the coin value (which is negative
/// for cost-heavy periods) is split into a positive counter and a separate counter of the absolute negative value.
/// </summary>
public static class FoldMetrics
{
    public const string Folded = "folded";
    public const string LateReward = "late_reward";
    public const string TooShort = "too_short";
    public const string Afk = "afk";
    public const string NoOwnedItems = "no_owned_items";
    public const string NotAMethodTask = "not_a_method_task";
    /// <summary>
    /// The declared drop gate excluded gains (none promoted) and what is left holds a cost or nothing (see
    /// TaskPeriodFolder.IsLeftoverWithoutLoot): the gains go to the undeclared pool, the value to the realized ledger, the
    /// remainder is not folded.
    /// </summary>
    public const string UndeclaredLeftover = "undeclared_leftover";
    public const string Error = "error";

    private static readonly string[] Labels = ["task", "outcome"];
    private static readonly Prometheus.Counter Count = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_fold_total", "Classified periods by fold outcome", Labels);
    private static readonly Prometheus.Counter Seconds = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_fold_seconds_total", "Session seconds (unweighted) by fold outcome", Labels);
    private static readonly Prometheus.Counter Value = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_fold_value_total", "Positive owned item coin value by fold outcome", Labels);
    private static readonly Prometheus.Counter NegativeValue = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_fold_value_negative_total", "Absolute value of negative owned item coin value (costs) by fold outcome", Labels);
    private static readonly Prometheus.Counter WinsorizedCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_fold_winsorized_total", "Folded periods whose common value was capped by winsorizing", ["task"]);

    public static void Record(string task, string outcome, double seconds, double value)
    {
        Count.WithLabels(task, outcome).Inc();
        if (seconds > 0 && double.IsFinite(seconds))
            Seconds.WithLabels(task, outcome).Inc(seconds);
        if (!double.IsFinite(value))
            return;
        if (value > 0)
            Value.WithLabels(task, outcome).Inc(value);
        else if (value < 0)
            NegativeValue.WithLabels(task, outcome).Inc(-value);
    }

    public static void Winsorized(string task) => WinsorizedCount.WithLabels(task).Inc();

    internal static double CountOf(string task, string outcome) => Count.WithLabels(task, outcome).Value;
    internal static double SecondsOf(string task, string outcome) => Seconds.WithLabels(task, outcome).Value;
    internal static double ValueOf(string task, string outcome) => Value.WithLabels(task, outcome).Value;
    internal static double NegativeValueOf(string task, string outcome) => NegativeValue.WithLabels(task, outcome).Value;
    internal static double WinsorizedOf(string task) => WinsorizedCount.WithLabels(task).Value;
}
