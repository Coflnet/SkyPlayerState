using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Small robust statistics over sorted or unsorted samples.</summary>
public static class GearStatistics
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    private const double HighMaxVariation = 0.35;

    /// <summary>Linear interpolation between the closest ranks (Excel PERCENTILE.INC); 0 for an empty sample.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>
    /// The inverse of <see cref="Percentile"/> on an ascending sample: 0 .. 100, interpolated between ranks; below the
    /// smallest value 0, from the largest value 100.
    /// </summary>
    public static double PercentileRank(IReadOnlyList<double> sorted, double value)
    {
        if (sorted.Count == 0)
            return 0;
        if (sorted.Count == 1 || value >= sorted[^1])
            return value >= sorted[^1] ? 100 : 0;
        if (value <= sorted[0])
            return 0;
        var lower = LastAtOrBelow(sorted, value);
        var (a, b) = (sorted[lower], sorted[lower + 1]);
        var fraction = b > a ? (value - a) / (b - a) : 0;
        return 100 * (lower + fraction) / (sorted.Count - 1);
    }

    /// <summary>Binary search: the last index below the last element whose value is at most <paramref name="value"/>.</summary>
    private static int LastAtOrBelow(IReadOnlyList<double> sorted, double value)
    {
        var (low, high) = (0, sorted.Count - 2);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (sorted[middle] <= value)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }

    /// <summary>The weighted median: the first value (ascending) at which more than half the total weight is reached.</summary>
    public static double WeightedMedian(IEnumerable<(double Value, double Weight)> samples)
    {
        var sorted = samples.Where(s => s.Weight > 0).OrderBy(s => s.Value).ToList();
        if (sorted.Count == 0)
            return 0;
        var half = sorted.Sum(s => s.Weight) / 2;
        var cumulative = 0.0;
        foreach (var (value, weight) in sorted)
        {
            cumulative += weight;
            if (cumulative > half)
                return value;
        }
        return sorted[^1].Value;
    }

    /// <summary>Sample standard deviation / mean; infinity for a non positive mean or fewer than 2 values.</summary>
    public static double Variation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return double.PositiveInfinity;
        var mean = values.Average();
        if (mean <= 0)
            return double.PositiveInfinity;
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance) / mean;
    }

    /// <summary>high from 5 players with a coefficient of variation &lt;= 0.35, medium from 5 players, low below 5.</summary>
    public static string Confidence(int players, double variation)
    {
        if (players < GearModelPublisher.ExactFromPlayers)
            return Low;
        return variation <= HighMaxVariation ? High : Medium;
    }

    /// <summary>
    /// Per name: each player's hours weighted mean, then the median over the players that have the name; names of
    /// fewer than <paramref name="minPlayers"/> players are left out.
    /// </summary>
    public static Dictionary<string, double> PlayerMedians(
        IEnumerable<(string Player, double Hours, IReadOnlyDictionary<string, double>? Values)> samples, int minPlayers)
        => samples.Where(s => s.Values != null && s.Hours > 0)
            .SelectMany(s => s.Values!.Select(v => (s.Player, Name: v.Key, v.Value, s.Hours)))
            .GroupBy(v => (v.Player, v.Name))
            .Select(g => (g.Key.Name, Mean: g.Sum(v => v.Value * v.Hours) / g.Sum(v => v.Hours)))
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .Where(g => g.Count() >= minPlayers)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Median(g.Select(v => v.Mean).ToList()));
}
