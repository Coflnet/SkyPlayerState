using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearLateGameCohortTests
{
    private static readonly GearModelOptions Options = GearModelOptions.Default;

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

        var cohort = GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, Options)!;

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
        var cohort = GearLateGameCohort.Select(Distribution(10, 20, 30, 40, 50, 60, 70, 80, 100), Options)!;

        cohort.Widened.Should().BeTrue();
        cohort.ScoreFrom.Should().Be(80);
        cohort.Players.Should().Be(2);
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

        var cohort = GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, Options)!;

        cohort.Members.Select(m => m.Player).Should().BeEquivalentTo([Player(1), Player(2)], "player 1's second build is not a second player");
        cohort.Members.Single(m => m.Player == Player(1)).CommonPerHour.Should().Be(5000, "(9000 + 1000) coins over 2 hours");
    }

    [Test]
    public void LateGame_PublishedRates_MedianOverPlayers_RarePooled_RoundedBelowFive()
    {
        var builds = Builds(Obs(1, ScoreGear(100), 3, 12_345, rarePerHour: 1000), Obs(2, ScoreGear(90), 1, 23_456),
            Obs(3, ScoreGear(1), 1, 1), Obs(4, ScoreGear(2), 1, 1), Obs(5, ScoreGear(3), 1, 1), Obs(6, ScoreGear(4), 1, 1),
            Obs(7, ScoreGear(5), 1, 1), Obs(8, ScoreGear(6), 1, 1));
        var cohort = GearLateGameCohort.Select(GearScoreDistribution.Of(builds, Options)!, Options)!;

        var published = GearModelPublisher.LateGame(cohort, Options);

        published.Players.Should().Be(2);
        published.PlayersLabel.Should().Be("2+");
        published.CommonCoinsPerHour.Should().Be(18_000, "the median 17900.5, 2 significant digits");
        published.RareCoinsPerHour.Should().Be(750, "3000 rare coins over 4 hours");
        published.Hours.Should().Be(2, "4 hours banded");
        published.CommonP25.Should().BeNull();
        published.Confidence.Should().Be(GearStatistics.Low);
        published.Rounded.Should().BeTrue();
    }
}
