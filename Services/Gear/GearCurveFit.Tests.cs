using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearCurveFitTests
{
    private static readonly GearModelOptions Options = GearModelOptions.Default;

    private static double Rate(double score) => 1000 * Math.Exp(0.002 * score);

    /// <summary>Players 1 .. n with scores 100, 200, ... on the exact curve 1000 × e^(0.002 × score).</summary>
    private static List<GearObservationInput> OnTheCurve(int players, double hours = 1)
        => Enumerable.Range(1, players).Select(i => Obs(i, ScoreGear(100 * i), hours, Rate(100 * i))).ToList();

    private static GearCurveModel? Fit(IEnumerable<GearObservationInput> inputs) => GearCurveFit.Fit(Builds([.. inputs]), Options);

    [Test]
    public void ExactExponential_IsRecovered()
    {
        var curve = Fit(OnTheCurve(10))!;

        curve.Slope.Should().BeApproximately(0.002, 1e-9);
        curve.PredictCommon(550).Should().BeApproximately(Rate(550), 1e-6);
        curve.Method.Should().Be(GearCurveModel.TheilSen);
        curve.Players.Should().Be(10);
        (curve.ScoreMin, curve.ScoreMax).Should().Be((200.0, 900.0), "the second lowest and second highest player score");
    }

    [Test]
    public void Outliers_BarelyMoveTheFit()
    {
        var inputs = OnTheCurve(10);
        inputs.Add(Obs(11, ScoreGear(150), 1, 1_000_000));
        inputs.Add(Obs(12, ScoreGear(950), 1, 1));

        var curve = Fit(inputs)!;

        curve.Slope.Should().BeApproximately(0.002, 0.0004, "two wild players out of twelve do not flip the slope");
        curve.PredictCommon(500).Should().BeApproximately(Rate(500), Rate(500) * 0.2);
    }

    [Test]
    public void TooFewPlayersOrHours_NoCurve()
    {
        Fit(OnTheCurve(7)).Should().BeNull("7 players are below 8");
        Fit(OnTheCurve(8, hours: 0.25)).Should().BeNull("2 hours are below 3");
        Fit(OnTheCurve(8)).Should().NotBeNull();
    }

    [Test]
    public void SameScoreForEveryone_NoCurve()
    {
        Fit(Enumerable.Range(1, 8).Select(i => Obs(i, ScoreGear(500), 1, 1000 * i))).Should().BeNull("no slope can be read");
    }

    [Test]
    public void ManyBuilds_AreFitOnBins()
    {
        var inputs = Enumerable.Range(1, 400).Select(i => Obs(i, ScoreGear(i * 2.5), 1, Rate(i * 2.5)));

        var curve = Fit(inputs)!;

        curve.Method.Should().Be(GearCurveModel.Binned);
        curve.Slope.Should().BeApproximately(0.002, 1e-4);
    }

    [Test]
    public void ALongGrinder_DoesNotOutweighEveryoneElse()
    {
        var inputs = OnTheCurve(10);
        // one player with 500 hours far below the curve at a high score
        inputs.Add(Obs(11, ScoreGear(1000), 500, 100));

        Fit(inputs)!.Slope.Should().BeApproximately(0.002, 0.0004, "a build's weight is capped at 10 hours");
    }

    /// <summary>
    /// Regression: points were predicted at the unrounded score but published with the rounded one; equal rounded scores
    /// gave duplicate points.
    /// </summary>
    [Test]
    public void Published_PointsPredictedAtTheirPublishedScore_DuplicateScoresOnce()
    {
        var builds = Builds([.. Enumerable.Range(0, 9).Select(i => Obs(i + 1, ScoreGear(1000 + 10 * i), 1, Rate(1000 + 10 * i)))]);
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var curve = GearCurveFit.Fit(distribution.Builds, Options)!;

        var published = GearModelPublisher.Curve(curve, distribution, 0, PlainScorer.Formula)!;

        published.Points.Select(p => (p.ScorePercentile, p.Score)).Should().Equal((50, 1000.0), (90, 1100.0));
        published.Points.Should().OnlyContain(p => p.CommonCoinsPerHour == Math.Round(curve.PredictCommon(p.Score)));
    }

    /// <summary>Regression: every build was a point and within-player pairs counted, so one prolific player set the slope.</summary>
    [Test]
    public void AProlificPlayer_HasOneVote()
    {
        // seven players with 10 hours each on the curve; player 8 has 40 one-hour builds with a falling rate
        var inputs = Enumerable.Range(1, 7).Select(i => Obs(i, ScoreGear(150 * i), 10, Rate(150 * i)))
            .Concat(Enumerable.Range(0, 40).Select(i => Obs(8, ScoreGear(100 + 25 * i), 1, 100_000 / (1 + i))));

        Fit(inputs)!.Slope.Should().BeApproximately(0.002, 0.0004);
    }

    [Test]
    public void AFallingCurve_IsNotPublished()
    {
        Fit(Enumerable.Range(1, 8).Select(i => Obs(i, ScoreGear(100 * i), 1, 10_000 - 1000 * i))).Should().BeNull("the slope must not be negative");
    }

    [Test]
    public void Predictions_NeverOverflow()
    {
        new GearCurveModel(0, 10, 0, 1000, 5, 10, GearCurveModel.TheilSen).PredictCommon(1e6).Should().Match(v => double.IsFinite(v));
    }

    [Test]
    public void Published_PointsAtTheDistributionPercentiles_SlopePerTenPercent()
    {
        var builds = Builds([.. OnTheCurve(9)]);
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var curve = GearCurveFit.Fit(distribution.Builds, Options)!;

        var published = GearModelPublisher.Curve(curve, distribution, 100, PlainScorer.Formula)!;

        published.Points.Select(p => p.Score).Should().Equal(180, 300, 500, 700, 820);
        published.Points[2].CommonCoinsPerHour.Should().Be(Math.Round(Rate(500)));
        published.Points[2].CoinsPerHour.Should().Be(Math.Round(Rate(500)) + 100, "plus the pooled rare drops");
        published.SlopePer10Pct.Should().Be(Math.Round((Math.Exp(0.002 * 50) - 1) * 100, 1), "+10 % of the median score 500");
        published.ScoreMax.Should().Be(800);
    }

    /// <summary>Regression: a steep curve of few players read absurd rates at the top (Sven 172.7M/h at the P90).</summary>
    [Test]
    public void Predictions_AreCappedAtTheThirdHighestPlayerRate_Rounded()
    {
        var curve = Fit(OnTheCurve(10))!;

        curve.CommonCap.Should().Be(5000, "the third highest player's 4953, 2 significant digits, is below 3 x the median");
        curve.PredictCommon(1000).Should().Be(5000, "no single player's exact rate is published");
        curve.PredictCommon(5000).Should().Be(5000);
        curve.IsCappedAt(5000).Should().BeTrue();
        curve.IsCappedAt(500).Should().BeFalse();
    }

    [Test]
    public void Cap_CanBeTurnedOff()
    {
        var curve = GearCurveFit.Fit(Builds([.. OnTheCurve(10)]), Options with { CurveCapEnabled = false })!;

        curve.CommonCap.Should().Be(double.PositiveInfinity);
        curve.PredictCommon(1000).Should().BeApproximately(Rate(1000), 1e-6);
    }

    [Test]
    public void Predictions_AreCappedAtThreeTimesTheMedianPlayer()
    {
        // seven players at about 1000 / h, three at 50 000 / h: the median player earns 1005.5
        var inputs = Enumerable.Range(1, 7).Select(i => Obs(i, ScoreGear(100 * i), 1, 1000 + i)).ToList();
        inputs.AddRange(Enumerable.Range(8, 3).Select(i => Obs(i, ScoreGear(100 * i), 1, 50_000)));

        var curve = Fit(inputs)!;

        curve.CommonCap.Should().Be(3000, "3 x 1005.5, 2 significant digits");
        curve.Slope.Should().BePositive();
        curve.PredictCommon(1e7).Should().Be(3000, "the three 50 000 players do not set the cap");
    }

    [Test]
    public void RecommendedRate_FromTheCurve_IsCappedAndFlagged()
    {
        var curve = Fit(OnTheCurve(10))!;

        var rate = GearBuildRecommender.Rate(1000, curve, 100, null, PlainScorer.Formula, Options)!;

        rate.CoinsPerHour.Should().Be(5100, "the cap 5000 plus the rare 100");
        rate.Capped.Should().BeTrue();
        rate.Basis.Should().Be(GearBuildRecommender.BasisCurve);
        GearBuildRecommender.Rate(500, curve, 100, null, PlainScorer.Formula, Options)!.Capped.Should().BeFalse();
    }
}
