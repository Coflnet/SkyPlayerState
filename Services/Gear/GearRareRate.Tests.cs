using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearRareRateTests
{
    private static readonly GearModelOptions Options = GearModelOptions.Default;
    private static readonly GearModelOptions Uncapped = Options with { RareCapPlayers = 0 };
    /// <summary>Both switches off: the late game cohort's own drops, pooled uncapped, as before 2026-11-10.</summary>
    private static readonly GearModelOptions Old = Uncapped with { LateGameRareFromTask = false };

    /// <summary>Player i plays <c>hours[i]</c> at <c>rates[i]</c> rare coins per hour (and 1000 common).</summary>
    private static System.Collections.Generic.List<GearPlayerBuild> RareBuilds(double[] hours, double[] rates)
        => Builds(hours.Select((h, i) => Obs(i + 1, ScoreGear(10 * (i + 1)), h, 1000, rarePerHour: rates[i])).ToArray());

    /// <summary>Regression: one lucky drop was pooled in full into the task's rare rate (F7 late game 7.5 -> 32.3M/h).</summary>
    [Test]
    public void OneLuckyDrop_CountsAtMostAtTheRateThreePlayersReach()
    {
        var builds = RareBuilds([2, 1, 1, 1, 1, 1], [150_000_000, 2_000_000, 1_000_000, 500_000, 0, 0]);

        var rate = GearRareRate.Of(builds, Options);

        rate.PlayerCap.Should().Be(1_000_000, "the third highest per-player rare rate");
        rate.PerHour(builds).Should().BeApproximately((2_000_000 + 1_000_000 + 1_000_000 + 500_000) / 7.0, 1e-6,
            "players 1 and 2 count at 1M/h of their hours");
        GearRareRate.Of(builds, Uncapped).PerHour(builds).Should().BeApproximately(303_500_000 / 7.0, 1e-6, "the switch off pools all");
    }

    [Test]
    public void FewerThanThreePlayersWithRareDrops_GiveNoRareRate()
    {
        var builds = RareBuilds([1, 1, 1, 1], [300_000_000, 5_000_000, 0, 0]);

        GearRareRate.Of(builds, Options).PerHour(builds).Should().Be(0);
    }

    [Test]
    public void SteadyRareIncome_IsKept()
    {
        var even = RareBuilds([1, 2, 3, 4, 5], [1000, 1000, 1000, 1000, 1000]);
        var grinder = RareBuilds([40, 1, 2, 3], [1000, 1100, 1200, 900]);

        GearRareRate.Of(even, Options).PerHour(even).Should().BeApproximately(1000, 1e-9);
        var pooled = GearRareRate.Of(grinder, Uncapped).PerHour(grinder);
        GearRareRate.Of(grinder, Options).PerHour(grinder).Should().BeApproximately(pooled, pooled * 0.02,
            "a long grinder's steady drops are not cut");
    }

    /// <summary>
    /// Players 1 .. 6 earn 1000 common and <paramref name="lowRare"/>; the cohort (7 .. 9, 3 h) <paramref name="cohortCommon"/>
    /// common and <paramref name="cohortRare"/>, player 9 <paramref name="luckyRare"/> instead.
    /// </summary>
    private static (GearScoreDistribution, GearCohort) LateGameCase(double lowRare, double cohortRare, double luckyRare, double cohortCommon = 10_000)
    {
        var builds = Builds(Enumerable.Range(1, 9).Select(i => Obs(i, ScoreGear(10 * i), 1, i > 6 ? cohortCommon : 1000,
            rarePerHour: i == 9 ? luckyRare : i > 6 ? cohortRare : lowRare)).ToArray());
        var distribution = GearScoreDistribution.Of(builds, Options)!;
        return (distribution, GearLateGameCohort.Select(distribution, Options)!);
    }

    private static Models.GearLateGame Published(GearScoreDistribution distribution, GearCohort cohort, GearModelOptions options)
        => GearModelPublisher.LateGame(cohort, GearLateGamePrior.Of(distribution, cohort, null, options), options);

    /// <summary>Regression: a cohort player's lucky drop moved the whole late game rate (F7 rare 31.3M/h, M5 1.1 -> 9.4).</summary>
    [Test]
    public void LateGame_OneLuckyDrop_DoesNotJumpByTheDrop()
    {
        var (distribution, cohort) = LateGameCase(1000, 0, 30_000_000);

        var published = Published(distribution, cohort, Options);
        var ownDrops = Published(distribution, cohort, Options with { LateGameRareFromTask = false });
        var old = Published(distribution, cohort, Old);

        cohort.Players.Should().Be(3);
        // cap 1000 / h: the task's rare 7000 / 9 h = 778; the cohort's common 10 000 over the task's pooled 4000 = 2.5
        published.RareCoinsPerHour.Should().Be(1400, "(2.5 x 778 + 778) / 2, shrunk halfway (3 h), 2 significant digits");
        ownDrops.RareCoinsPerHour.Should().Be(560, "the cohort's own capped drops: (1000 / 3 + 778) / 2");
        old.RareCoinsPerHour.Should().Be(6_700_000, "both switches off: (10M + 3.33M) / 2");
        published.CommonCoinsPerHour.Should().Be(old.CommonCoinsPerHour, "only the rare part changes");
    }

    [Test]
    public void LateGame_SteadyRareIncome_IsKept()
    {
        var (distribution, cohort) = LateGameCase(2000, 2000, 2000, cohortCommon: 1000);

        Published(distribution, cohort, Options).RareCoinsPerHour.Should().Be(2000);
        Published(distribution, cohort, Old).RareCoinsPerHour.Should().Be(2000);
    }

    /// <summary>
    /// Regression (review 2026-11-10): three short lucky sessions set the per-player cap above a cohort player's 300M drop,
    /// which then went into the cohort's rate uncapped at a weight of 0.8 (F7 about 31M/h).
    /// </summary>
    // TODO: the expected 7.7M is pending the shared rare-pool rule (task-rate aggregates and gear model): player 11's 300M
    // drop under the third highest cap still lifts the task's rare rate (Floor.Rare) about 5x.
    [Test]
    public void LateGame_ADropUnderTheCap_IsBoundedByTheTasksRate()
    {
        // players 1 .. 3: 15 minutes with a 25M drop each (100M / h); 4 .. 8: 10 h without; the cohort 9 .. 11: 4 h at 1500
        // common, player 11 with a 300M drop (75M / h, under the cap)
        var inputs = Enumerable.Range(1, 3).Select(i => Obs(i, ScoreGear(i), 0.25, 1000, rarePerHour: 100_000_000))
            .Concat(Enumerable.Range(4, 5).Select(i => Obs(i, ScoreGear(i), 10, 1000)))
            .Concat(Enumerable.Range(9, 3).Select(i => Obs(i, ScoreGear(10 * i), 4, 1500, rarePerHour: i == 11 ? 75_000_000 : 0)))
            .ToArray();
        var distribution = GearScoreDistribution.Of(Builds(inputs), Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;
        var prior = GearLateGamePrior.Of(distribution, cohort, null, Options);

        var published = Published(distribution, cohort, Options);

        cohort.Members.Select(m => m.Player).Should().BeEquivalentTo([Player(9), Player(10), Player(11)]);
        prior.RareRate.PlayerCap.Should().Be(100_000_000, "player 11 is not capped");
        prior.Floor.Rare.Should().BeApproximately(375_000_000 / 62.75, 1, "the task's rare rate");
        prior.PooledCommon.Should().BeApproximately(68_750 / 62.75, 1e-6);
        published.RareCoinsPerHour.Should().BeLessThanOrEqualTo(1.5 * prior.Floor.Rare, "the cohort's 1500 common over the task's pooled 1096");
        published.RareCoinsPerHour.Should().Be(7_700_000, "0.8 x 1.37 x 5.98M + 0.2 x 5.98M, 2 significant digits");
        Published(distribution, cohort, Options with { LateGameRareFromTask = false }).RareCoinsPerHour
            .Should().Be(21_000_000, "the cohort's own drops: 0.8 x 25M + 0.2 x 5.98M");
    }

    /// <summary>
    /// Regression (review 2026-11-10): the scale compared the cohort's common rate with the per-player median while the rare
    /// rate is pooled over hours, so a cohort holding most of the hours counted its advantage twice (8400 published, 3000 earned).
    /// </summary>
    [Test]
    public void LateGame_ACohortHoldingMostHours_IsNotScaledTwice()
    {
        var inputs = Enumerable.Range(1, 9)
            .Select(i => Obs(i, ScoreGear(10 * i), i > 6 ? 30 : 1, i > 6 ? 3000 : 1000, rarePerHour: i > 6 ? 3000 : 1000)).ToArray();
        var distribution = GearScoreDistribution.Of(Builds(inputs), Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        var published = Published(distribution, cohort, Options);

        cohort.Players.Should().Be(3);
        published.RareCoinsPerHour.Should().Be(3000, "the task's 2875 x 3000 / 2875, shrunk with 90 / 93 toward 2875: 2996");
    }

    /// <summary>Regression (review 2026-11-10): many tiny sessions pulled the per-player median down and the scale to its 3x clamp (2700 published).</summary>
    [Test]
    public void LateGame_ManyTinySessions_DoNotScaleTheRareRateUp()
    {
        var tiny = Enumerable.Range(1, 12).Select(i => Obs(i, ScoreGear(i), 0.02, 50));
        var steady = Enumerable.Range(13, 9).Select(i => Obs(i, ScoreGear(10 * i), 5, 1000, rarePerHour: 1000));
        var distribution = GearScoreDistribution.Of(Builds(tiny.Concat(steady).ToArray()), Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;

        Published(distribution, cohort, Options).RareCoinsPerHour.Should().BeApproximately(1000, 10, "the cohort earns the task's pooled rates");
    }

    /// <summary>A task without a positive pooled common rate scales the rare rate by 1, never by NaN.</summary>
    [Test]
    public void LateGame_NoPooledCommonRate_ScalesByOne()
    {
        var distribution = GearScoreDistribution.Of(Builds(Enumerable.Range(1, 9)
            .Select(i => Obs(i, ScoreGear(10 * i), 1, 0, rarePerHour: 1000)).ToArray()), Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;
        var flat = new GearRatePrior(1000, 1000);

        Published(distribution, cohort, Options).RareCoinsPerHour.Should().Be(1000, "0 / 0 common");
        new GearLateGamePrior(flat, flat).RareScale(5000, Options).Should().Be(1, "a hand made prior has no pooled rate");
    }

    /// <summary>Regression: the pooled rare rate was added flat to every curve point (Voidgloom T4's curve 2 -> 15M/h, 14.6 rare).</summary>
    [Test]
    public void Curve_OneLuckyDrop_IsNotAddedToEveryPoint()
    {
        var inputs = Enumerable.Range(1, 10)
            .Select(i => Obs(i, ScoreGear(100 + 20 * i), 1, 1000 + 300 * i, rarePerHour: i == 10 ? 1_000_000_000 : 0)).ToArray();

        var capped = GearModelService.Compute(Wheat, inputs, Now).Curve!;
        var uncapped = GearModelService.Compute(Wheat, inputs, Now, Uncapped).Curve!;

        capped.RareCoinsPerHour.Should().Be(0, "one player's drop");
        capped.Points.Should().OnlyContain(p => p.CoinsPerHour == p.CommonCoinsPerHour);
        uncapped.RareCoinsPerHour.Should().Be(100_000_000, "the switch off: 1B over 10 h");
    }
}
