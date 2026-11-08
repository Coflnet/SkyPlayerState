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
        Fit(OnTheCurve(4)).Should().BeNull("4 players are below 5");
        Fit(OnTheCurve(5, hours: 0.5)).Should().BeNull("2.5 hours are below 3");
        Fit(OnTheCurve(5)).Should().NotBeNull();
    }

    [Test]
    public void SameScoreForEveryone_NoCurve()
    {
        Fit(Enumerable.Range(1, 6).Select(i => Obs(i, ScoreGear(500), 1, 1000 * i))).Should().BeNull("no slope can be read");
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
        // five players with 10 hours each on the curve; player 6 has 40 one-hour builds with a falling rate
        var inputs = Enumerable.Range(1, 5).Select(i => Obs(i, ScoreGear(200 * i), 10, Rate(200 * i)))
            .Concat(Enumerable.Range(0, 40).Select(i => Obs(6, ScoreGear(100 + 25 * i), 1, 100_000 / (1 + i))));

        Fit(inputs)!.Slope.Should().BeApproximately(0.002, 0.0004);
    }

    [Test]
    public void AFallingCurve_IsNotPublished()
    {
        Fit(Enumerable.Range(1, 6).Select(i => Obs(i, ScoreGear(100 * i), 1, 10_000 - 1000 * i))).Should().BeNull("the slope must not be negative");
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
}
