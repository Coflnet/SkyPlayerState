using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Thresholds of the setup derivation.</summary>
public sealed record GearClusterOptions
{
    /// <summary>Distinct players a cluster, a modifier effect or a published item attribute needs (k).</summary>
    public int MinPlayers { get; init; } = 2;
    /// <summary>Play time a cluster needs, summed over its observations.</summary>
    public double MinHours { get; init; } = 1;
    /// <summary>Play time a player needs on each side of a modifier change.</summary>
    public double MinHoursPerSide { get; init; } = 1;
    public int MaxClusters { get; init; } = 12;

    public static readonly GearClusterOptions Default = new();
}

/// <summary>A player's collapsed rate within a cluster.</summary>
public record GearPlayerRate(string Player, double Hours, double CommonPerHour);

/// <summary>Rates of a cluster, unrounded (see <see cref="GearClusterPublisher"/> for the public form).</summary>
public record GearClusterRates(int Players, int Observations, double Hours, double CommonMedian, double RarePerHour,
    double CommonP25, double CommonP75, double Variation, string Confidence)
{
    public double Total => CommonMedian + RarePerHour;
}

/// <summary>A cluster that met the thresholds: its key, every observation under the key and their rates.</summary>
public record MeasuredGearCluster(GearSetupKey Key, IReadOnlyList<GearClusterInput> Members, GearClusterRates Rates)
{
    /// <summary>The key one level up (every member shares it), null for L1.</summary>
    public string? ParentKey => Key.Level > GearSetupKeys.ToolArmor ? Members[0].Keys[Key.Level - 2]?.Text : null;
}

/// <summary>
/// Derives measured setups from observations (pure). Levels are NESTED: at every level each key with at least
/// <see cref="GearClusterOptions.MinPlayers"/> distinct players and <see cref="GearClusterOptions.MinHours"/> is a
/// cluster of ALL observations under that key, so an L1 cluster stands for everyone with that tool and armor, including
/// the players of its finer L2/L3 clusters (children are subsets). A cluster's rate first collapses each player to one
/// hours weighted common rate (so a player with many sessions counts once), then takes the median over players; the
/// rare drop value is pooled (sum of rare coins / sum of hours).
/// </summary>
public static class GearClustering
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    private const double HighMaxVariation = 0.35;

    /// <summary>
    /// The qualifying clusters, at most <see cref="GearClusterOptions.MaxClusters"/>: always the best total rate overall
    /// and the best of each level, the rest by players (then hours); best total rate first.
    /// </summary>
    public static List<MeasuredGearCluster> Derive(IEnumerable<GearClusterInput> inputs, GearClusterOptions? options = null)
    {
        options ??= GearClusterOptions.Default;
        var keyed = inputs.Where(i => i.Keys[0] != null).ToList();
        var clusters = new List<MeasuredGearCluster>();
        for (var level = GearSetupKeys.WithModifiers; level >= GearSetupKeys.ToolArmor; level--)
            clusters.AddRange(ClusterLevel(keyed, level - 1, options));
        return Select(clusters, options.MaxClusters);
    }

    private static IEnumerable<MeasuredGearCluster> ClusterLevel(List<GearClusterInput> keyed, int index, GearClusterOptions options)
    {
        foreach (var group in keyed.Where(k => k.Keys[index] != null).GroupBy(k => k.Keys[index]!.Text, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (Qualifies(members, options))
                yield return new MeasuredGearCluster(members[0].Keys[index]!, members, Measure(members));
        }
    }

    public static bool Qualifies(IReadOnlyCollection<GearClusterInput> members, GearClusterOptions options)
        => members.Select(m => m.Player).Distinct(StringComparer.Ordinal).Count() >= options.MinPlayers
           && members.Sum(m => m.Hours) >= options.MinHours;

    /// <summary>One hours weighted common rate per player: sum of common coins / sum of hours.</summary>
    public static List<GearPlayerRate> PlayerRates(IEnumerable<GearClusterInput> members)
        => members.GroupBy(m => m.Player, StringComparer.Ordinal)
            .Select(g => (Player: g.Key, Hours: g.Sum(m => m.Hours), Common: g.Sum(m => m.Observation.CommonCoins)))
            .Where(p => p.Hours > 0)
            .Select(p => new GearPlayerRate(p.Player, p.Hours, p.Common / p.Hours))
            .ToList();

    public static GearClusterRates Measure(IReadOnlyList<GearClusterInput> members)
    {
        var rates = PlayerRates(members).Select(p => p.CommonPerHour).OrderBy(r => r).ToList();
        var hours = members.Sum(m => m.Hours);
        var rare = hours > 0 ? members.Sum(m => m.Observation.RareCoins) / hours : 0;
        var variation = GearStatistics.Variation(rates);
        return new GearClusterRates(rates.Count, members.Count, hours, GearStatistics.Percentile(rates, 0.5), rare,
            GearStatistics.Percentile(rates, 0.25), GearStatistics.Percentile(rates, 0.75), variation,
            Confidence(rates.Count, variation));
    }

    public static string Confidence(int players, double variation)
    {
        if (players >= 5 && variation <= HighMaxVariation)
            return High;
        return players >= 3 ? Medium : Low;
    }

    private static List<MeasuredGearCluster> Select(List<MeasuredGearCluster> clusters, int max)
    {
        var byRate = clusters.OrderByDescending(c => c.Rates.Total).ThenBy(c => c.Key.Text, StringComparer.Ordinal).ToList();
        var kept = byRate.Take(1)
            .Concat(byRate.GroupBy(c => c.Key.Level).OrderByDescending(g => g.Key).Select(g => g.First()))
            .Distinct().Take(max).ToList();
        var fill = clusters.Except(kept)
            .OrderByDescending(c => c.Rates.Players).ThenByDescending(c => c.Rates.Hours).ThenBy(c => c.Key.Text, StringComparer.Ordinal)
            .Take(max - kept.Count);
        return kept.Concat(fill)
            .OrderByDescending(c => c.Rates.Total).ThenBy(c => c.Key.Text, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Small robust statistics over sorted or unsorted samples.</summary>
public static class GearStatistics
{
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
