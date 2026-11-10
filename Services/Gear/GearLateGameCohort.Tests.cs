using System;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearLateGameCohortTests
{
    private static readonly GearModelOptions Options = GearModelOptions.Default;
    /// <summary>The cohort rules before 2026-11-09 (2 players, 1 h), for the tests of the sweep mechanics.</summary>
    private static readonly GearModelOptions TwoPlayers = Options with { LateGameMinPlayers = 2, LateGameMinHours = 1 };

    /// <summary>Player i + 1 plays one hour with score <c>scores[i]</c> at rate <c>1000 × (i + 1)</c>.</summary>
    private static GearScoreDistribution Distribution(params double[] scores)
        => GearScoreDistribution.Of(Builds(scores.Select((s, i) => Obs(i + 1, ScoreGear(s), 1, 1000 * (i + 1))).ToArray()), Options)!;

    [Test]
    public void Distribution_OneWeightPerPlayerBuild_OnlyBuildsWithToolAndArmor()
    {
        var noArmor = ScoreGear(5000);
        foreach (var slot in GearSnapshot.ArmorSlots)
            noArmor.Slots.Remove(slot);
        var builds = Builds(Obs(1, ScoreGear(100), 1, 1000), Obs(1, ScoreGear(100), 5, 1000), Obs(2, ScoreGear(200), 1, 1000),
            Obs(3, noArmor, 1, 1000));

        var distribution = GearScoreDistribution.Of(builds, Options)!;

        distribution.Builds.Select(b => b.Score).Should().BeEquivalentTo([100.0, 200.0]);
        distribution.Players.Should().Be(2, "player 3's armor is unknown");
        distribution.Hours.Should().Be(7);
        distribution.Percentile(0.5).Should().Be(150, "player 1's six hours are one build");
    }

    /// <summary>Regression: every build had one vote, so one player with many builds owned the percentiles.</summary>
    [Test]
    public void Distribution_OneVotePerPlayer_APlayerWithManyBuildsDoesNotOwnThePercentiles()
    {
        var inputs = new[] { 100.0, 200, 300, 400 }.Select((s, i) => Obs(i + 1, ScoreGear(s), 1, 1000))
            .Concat(Enumerable.Range(0, 40).Select(i => Obs(5, ScoreGear(1000 + i), 1, 1000))).ToArray();

        var distribution = GearScoreDistribution.Of(Builds(inputs), Options)!;

        distribution.Percentile(0.25).Should().BeLessThan(1000);
        distribution.Percentile(0.5).Should().BeLessThan(1000);
        distribution.Percentile(0.75).Should().BeLessThan(1000, "player 5 is one of five votes, the top fifth");
        distribution.Percentile(0.9).Should().BeGreaterThan(1000);
        GearStatistics.Percentile(distribution.Builds.Select(b => b.Score).ToList(), 0.25).Should().BeGreaterThan(1000, "one vote per build would");
    }

    [Test]
    public void Distribution_NeedsThreeOfFourArmorPieces()
    {
        var twoPieces = ScoreGear(500);
        twoPieces.Slots.Remove(GearSnapshot.Helmet);
        twoPieces.Slots.Remove(GearSnapshot.Boots);
        var threePieces = ScoreGear(600);
        threePieces.Slots.Remove(GearSnapshot.Helmet);

        var builds = Builds(Obs(1, ScoreGear(100), 1, 1000), Obs(2, twoPieces, 1, 1000), Obs(3, threePieces, 1, 1000));

        GearScoreDistribution.Of(builds, Options)!.Builds.Select(b => b.Score).Should().BeEquivalentTo([100.0, 600.0]);
    }

    [Test]
    public void Cohort_BuildsUnderTenMinutes_DoNotCount()
    {
        var builds = Builds(Obs(1, ScoreGear(100), 1, 1000), Obs(1, ScoreGear(101), 0.1, 1_000_000), Obs(2, ScoreGear(99), 1, 3000),
            Obs(3, ScoreGear(1), 1, 1), Obs(4, ScoreGear(2), 1, 1), Obs(5, ScoreGear(3), 1, 1), Obs(6, ScoreGear(4), 1, 1),
            Obs(7, ScoreGear(5), 1, 1), Obs(8, ScoreGear(6), 1, 1));

        var cohort = GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, TwoPlayers)!;

        cohort.Members.Single(m => m.Player == Player(1)).CommonPerHour.Should().Be(1000, "the 6 minute build is left out");
    }

    [Test]
    public void Distribution_UnknownEquipmentIsAllowed_AndReported()
    {
        var withEquipment = TestGear(pet: null, armor: "TEST_PLAIN");
        var builds = Builds(Obs(1, withEquipment, 1, 1000), Obs(2, ScoreGear(1), 1, 1000), Obs(3, ScoreGear(2), 1, 1000), Obs(4, ScoreGear(3), 1, 1000));

        GearScoreDistribution.Of(builds, Options)!.UnknownEquipmentShare.Should().Be(0.75);
    }

    [Test]
    public void Distribution_BelowTwoPlayers_IsNull()
    {
        GearScoreDistribution.Of(Builds(Obs(1, ScoreGear(1), 3, 1000), Obs(1, ScoreGear(2), 3, 1000)), Options).Should().BeNull();
    }

    [Test]
    public void Cohort_TheBuildsFromTheNinetiethPercentile()
    {
        var distribution = Distribution(Enumerable.Range(1, 30).Select(i => (double)i).ToArray());

        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        cohort.ScoreFrom.Should().BeApproximately(27.1, 1e-9, "P90 of 1 .. 30");
        cohort.Widened.Should().BeFalse();
        cohort.Members.SelectMany(m => m.Builds).Select(b => b.Score).Should().BeEquivalentTo([28.0, 29.0, 30.0]);
    }

    [Test]
    public void Cohort_WidenedDownToTwoPlayers()
    {
        // P90 = 84 holds only the top player, the next lower score (80) is above the P75 (70)
        var cohort = GearLateGameCohort.Select(Distribution(10, 20, 30, 40, 50, 60, 70, 80, 100), TwoPlayers)!;

        cohort.Widened.Should().BeTrue();
        cohort.ScoreFrom.Should().Be(80);
        cohort.Players.Should().Be(2);
    }

    /// <summary>Regression: cohorts of 2 players over 1 h swung 4x between derivations (Wheat 18 -> 4.4M/h).</summary>
    [Test]
    public void Cohort_NeedsThreePlayersAndTwoHours()
    {
        var cohort = GearLateGameCohort.Select(Distribution(10, 20, 30, 40, 50, 60, 70, 80, 100), Options)!;

        cohort.Players.Should().Be(3, "two players are not enough any more");
        cohort.ScoreFrom.Should().Be(70, "widened down to the P75");
        cohort.Hours.Should().Be(3);
    }

    [Test]
    public void Cohort_ThreePlayersUnderTwoHours_IsNoCohort()
    {
        var builds = Builds(new double[] { 10, 20, 30, 40, 50, 60, 70, 80, 100 }.Select((s, i) => Obs(i + 1, ScoreGear(s), 0.5, 1000)).ToArray());

        GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, Options).Should().BeNull("3 players down to the P75 hold 1.5 hours");
    }

    [Test]
    public void Cohort_NeverBelowTheSeventyFifthPercentile()
    {
        // P90 = 70.9 and P75 = 27.25 of 1, 2, 3, 100 hold only the 100: the next build (3) is below the P75
        GearLateGameCohort.Select(Distribution(1, 2, 3, 100), Options).Should().BeNull();
    }

    [Test]
    public void Cohort_APlayerCountsOnce_TheirBuildsAboveTheThresholdPooledByHours()
    {
        var builds = Builds(Obs(1, ScoreGear(100), 1, 9000), Obs(1, ScoreGear(99), 1, 1000), Obs(2, ScoreGear(98), 1, 5000),
            Obs(3, ScoreGear(10), 1, 1000), Obs(4, ScoreGear(11), 1, 1000), Obs(5, ScoreGear(12), 1, 1000),
            Obs(6, ScoreGear(13), 1, 1000), Obs(7, ScoreGear(14), 1, 1000), Obs(8, ScoreGear(15), 1, 1000));

        var cohort = GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, TwoPlayers)!;

        cohort.Members.Select(m => m.Player).Should().BeEquivalentTo([Player(1), Player(2)], "player 1's second build is not a second player");
        cohort.Members.Single(m => m.Player == Player(1)).CommonPerHour.Should().Be(5000, "(9000 + 1000) coins over 2 hours");
    }

    [Test]
    public void LateGame_PublishedRates_MedianOverPlayers_RarePooled_RoundedBelowFive()
    {
        var builds = Builds(Obs(1, ScoreGear(100), 3, 12_345, rarePerHour: 1000), Obs(2, ScoreGear(90), 1, 23_456),
            Obs(3, ScoreGear(1), 1, 1), Obs(4, ScoreGear(2), 1, 1), Obs(5, ScoreGear(3), 1, 1), Obs(6, ScoreGear(4), 1, 1),
            Obs(7, ScoreGear(5), 1, 1), Obs(8, ScoreGear(6), 1, 1));
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        // the cohort's own pooled drops, uncapped: only player 1 has rare drops, the cap and the task's rate (GearRareRateTests) would hide them
        var unshrunk = TwoPlayers with { LateGamePriorHours = 0, RareCapPlayers = 0, LateGameRareFromTask = false };
        var cohort = GearLateGameCohort.Select(distribution, unshrunk)!;

        var published = GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution, cohort, null, unshrunk), unshrunk);

        published.Players.Should().Be(2);
        published.PlayersLabel.Should().Be("2+");
        published.CommonCoinsPerHour.Should().Be(18_000, "the median 17900.5, 2 significant digits");
        published.RareCoinsPerHour.Should().Be(750, "3000 rare coins over 4 hours");
        published.Hours.Should().Be(2, "4 hours banded");
        published.CommonP25.Should().BeNull();
        published.Confidence.Should().Be(GearStatistics.Low);
        published.Rounded.Should().BeTrue();
    }

    [Test]
    public void Prior_MedianPlayerCommonRate_PooledRare()
    {
        var builds = Builds(Obs(1, ScoreGear(1), 1, 1000), Obs(1, ScoreGear(2), 3, 3000), Obs(2, ScoreGear(3), 1, 4000, rarePerHour: 8000),
            Obs(3, ScoreGear(4), 2, 9000));

        // uncapped: only player 2 has rare drops, the cap (GearRareRateTests) would take them out
        var prior = GearRatePrior.Of(GearScoreDistribution.Of(builds, Options)!, Options with { RareCapPlayers = 0 });

        prior.Common.Should().Be(4000, "players at 2500 (10 000 over 4 h), 4000 and 9000");
        prior.Rare.Should().Be(8000.0 / 7, "8000 rare coins over 7 hours");
    }

    /// <summary>Regression: small cohorts were published as they were, Wheat fell from 18 to 4.4M/h between two checks.</summary>
    [Test]
    public void LateGameRate_IsShrunkTowardThePrior_ByHours()
    {
        var prior = Flat(4000, 1000);

        var halfway = prior.Shrink(10_000, 3000, 3, Options);
        var mostly = prior.Shrink(10_000, 3000, 27, Options);

        halfway.Common.Should().BeApproximately(7000, 1e-6, "3 h against the prior's 3 h: halfway");
        halfway.Rare.Should().BeApproximately(2000, 1e-6);
        mostly.Common.Should().BeApproximately(9400, 1e-6, "27 h weigh 90 %");
        mostly.Rare.Should().BeApproximately(2800, 1e-6);
        prior.Shrink(10_000, 3000, 3, Options with { LateGamePriorHours = 0 }).Should().Be(new GearShrunkRate(10_000, 3000, false),
            "0 prior hours turn the shrinkage off");
    }

    /// <summary>Regression: the late game cohort was published far below the typical player (M7 at -9.7M/h).</summary>
    [Test]
    public void LateGameRate_BelowThePrior_IsThePrior()
    {
        var prior = Flat(4000, 1000);

        prior.Shrink(-19_000_000, 9_300_000, 3, Options).Should().Be(new GearShrunkRate(4000, 1000, true));
        prior.Shrink(-19_000_000, 9_300_000, 3, Options with { LateGameFloorAtPrior = false }).Floored.Should().BeFalse("the floor can be turned off");
        var above = prior.Shrink(3000, 2500, 3, Options);
        above.Common.Should().BeApproximately(3500, 1e-6, "the total 5250 is above the prior's 5000");
        above.Rare.Should().BeApproximately(1750, 1e-6);
    }

    [Test]
    public void LateGame_PublishedRate_IsTheShrunkRate()
    {
        // players 7 .. 9 form the cohort (3 h at 10 000), players 1 .. 6 earn 1000
        var builds = Builds(Enumerable.Range(1, 9).Select(i => Obs(i, ScoreGear(10 * i), 1, i > 6 ? 10_000 : 1000)).ToArray());
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        var published = GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution, cohort, null, Options), Options);

        cohort.Players.Should().Be(3);
        published.CommonCoinsPerHour.Should().Be(5500, "halfway between the cohort's 10 000 and the median player's 1000");
        published.CoinsPerHour.Should().Be(5500);
    }

    private static GearLateGamePrior Flat(double common, double rare) => new(new GearRatePrior(common, rare), new GearRatePrior(common, rare));

    /// <summary>
    /// Regression: shrinking toward the all-player median pulled the endgame below the curve-read budget and mid builds,
    /// which SkyApi then hid. With a curve the target is the curve at the cohort's median score; the floor stays.
    /// </summary>
    [Test]
    public void Prior_WithACurve_IsTheCurveAtTheCohortsMedianScore()
    {
        // players 7 .. 9 form the cohort (scores 70, 80, 90), players 1 .. 6 earn 1000
        var builds = Builds(Enumerable.Range(1, 9).Select(i => Obs(i, ScoreGear(10 * i), 1, i > 6 ? 10_000 : 1000)).ToArray());
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;
        var curve = new GearCurveModel(Math.Log(1000), 0.02, 10, 90, 9, 9, GearCurveModel.TheilSen);

        var prior = GearLateGamePrior.Of(distribution, cohort, curve, Options);

        GearLateGamePrior.MedianScore(cohort).Should().Be(80);
        prior.Target.Common.Should().BeApproximately(1000 * Math.Exp(1.6), 1e-6, "the curve at score 80");
        prior.Floor.Common.Should().Be(1000, "the all-player median");
        GearModelPublisher.LateGame(cohort, prior, Options).CommonCoinsPerHour
            .Should().Be(7500, "halfway between 10 000 and the curve's 4953, 2 significant digits");
    }

    /// <summary>Regression: the shrunk headline was published next to the cohort's unshrunk P25 and P75.</summary>
    [Test]
    public void LateGame_PercentilesAreShrunkLikeTheHeadline()
    {
        // 45 players at 1000 / h; the top 5 (the P90 cohort) at 10 000 .. 18 000
        var builds = Builds(Enumerable.Range(1, 50).Select(i => Obs(i, ScoreGear(10 * i), 1, i > 45 ? 10_000 + 2000 * (i - 46) : 1000)).ToArray());
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        var published = GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution, cohort, null, Options), Options);

        cohort.Players.Should().Be(5);
        published.CommonCoinsPerHour.Should().Be(9125, "5 h weigh 5 / 8: 0.625 x 14 000 + 0.375 x 1000");
        published.CommonP25.Should().Be(7875, "0.625 x 12 000 + 0.375 x 1000");
        published.CommonP75.Should().Be(10_375);
    }

    [Test]
    public void LateGame_FlooredHeadline_StaysWithinItsPercentiles()
    {
        var builds = Builds(Enumerable.Range(1, 50).Select(i => Obs(i, ScoreGear(10 * i), 1, i > 45 ? 100 * (i - 45) : 1000)).ToArray());
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        var published = GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution, cohort, null, Options), Options);

        published.CommonCoinsPerHour.Should().Be(1000, "the cohort is below the typical player");
        published.CommonP25.Should().BeLessThanOrEqualTo(1000);
        published.CommonP75.Should().Be(1000);
    }
}
