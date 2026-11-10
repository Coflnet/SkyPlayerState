using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The task's gear score distribution over the eligible player-builds (tool and at least 3 armor pieces known, equipment
/// may be unknown). Each player has one vote in total, split evenly over their builds, so a player with many builds
/// cannot own the percentiles. Unrounded; see <see cref="GearModelPublisher"/> for the public form.
/// </summary>
public sealed class GearScoreDistribution
{
    public static readonly double[] PublishedPercentiles = [0.1, 0.25, 0.5, 0.75, 0.9];

    private readonly GearWeightedSample sample;

    public IReadOnlyList<GearPlayerBuild> Builds { get; }
    public int Players { get; }
    public double Hours { get; }
    /// <summary>Share of the builds without any known equipment piece.</summary>
    public double UnknownEquipmentShare { get; }

    private GearScoreDistribution(List<GearPlayerBuild> builds)
    {
        Builds = builds;
        var perPlayer = builds.GroupBy(b => b.Player, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        sample = new GearWeightedSample(builds.Select(b => (b.Score, 1.0 / perPlayer[b.Player])));
        Players = perPlayer.Count;
        Hours = builds.Sum(b => b.Hours);
        UnknownEquipmentShare = builds.Count == 0 ? 0 : (double)builds.Count(b => !b.EquipmentKnown) / builds.Count;
    }

    /// <summary>Null below k distinct players or the minimum hours.</summary>
    public static GearScoreDistribution? Of(IEnumerable<GearPlayerBuild> builds, GearModelOptions options)
    {
        var distribution = new GearScoreDistribution(builds.Where(b => b.Eligible).ToList());
        if (distribution.Players < options.MinPlayers || distribution.Hours < options.MinHours)
            return null;
        return distribution;
    }

    public double Percentile(double p) => sample.Percentile(p);

    /// <summary>Where a score lies in the distribution, 0 .. 100.</summary>
    public double RankOf(double score) => sample.Rank(score);
}

/// <summary>One player of the late game cohort: their builds at or above the cohort's threshold, pooled.</summary>
public sealed record GearCohortMember(string Player, IReadOnlyList<GearPlayerBuild> Builds)
{
    public double Hours => Builds.Sum(b => b.Hours);
    public double Rare => Builds.Sum(b => b.Rare);
    /// <summary>Hours weighted over the builds: all common coins / all hours.</summary>
    public double CommonPerHour => Builds.Sum(b => b.Common) / Hours;
    public double TotalPerHour => (Builds.Sum(b => b.Common) + Rare) / Hours;
    public IEnumerable<GearObservationInput> Observations => Builds.SelectMany(b => b.Members);
}

/// <summary>The late game cohort: the players with a build at or above <see cref="ScoreFrom"/>, unrounded.</summary>
public sealed record GearCohort(double ScoreFrom, bool Widened, IReadOnlyList<GearCohortMember> Members)
{
    public int Players => Members.Count;
    public double Hours => Members.Sum(m => m.Hours);
}

/// <summary>
/// Selects the late game cohort (pure): the player-builds with a score at or above the P90 of the distribution, widened
/// downward by score (one build score at a time, a single descending sweep) until they hold
/// <see cref="GearModelOptions.LateGameMinPlayers"/> distinct players and <see cref="GearModelOptions.LateGameMinHours"/>,
/// but never below the P75: if that is not enough there is no late game cohort. A build needs
/// <see cref="MinBuildHours"/> to count. A player counts once: their counting builds at or above the threshold are pooled.
/// </summary>
public static class GearLateGameCohort
{
    /// <summary>10 minutes.</summary>
    public const double MinBuildHours = 10.0 / 60;

    private sealed class Sweep
    {
        private readonly List<GearPlayerBuild> sorted;
        private readonly HashSet<string> players = new(StringComparer.Ordinal);
        private double hours;

        public int Taken { get; private set; }
        public double NextScore => sorted[Taken].Score;
        public bool HasNext => Taken < sorted.Count;

        public Sweep(List<GearPlayerBuild> sorted) => this.sorted = sorted;

