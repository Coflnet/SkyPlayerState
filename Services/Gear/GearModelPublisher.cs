using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The public form of the derivation. Privacy: no player key, fingerprint or per-player row leaves this class; nothing
/// below <see cref="GearModelOptions.MinPlayers"/> players is published; item attributes only when k players share them
/// (<see cref="GearSharedItems"/>). Below <see cref="ExactFromPlayers"/> players (where a median or a percentile would
/// give away single players' values) rates, scores, stats and percent changes are rounded to 2 significant digits,
/// counts and hours are banded (1-2-5 lower bounds) and the rate percentiles are left out. Gear scores (percentiles,
/// thresholds, curve points and range, the recommended build's score) are always rounded to 2 significant digits.
/// </summary>
public static class GearModelPublisher
{
    /// <summary>Numbers of fewer players than this are rounded and banded.</summary>
    public const int ExactFromPlayers = 5;
    private const int SignificantDigits = 2;

    /// <summary>The formula info of a task without a score model (an unbound task that is not fought).</summary>
    public static GearScoreFormulaInfo NoFormula() => new() { Kind = GearScoreFormula.None };

    public static GearScoreFormulaInfo Formula(GearScoreFormula formula, int effectCount) => new()
    {
        Kind = formula.Kind,
        Weights = formula.Weights.ToDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal),
        Expression = formula.IsLog
            ? "ln((5 + Damage) × (1 + Strength / 100) × (1 + Crit Damage / 100))"
            : string.Join(" + ", formula.Weights.Select(w => $"{w.Value.ToString(CultureInfo.InvariantCulture)} × {w.Key}")),
        EffectCount = effectCount
    };

    public static GearScoreSummary Distribution(GearScoreDistribution distribution)
    {
        var rounded = distribution.Players < ExactFromPlayers;
        // below 5 players only the median is published: the other percentiles repeat it
        double Score(double p) => RoundScore(distribution.Percentile(rounded ? 0.5 : p));
        return new GearScoreSummary
        {
            Players = PlayerCount(distribution.Players),
            PlayerBuilds = rounded ? Band(distribution.Builds.Count) : distribution.Builds.Count,
            Hours = Hours(distribution.Hours, rounded),
            ScoreP10 = Score(0.1),
            ScoreP25 = Score(0.25),
            ScoreP50 = Score(0.5),
            ScoreP75 = Score(0.75),
            ScoreP90 = Score(0.9),
            UnknownEquipmentShare = Math.Round(distribution.UnknownEquipmentShare, 1),
            Rounded = rounded
        };
    }

    public static GearLateGame LateGame(GearCohort cohort, GearModelOptions options)
    {
        var rates = cohort.Members.Select(m => m.CommonPerHour).OrderBy(r => r).ToList();
        var rounded = cohort.Players < ExactFromPlayers;
        var common = Rate(GearStatistics.Median(rates), rounded);
        var rare = Rate(cohort.Members.Sum(m => m.Rare) / cohort.Hours, rounded);
        return new GearLateGame
        {
            ScoreFrom = RoundScore(cohort.ScoreFrom),
            Widened = cohort.Widened,
            Players = PlayerCount(cohort.Players),
            PlayersLabel = PlayersLabel(cohort.Players),
            Hours = Hours(cohort.Hours, rounded),
            CommonCoinsPerHour = common,
            RareCoinsPerHour = rare,
            CoinsPerHour = common + rare,
            CommonP25 = rounded ? null : Math.Round(GearStatistics.Percentile(rates, 0.25)),
            CommonP75 = rounded ? null : Math.Round(GearStatistics.Percentile(rates, 0.75)),
            Confidence = GearStatistics.Confidence(cohort.Players, GearStatistics.Variation(rates)),
            Rounded = rounded,
            StatMedians = GearSharedItems.StatMedians(cohort.Members.SelectMany(m => m.Observations), options.MinPlayers)
                .ToDictionary(s => s.Key, s => StatRounding(rounded)(s.Value))
        };
    }

    /// <summary>
    /// The curve has at least 5 players (<see cref="GearModelOptions.CurveMinPlayers"/>), so its rates are exact. Null when
    /// any published value is not finite (the curve is then not used at all).
    /// </summary>
    public static GearRateCurve? Curve(GearCurveModel curve, GearScoreDistribution distribution, double rarePerHour, GearScoreFormula formula)
    {
        var published = CurveValues(curve, distribution, rarePerHour, formula);
        var values = published.Points.SelectMany(p => new[] { p.Score, p.CommonCoinsPerHour, p.CoinsPerHour })
            .Concat([published.SlopePer10Pct, published.RareCoinsPerHour, published.ScoreMin, published.ScoreMax]);
        return values.All(double.IsFinite) ? published : null;
    }

    private static GearRateCurve CurveValues(GearCurveModel curve, GearScoreDistribution distribution, double rarePerHour, GearScoreFormula formula)
    {
        var rare = Math.Round(rarePerHour);
        return new GearRateCurve
        {
            Players = curve.Players,
            SlopePer10Pct = Math.Round(SlopePer10Pct(curve, distribution.Percentile(0.5), formula), 1),
            RareCoinsPerHour = rare,
            Points = Points(curve, distribution, rare),
            ScoreMin = RoundSignificant(curve.ScoreMin, SignificantDigits),
            ScoreMax = RoundSignificant(curve.ScoreMax, SignificantDigits),
            Method = curve.Method
        };
    }

    /// <summary>Percent change of the rate for a 10 % higher score (log scale: 10 % more of the underlying product).</summary>
    public static double SlopePer10Pct(GearCurveModel curve, double medianScore, GearScoreFormula formula)
    {
        var step = formula.Extend(medianScore, 0.1) - medianScore;
        return (Math.Exp(curve.Slope * step) - 1) * 100;
    }

    /// <summary>
    /// The curve at the distribution's published percentiles, predicted at the published (rounded) score; points whose
    /// rounded scores coincide are published once, with the higher percentile.
    /// </summary>
    private static List<GearCurvePoint> Points(GearCurveModel curve, GearScoreDistribution distribution, double rare)
        => GearScoreDistribution.PublishedPercentiles
            .Select(p => Point(curve, RoundScore(distribution.Percentile(p)), p, rare))
            .GroupBy(p => p.Score)
            .Select(g => g.MaxBy(p => p.ScorePercentile)!)
            .OrderBy(p => p.ScorePercentile)
            .ToList();

    private static GearCurvePoint Point(GearCurveModel curve, double score, double percentile, double rare)
    {
        var common = Math.Round(curve.PredictCommon(score));
        return new GearCurvePoint
        {
            ScorePercentile = (int)Math.Round(percentile * 100),
            Score = score,
            CommonCoinsPerHour = common,
            CoinsPerHour = common + rare
        };
    }

    public static GearCatalogPiece Piece(GearCatalogEntry entry)
    {
        var rounded = entry.Players < ExactFromPlayers;
        var round = StatRounding(rounded);
        return new GearCatalogPiece
        {
            Id = entry.Id,
            Slot = entry.Slot,
            Item = entry.Piece == null ? null : new GearPiece(entry.Piece) { Stats = Rounded(entry.Piece.Stats, round) },
            Pet = entry.Pet == null ? null : new GearPet(entry.Pet) { Stats = Rounded(entry.Pet.Stats, round) },
            ScoreContribution = rounded ? RoundSignificant(entry.Contribution, SignificantDigits) : Math.Round(entry.Contribution, 2),
            UsedAtScorePercentile = !rounded && entry.UsedAtPercentile is { } p ? Math.Round(p) : null,
            Effects = entry.Effects.Select(PieceEffect).ToList(),
            Players = PlayerCount(entry.Players),
            PlayersLabel = PlayersLabel(entry.Players),
            Rounded = rounded
        };
    }

    public static GearRecommendedBuild Recommended(GearRecommendation build, GearRecommendedRate rate, GearScoreDistribution? distribution)
    {
        return new GearRecommendedBuild
        {
            Pieces = build.Entries.ToDictionary(e => e.Key, e => e.Value.Id, StringComparer.Ordinal),
            Score = RoundScore(build.Score),
            ScorePercentile = distribution == null ? null : Math.Round(distribution.RankOf(build.Score)),
            CoinsPerHour = rate.CoinsPerHour,
            Extrapolated = rate.Extrapolated,
            Capped = rate.Capped,
            Basis = rate.Basis,
            Effects = build.Effects.Select(e => e.Effect.Id).Distinct().ToList()
        };
    }

    public static GearModifierEffect? Effect(MeasuredGearEffect measured, GearModelOptions options)
    {
        if (measured.Players < options.MinPlayers)
            return null;
        var rounded = measured.Players < ExactFromPlayers;
        var percent = (measured.Ratio - 1) * 100;
        percent = rounded ? RoundSignificant(percent, SignificantDigits) : Math.Round(percent, 1);
        return new GearModifierEffect
        {
            Changes = measured.Changes.Select(c => new GearKeyChange { Component = c.Component, From = c.From, To = c.To }).ToList(),
            Players = PlayerCount(measured.Players),
            Pairs = rounded ? Band(measured.Pairs) : measured.Pairs,
            Ratio = rounded ? Math.Round(1 + percent / 100, 4) : Math.Round(measured.Ratio, 3),
            PercentChange = percent,
            Rounded = rounded
        };
    }

    private static GearPieceEffect PieceEffect(GearEntryEffect effect) => new()
    {
        Id = effect.Effect.Id,
        Kind = effect.Effect.Kind,
        Mode = effect.Effect.Mode,
        Stats = effect.Effect.Stats.ToList(),
        Value = Math.Round(effect.Amount, 3),
        SetShare = effect.IsSetShare,
        Confidence = effect.Effect.Confidence,
        Source = effect.Effect.Source
    };

    private static int PlayerCount(int players) => players < ExactFromPlayers ? Band(players) : players;

    private static string PlayersLabel(int players)
        => players < ExactFromPlayers ? $"{Band(players)}+" : players.ToString(CultureInfo.InvariantCulture);

    private static double Rate(double value, bool rounded) => rounded ? RoundSignificant(value, SignificantDigits) : Math.Round(value);

    private static double RoundScore(double value) => RoundSignificant(value, SignificantDigits);

    private static double Hours(double hours, bool rounded) => rounded ? BandLowerBound(hours) : RoundSignificant(hours, SignificantDigits);

    private static Func<double, double> StatRounding(bool rounded)
        => rounded ? v => RoundSignificant(v, SignificantDigits) : v => Math.Round(v, 1);

    private static Dictionary<string, double> Rounded(Dictionary<string, double>? stats, Func<double, double> round)
        => (stats ?? new()).ToDictionary(s => s.Key, s => round(s.Value), StringComparer.Ordinal);

    /// <summary>The lower bound of the 1-2-5 band of a count (7 gives 5, 23 gives 20).</summary>
    public static int Band(int count) => (int)GearFingerprint.BucketCounter("", count);

    /// <summary>The lower bound of the 1-2-5 band of a positive value (3.7 gives 2, 0.8 gives 0.5); 0 for non positive values.</summary>
    public static double BandLowerBound(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var leading = value / power;
        var step = leading >= 5 ? 5 : leading >= 2 ? 2 : 1;
        return RoundSignificant(step * power, 1);
    }

    /// <summary>Rounds to <paramref name="digits"/> significant digits through the decimal text, so 12345 gives exactly 12000.</summary>
    public static double RoundSignificant(double value, int digits)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return value;
        var text = value.ToString("G" + digits, CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
