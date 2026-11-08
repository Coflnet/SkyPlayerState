using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>A fitted ln(common rate) = Intercept + Slope × score, unrounded.</summary>
public sealed record GearCurveModel(double Intercept, double Slope, double ScoreMin, double ScoreMax, int Players, double Hours, string Method)
{
    public const string TheilSen = "theil-sen";
    public const string Binned = "binned-theil-sen";
    /// <summary>Exponents are capped here so a prediction never overflows to infinity.</summary>
    private const double MaxExponent = 700;

    public double PredictCommon(double score) => Math.Exp(Math.Min(MaxExponent, Intercept + Slope * score));
}

/// <summary>
/// The rate curve of a task (pure): ln(common coins/hour) against the gear score, fit robustly with a weighted Theil–Sen
/// estimator (the weighted median of the pairwise slopes; the intercept is the weighted median of the residuals).
/// One vote per player: each player is collapsed to one point per score bin (deciles of the build scores; the pooled
/// rate and the weighted median score of their builds there), a player's points share one weight (their hours, capped
/// at <see cref="GearModelOptions.CurveMaxWeightHours"/>, split by hours), only pairs of different players count and a
/// pair weighs as much as its lighter point. Above <see cref="GearModelOptions.CurveMaxPairwisePoints"/> points the
/// fit runs on score bins of the points. Needs <see cref="GearModelOptions.CurveMinPlayers"/> distinct players and
/// <see cref="GearModelOptions.CurveMinHours"/>; null otherwise, when no slope can be read, or when the slope is
/// negative or not finite. The observed range is the k-th lowest and k-th highest player score, so at least k players
/// reach each bound.
/// </summary>
public static class GearCurveFit
{
    private const int PlayerBins = 10;

    private readonly record struct Point(double X, double Y, double W, string Owner);

    public static GearCurveModel? Fit(IEnumerable<GearPlayerBuild> builds, GearModelOptions options)
    {
        var usable = builds.Where(b => b.Eligible && b.CommonPerHour > 0).ToList();
        var players = GearPlayerBuilds.Players(usable);
        var hours = usable.Sum(b => b.Hours);
        if (players < options.CurveMinPlayers || hours < options.CurveMinHours)
            return null;
        var points = PlayerPoints(usable, options.CurveMaxWeightHours);
        var binned = points.Count > options.CurveMaxPairwisePoints;
        var fitted = binned ? Bins(points, options.CurveBins) : points;
        var slope = WeightedSlope(fitted);
        if (slope is not { } value || value < 0 || !double.IsFinite(value))
            return null;
        var intercept = GearStatistics.WeightedMedian(fitted.Select(p => (p.Y - value * p.X, p.W)));
        if (!double.IsFinite(intercept))
            return null;
        var (min, max) = Range(usable, options.MinPlayers);
        return new GearCurveModel(intercept, value, min, max, players, hours, binned ? GearCurveModel.Binned : GearCurveModel.TheilSen);
    }

    /// <summary>One point per player and score decile, the player's capped hours split over their points by hours.</summary>
    private static List<Point> PlayerPoints(List<GearPlayerBuild> builds, double maxWeight)
    {
        var edges = Edges(builds.Select(b => b.Score).ToList());
        return builds.GroupBy(b => b.Player, StringComparer.Ordinal)
            .SelectMany(player => PointsOf(player.ToList(), edges, maxWeight))
            .ToList();
    }

    private static IEnumerable<Point> PointsOf(List<GearPlayerBuild> builds, List<double> edges, double maxWeight)
    {
        var total = builds.Sum(b => b.Hours);
        var weight = Math.Min(total, maxWeight);
        return builds.GroupBy(b => edges.Count(e => b.Score > e))
            .Select(bin => bin.ToList())
            .Select(bin => new Point(
                GearStatistics.WeightedMedian(bin.Select(b => (b.Score, b.Hours))),
                Math.Log(bin.Sum(b => b.Common) / bin.Sum(b => b.Hours)),
                weight * bin.Sum(b => b.Hours) / total,
                builds[0].Player));
    }

    private static List<double> Edges(List<double> scores)
        => Enumerable.Range(1, PlayerBins - 1).Select(i => GearStatistics.Percentile(scores, i / (double)PlayerBins)).ToList();

    /// <summary>Weighted median of the slopes between different owners; null without two distinct scores.</summary>
    private static double? WeightedSlope(IReadOnlyList<Point> points)
    {
        var slopes = new List<(double Value, double Weight)>();
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                AddSlope(slopes, points[i], points[j]);
        return slopes.Count == 0 ? null : GearStatistics.WeightedMedian(slopes);
    }

    private static void AddSlope(List<(double Value, double Weight)> slopes, Point a, Point b)
    {
        var dx = b.X - a.X;
        if (a.Owner == b.Owner || Math.Abs(dx) < 1e-9)
            return;
        slopes.Add(((b.Y - a.Y) / dx, Math.Min(a.W, b.W)));
    }

    /// <summary>Equal count bins by score, each the weighted median score and rate with the bin's summed weight.</summary>
    private static List<Point> Bins(List<Point> points, int bins)
    {
        var sorted = points.OrderBy(p => p.X).ToList();
        var size = (int)Math.Ceiling(sorted.Count / (double)bins);
        return sorted.Chunk(size)
            .Select((c, index) => new Point(GearStatistics.WeightedMedian(c.Select(p => (p.X, p.W))),
                GearStatistics.WeightedMedian(c.Select(p => (p.Y, p.W))), c.Sum(p => p.W), $"bin{index}"))
            .ToList();
    }

    private static (double Min, double Max) Range(List<GearPlayerBuild> builds, int k)
    {
        var perPlayer = builds.GroupBy(b => b.Player, StringComparer.Ordinal)
            .Select(g => (Low: g.Min(b => b.Score), High: g.Max(b => b.Score))).ToList();
        var lows = perPlayer.Select(p => p.Low).OrderBy(s => s).ToList();
        var highs = perPlayer.Select(p => p.High).OrderByDescending(s => s).ToList();
        var index = Math.Min(k, perPlayer.Count) - 1;
        var (min, max) = (lows[index], highs[index]);
        return min <= max ? (min, max) : (max, min);
    }
}
