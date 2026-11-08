using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The public form of measured clusters and effects. Privacy: no player key, fingerprint or observation leaves this
/// class; nothing below <see cref="GearClusterOptions.MinPlayers"/> players is published; item attributes only when
/// k players share them (<see cref="GearClusterItems"/>). Below <see cref="ExactFromPlayers"/> players (where the
/// median and the percentiles would give away every player's rate) the rates, stat values and percent change are
/// rounded to 2 significant digits, the observation and pair counts are banded, and the percentiles are left out.
/// Hours are always rounded to 2 significant digits.
/// </summary>
public static class GearClusterPublisher
{
    /// <summary>Clusters and effects with fewer players than this are rounded and banded.</summary>
    public const int ExactFromPlayers = 5;
    private const int SignificantDigits = 2;

    public static GearCluster? Cluster(MeasuredGearCluster measured, GearClusterOptions options)
    {
        var rates = measured.Rates;
        if (rates.Players < options.MinPlayers)
            return null;
        var rounded = rates.Players < ExactFromPlayers;
        var common = Rate(rates.CommonMedian, rounded);
        var rare = Rate(rates.RarePerHour, rounded);
        var round = StatRounding(rounded);
        return new GearCluster
        {
            Key = measured.Key.Text,
            Level = measured.Key.Level,
            ParentKey = measured.ParentKey,
            Players = rates.Players,
            Observations = rounded ? Band(rates.Observations) : rates.Observations,
            ObservationsLabel = rounded ? $"{Band(rates.Observations)}+" : rates.Observations.ToString(CultureInfo.InvariantCulture),
            Hours = RoundSignificant(rates.Hours, SignificantDigits),
            CommonCoinsPerHour = common,
            RareCoinsPerHour = rare,
            CoinsPerHour = common + rare,
            CommonP25 = rounded ? null : Math.Round(rates.CommonP25),
            CommonP75 = rounded ? null : Math.Round(rates.CommonP75),
            Confidence = rates.Confidence,
            Rounded = rounded,
            Slots = GearClusterItems.Slots(measured.Members, options.MinPlayers, round),
            Pet = GearClusterItems.Pet(measured.Members, options.MinPlayers, round),
            StatMedians = GearClusterItems.StatMedians(measured.Members, options.MinPlayers).ToDictionary(s => s.Key, s => round(s.Value))
        };
    }

    /// <summary>The published clusters with <see cref="GearCluster.IncludesChildren"/> set from the published parents.</summary>
    public static List<GearCluster> Clusters(IEnumerable<MeasuredGearCluster> measured, GearClusterOptions options)
    {
        var clusters = measured.Select(c => Cluster(c, options)).OfType<GearCluster>().ToList();
        var parents = clusters.Select(c => c.ParentKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var cluster in clusters)
            cluster.IncludesChildren = parents.Contains(cluster.Key);
        return clusters;
    }

    public static GearModifierEffect? Effect(MeasuredGearEffect measured, GearClusterOptions options)
    {
        if (measured.Players < options.MinPlayers)
            return null;
        var rounded = measured.Players < ExactFromPlayers;
        var percent = (measured.Ratio - 1) * 100;
        percent = rounded ? RoundSignificant(percent, SignificantDigits) : Math.Round(percent, 1);
        return new GearModifierEffect
        {
            Changes = measured.Changes.Select(c => new GearKeyChange { Component = c.Component, From = c.From, To = c.To }).ToList(),
            Players = measured.Players,
            Pairs = rounded ? Band(measured.Pairs) : measured.Pairs,
            Ratio = rounded ? Math.Round(1 + percent / 100, 4) : Math.Round(measured.Ratio, 3),
            PercentChange = percent,
            Rounded = rounded
        };
    }

    private static double Rate(double value, bool rounded) => rounded ? RoundSignificant(value, SignificantDigits) : Math.Round(value);

    private static Func<double, double> StatRounding(bool rounded)
        => rounded ? v => RoundSignificant(v, SignificantDigits) : v => Math.Round(v, 1);

    /// <summary>The lower bound of the 1-2-5 band of a count (7 gives 5, 23 gives 20).</summary>
    public static int Band(int count) => (int)GearFingerprint.BucketCounter("", count);

    /// <summary>Rounds to <paramref name="digits"/> significant digits through the decimal text, so 12345 gives exactly 12000.</summary>
    public static double RoundSignificant(double value, int digits)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return value;
        var text = value.ToString("G" + digits, CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
