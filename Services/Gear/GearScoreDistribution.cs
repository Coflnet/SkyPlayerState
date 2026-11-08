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
/// downward by score (one build score at a time, a single descending sweep) until they hold k distinct players and the
/// minimum hours, but never below the P75: if that is not enough there is no late game cohort. A build needs
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

        public bool Qualifies(GearModelOptions options) => players.Count >= options.MinPlayers && hours >= options.MinHours;

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