        /// <summary>Takes every remaining build with a score of at least <paramref name="threshold"/>.</summary>
        public void TakeFrom(double threshold)
        {
            while (HasNext && NextScore >= threshold)
            {
                players.Add(sorted[Taken].Player);
                hours += sorted[Taken].Hours;
                Taken++;
            }
        }

        public bool Qualifies(GearModelOptions options) => players.Count >= options.LateGameMinPlayers && hours >= options.LateGameMinHours;

        public GearCohort Cohort(double threshold, bool widened) => new(threshold, widened, sorted.Take(Taken)
            .GroupBy(b => b.Player, StringComparer.Ordinal)
            .Select(g => new GearCohortMember(g.Key, g.ToList()))
            .ToList());
    }

    public static GearCohort? Select(GearScoreDistribution distribution, GearModelOptions options)
    {
        var start = distribution.Percentile(options.LateGamePercentile);
        var floor = distribution.Percentile(options.LateGameFloorPercentile);
        var sweep = new Sweep(distribution.Builds.Where(b => b.Hours >= MinBuildHours - 1e-9).OrderByDescending(b => b.Score).ToList());
        sweep.TakeFrom(start);
        if (sweep.Qualifies(options))
            return sweep.Cohort(start, false);
        while (sweep.HasNext && sweep.NextScore >= floor)
        {
            var threshold = sweep.NextScore;
            sweep.TakeFrom(threshold);
            if (sweep.Qualifies(options))
                return sweep.Cohort(threshold, true);
        }
        return null;
    }
}

/// <summary>
/// What a typical player of the task earns (unrounded): the median over the distribution's players of each player's
/// common coins per hour (pooled over their eligible builds) and the rare coins pooled over all eligible builds (rare
/// drops are too lumpy for a per-player median), each player's capped (<see cref="GearRareRate"/>).
/// </summary>
public sealed record GearRatePrior(double Common, double Rare)
{
    public double Total => Common + Rare;

    public static GearRatePrior Of(GearScoreDistribution distribution, GearModelOptions options)
        => Of(distribution, GearRareRate.Of(distribution.Builds, options));

    public static GearRatePrior Of(GearScoreDistribution distribution, GearRareRate rare)
    {
        var perPlayer = distribution.Builds.GroupBy(b => b.Player, StringComparer.Ordinal)
            .Select(g => g.Sum(b => b.Common) / g.Sum(b => b.Hours))
            .ToList();
        return new GearRatePrior(GearStatistics.Median(perPlayer), rare.PerHour(distribution.Builds));
    }
}

/// <summary>A late game rate after <see cref="GearLateGamePrior.Shrink"/>, unrounded.</summary>
public sealed record GearShrunkRate(double Common, double Rare, bool Floored);

/// <summary>
/// What the late game rate is shrunk toward (<paramref name="Target"/>) and never published below (<paramref name="Floor"/>,
/// the all-player <see cref="GearRatePrior"/>). With a curve the target's common rate is the curve at the cohort's median
/// score (its rare rate the all-player one): the all-player median as target pulled the endgame below the curve-read budget
/// and mid builds, which then were not shown. Without a curve the target is the floor. <see cref="RareRate"/> is the task's
/// rare cap and <see cref="PooledCommon"/> its pooled common rate (both read by <see cref="Of"/>; a prior built with the
/// constructor is uncapped and has no pooled common rate, so its <see cref="RareScale"/> is 1).
/// </summary>
public sealed record GearLateGamePrior(GearRatePrior Target, GearRatePrior Floor)
{
    public GearRareRate RareRate { get; init; } = GearRareRate.Uncapped;
    /// <summary>All common coins / all hours of the distribution's builds: pooled like <see cref="GearRatePrior.Rare"/>.</summary>
    public double PooledCommon { get; init; }

    public static GearLateGamePrior Of(GearScoreDistribution distribution, GearCohort? cohort, GearCurveModel? curve, GearModelOptions options)
        => Of(distribution, cohort, curve, GearRareRate.Of(distribution.Builds, options));

