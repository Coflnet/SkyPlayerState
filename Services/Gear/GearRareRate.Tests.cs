using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearRareRateTests
{
    /// <summary>The threshold set explicitly: several tests have players of exactly 1 h, which then set the cap.</summary>
    private static readonly GearModelOptions Options = GearModelOptions.Default with { RareCapMinHours = 1 };
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
    /// Players 1 .. 3: 15 minutes with a 25M drop each (100M / h); 4 .. 8: 10 h at 1000 common, 4 .. 6 with 1M / h steady rare
    /// drops; the cohort 9 .. 11: 4 h at 1500 common, player 11 with <paramref name="luckyDrop"/> rare coins. 62.75 h in all.
    /// </summary>
    private static GearObservationInput[] DropUnderTheCap(double luckyDrop)
        => Enumerable.Range(1, 3).Select(i => Obs(i, ScoreGear(i), 0.25, 1000, rarePerHour: 100_000_000))
            .Concat(Enumerable.Range(4, 5).Select(i => Obs(i, ScoreGear(i), 10, 1000, rarePerHour: i <= 6 ? 1_000_000 : 0)))
            .Concat(Enumerable.Range(9, 3).Select(i => Obs(i, ScoreGear(10 * i), 4, 1500, rarePerHour: i == 11 ? luckyDrop / 4 : 0)))
            .ToArray();

    private static GearLateGamePrior Prior(GearObservationInput[] inputs, GearModelOptions options)
    {
        var distribution = GearScoreDistribution.Of(Builds(inputs), options)!;
        return GearLateGamePrior.Of(distribution, GearLateGameCohort.Select(distribution, options), null, options);
    }

    /// <summary>
    /// Regression (review 2026-11-10): three short lucky sessions set the per-player cap (100M / h) above a long player's
    /// 300M drop (75M / h), which then lifted the task's rare rate about 5x and the late game to 7.7M rare / h. Only players
    /// of at least 1 h set the cap now: the third highest of them is the steady 1M / h.
    /// </summary>
    [Test]
    public void LateGame_ADropUnderTheCap_IsBoundedByTheTasksRate()
    {
        var inputs = DropUnderTheCap(300_000_000);
        var distribution = GearScoreDistribution.Of(Builds(inputs), Options)!;
        var cohort = GearLateGameCohort.Select(distribution, Options)!;
        var prior = GearLateGamePrior.Of(distribution, cohort, null, Options);
        var old = Options with { RareCapMinHours = 0 };

        var published = Published(distribution, cohort, Options);

        cohort.Members.Select(m => m.Player).Should().BeEquivalentTo([Player(9), Player(10), Player(11)]);
        prior.RareRate.PlayerCap.Should().Be(1_000_000, "the third highest rate of the players with at least 1 h");
        prior.Floor.Rare.Should().BeApproximately((3 * 250_000 + 30_000_000 + 4_000_000) / 62.75, 1,
            "players 1 .. 3 and 11 count at 1M / h of their hours");
        published.RareCoinsPerHour.Should().Be(720_000, "0.8 x 1.37 x 554k + 0.2 x 554k, 2 significant digits");
        Prior(inputs, old).RareRate.PlayerCap.Should().Be(100_000_000, "the switch off: the short sessions set the cap");
        Published(distribution, cohort, old).RareCoinsPerHour.Should().Be(8_400_000, "the switch off: 0.8 x 1.37 x 6.45M + 0.2 x 6.45M");
    }

    /// <summary>
    /// Fewer than k qualifying players with rare drops give no gear rare rate, however many short players have drops; a
    /// qualifying player without drops counts toward k at 0.
    /// </summary>
    [Test]
    public void FewerThanThreeQualifyingPlayersWithDrops_GiveNoRareRate()
    {
        var builds = RareBuilds([5, 5, .. Enumerable.Repeat(0.5, 10)], [2_000_000, 2_000_000, .. Enumerable.Repeat(10_000_000.0, 10)]);
        var withAThirdLong = RareBuilds([5, 5, 5, .. Enumerable.Repeat(0.5, 10)], [2_000_000, 2_000_000, 0, .. Enumerable.Repeat(10_000_000.0, 10)]);

        GearRareRate.Of(builds, Options).PerHour(builds).Should().Be(0, "two players of at least 1 h, k is 3");
        GearRareRate.Of(withAThirdLong, Options).PlayerCap.Should().Be(0, "the third long player, without drops, is the third highest");
        var old = GearRareRate.Of(builds, Options with { RareCapMinHours = 0 });
        old.PlayerCap.Should().Be(10_000_000, "the switch off: the short sessions set the cap");
        old.PerHour(builds).Should().BeApproximately((20_000_000 + 50_000_000) / 15.0, 1e-6);
    }

    [Test]
    public void RareCapMinHours_APlayerJustUnderTheThreshold_DoesNotSetTheCap_OneAtIt_Does()
    {
        GearRareRate Cap(double hours) => GearRareRate.Of(RareBuilds([5, 5, hours], [2_000_000, 1_000_000, 3_000_000]), Options);

        Cap(0.99).PlayerCap.Should().Be(0, "two qualifying players");
        Cap(1).PlayerCap.Should().Be(1_000_000, "the third highest of three qualifying players");
        var twoShortBuilds = Builds(Obs(1, ScoreGear(10), 5, 1000, rarePerHour: 2_000_000), Obs(2, ScoreGear(20), 5, 1000, rarePerHour: 1_000_000),
            Obs(3, ScoreGear(30), 0.6, 1000, rarePerHour: 3_000_000), Obs(3, ScoreGear(31), 0.6, 1000, rarePerHour: 3_000_000));
        GearRareRate.Of(twoShortBuilds, Options).PlayerCap.Should().Be(1_000_000, "per player: player 3's two builds of 0.6 h sum to 1.2 h");
    }

    /// <summary>One long player's lucky drop, under or far above the cap, moves the task's gear rare rate only a little.</summary>
    [Test]
    public void OneLongPlayersLuckyDrop_MovesTheTasksRareRateLittle()
    {
        var without = Prior(DropUnderTheCap(0), Options).Floor.Rare;

        Prior(DropUnderTheCap(300_000_000), Options).Floor.Rare.Should().BeLessThan(1.15 * without);
        Prior(DropUnderTheCap(3_000_000_000), Options).Floor.Rare.Should().BeLessThan(1.15 * without);
        Prior(DropUnderTheCap(300_000_000), Options with { RareCapMinHours = 0 }).Floor.Rare.Should().BeGreaterThan(10 * without,
            "the switch off: the drop counts in full under the short sessions' cap");
    }

    /// <summary>The task estimates (/Task/estimates) keep counting every rare drop: the gear limit is not applied there.</summary>
    [Test]
    public void TaskEstimate_KeepsTheLuckyDropInFull()
    {
        var inputs = DropUnderTheCap(300_000_000);
        var rareCoins = inputs.Sum(i => i.Observation.RareCoins);
        var hours = inputs.Sum(i => i.Hours);
        var aggregate = new Tasks.BucketAggregate { TaskName = Wheat, WSeconds = hours * 3600, RareCoins = rareCoins };

        var parts = Tasks.TaskEstimator.RateParts(null!, aggregate, new());

        parts.ScaledRareCoins.Should().Be(rareCoins, "75M of the short sessions, 30M steady and the 300M drop");
        parts.Rate.Should().BeApproximately(405_000_000 / 62.75, 1);
        Prior(inputs, Options).Floor.Rare.Should().BeLessThan(parts.Rate / 10, "the gear evaluation limits the same drops");
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
