using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Percentiles of a weighted sample: each value sits at the middle of its weight span, scaled so the smallest value is
/// at 0 and the largest at 1, and percentiles interpolate between neighbours. With equal weights this is Excel
/// PERCENTILE.INC (<see cref="GearStatistics.Percentile"/>).
/// </summary>
public sealed class GearWeightedSample
{
    private readonly double[] values;
    private readonly double[] positions;

    public GearWeightedSample(IEnumerable<(double Value, double Weight)> samples)
    {
        var sorted = samples.Where(s => s.Weight > 0).OrderBy(s => s.Value).ToList();
        values = sorted.Select(s => s.Value).ToArray();
        positions = Positions(sorted.Select(s => s.Weight).ToList());
    }

    public int Count => values.Length;

    /// <summary>The value at <paramref name="p"/> (0 .. 1); 0 for an empty sample.</summary>
    public double Percentile(double p)
    {
        if (values.Length == 0)
            return 0;
        if (values.Length == 1 || p <= 0)
            return values[0];
        if (p >= 1)
            return values[^1];
        var lower = LastAtOrBelow(positions, p);
        return Interpolate(positions, values, lower, p);
    }

    /// <summary>Where <paramref name="value"/> lies, 0 .. 100: 0 at or below the smallest value, 100 from the largest.</summary>
    public double Rank(double value)
    {
        if (values.Length == 0)
            return 0;
        if (value >= values[^1])
            return 100;
        if (value <= values[0])
            return 0;
        var lower = LastAtOrBelow(values, value);
        return 100 * Interpolate(values, positions, lower, value);
    }

    private static double[] Positions(List<double> weights)
    {
        var middles = new double[weights.Count];
        var cumulative = 0.0;
        for (var i = 0; i < weights.Count; i++)
        {
            middles[i] = cumulative + weights[i] / 2;
            cumulative += weights[i];
        }
        if (middles.Length < 2)
            return middles.Select(_ => 0.0).ToArray();
        var (first, span) = (middles[0], middles[^1] - middles[0]);
        return middles.Select(m => (m - first) / span).ToArray();
    }

    /// <summary>Linear interpolation of <paramref name="to"/> at <paramref name="x"/> between index <paramref name="lower"/> and the next.</summary>
    private static double Interpolate(double[] from, double[] to, int lower, double x)
    {
        var (a, b) = (from[lower], from[lower + 1]);
        var fraction = b > a ? (x - a) / (b - a) : 0;
        return to[lower] + fraction * (to[lower + 1] - to[lower]);
    }

    /// <summary>Binary search: the last index below the last element whose value is at most <paramref name="x"/>.</summary>
    private static int LastAtOrBelow(double[] sorted, double x)
    {
        var (low, high) = (0, sorted.Length - 2);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (sorted[middle] <= x)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }
}