    /// <summary>The prior with the task's rare cap already read (<see cref="GearRareRate.Of"/>).</summary>
    public static GearLateGamePrior Of(GearScoreDistribution distribution, GearCohort? cohort, GearCurveModel? curve, GearRareRate rareRate)
    {
        var floor = GearRatePrior.Of(distribution, rareRate);
        var target = curve == null || cohort == null ? floor : new GearRatePrior(curve.PredictCommon(MedianScore(cohort)), floor.Rare);
        var pooledCommon = distribution.Hours > 0 ? distribution.Builds.Sum(b => b.Common) / distribution.Hours : 0;
        return new GearLateGamePrior(target, floor) { RareRate = rareRate, PooledCommon = pooledCommon };
    }

    /// <summary>
    /// The cohort's rare rate. By default the task's capped rare rate (<see cref="Floor"/>) scaled by the cohort's common
    /// rate over the task's (<see cref="RareScale"/>): the cohort's own pooled drops, capped at the task's per-player cap,
    /// still let one lucky drop through when short lucky sessions set the cap (a 300M F7 drop published about 31M/h at a
    /// weight of 0.8, review 2026-11-10). With <see cref="GearModelOptions.LateGameRareFromTask"/> off: the cohort's rare
    /// coins, each player's capped by <see cref="RareRate"/>, over its hours.
    /// </summary>
    public double CohortRare(GearCohort cohort, double cohortCommon, GearModelOptions options)
    {
        if (!options.LateGameRareFromTask)
            return RareRate.PerHour(cohort.Members.SelectMany(m => m.Builds));
        return Floor.Rare * RareScale(cohortCommon, options);
    }

    /// <summary>
    /// The cohort's common rate over <see cref="PooledCommon"/>, clamped to 1 .. <see cref="GearModelOptions.CurveMaxMedianMultiple"/>
    /// (the curve cap's multiple, reused). Both rates are pooled over hours, like the rare rate it scales: against the
    /// per-player median a cohort holding most of the hours counted its advantage twice (8400 rare/h published for a
    /// cohort earning 3000, review 2026-11-10). 1 without a positive pooled rate or with a non finite ratio.
    /// </summary>
    public double RareScale(double cohortCommon, GearModelOptions options)
    {
        var ratio = cohortCommon / PooledCommon;
        if (!(PooledCommon > 0) || !double.IsFinite(ratio))
            return 1;
        return Math.Clamp(ratio, 1, Math.Max(1, options.CurveMaxMedianMultiple));
    }

    /// <summary>The median over the cohort's players of each player's hours weighted score.</summary>
    public static double MedianScore(GearCohort cohort)
        => GearStatistics.Median(cohort.Members.Select(m => m.Builds.Sum(b => b.Score * b.Hours) / m.Hours).ToList());

    /// <summary>
    /// The cohort's weight: hours / (hours + <see cref="GearModelOptions.LateGamePriorHours"/>); 1 (no shrinkage) when
    /// that is 0.
    /// </summary>
    public static double Weight(double hours, GearModelOptions options)
        => options.LateGamePriorHours <= 0 ? 1 : hours / (hours + options.LateGamePriorHours);

    /// <summary>
    /// Each part is w × cohort + (1 − w) × target. With <see cref="GearModelOptions.LateGameFloorAtPrior"/> a total below
    /// the floor's is the floor: the players with the best gear are never published as earning less than the typical player.
    /// </summary>
    public GearShrunkRate Shrink(double common, double rare, double hours, GearModelOptions options)
    {
        var weight = Weight(hours, options);
        var shrunkCommon = weight * common + (1 - weight) * Target.Common;
        var shrunkRare = weight * rare + (1 - weight) * Target.Rare;
        if (options.LateGameFloorAtPrior && shrunkCommon + shrunkRare < Floor.Total)
            return new GearShrunkRate(Floor.Common, Floor.Rare, true);
        return new GearShrunkRate(shrunkCommon, shrunkRare, false);
    }

    /// <summary>A common rate (a percentile of the cohort) shrunk with the same weight and target.</summary>
    public double ShrinkCommon(double common, double hours, GearModelOptions options)
    {
        var weight = Weight(hours, options);
        return weight * common + (1 - weight) * Target.Common;
    }
}
