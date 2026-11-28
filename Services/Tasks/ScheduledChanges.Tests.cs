using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The formula cost top-up (S3) and the agreement weighted formula prior (S7) only start on 2026-12-05 00:00 UTC. Before, the
/// estimator behaves as it did before them; from the instant on, as the new code.
/// </summary>
public class ScheduledChangesTests
{
    private static readonly DateTime Before = ScheduledChanges.FormulaCostTopUpFrom.AddTicks(-1);

    [TearDown]
    public void Reset() => ScheduledChanges.SetNowForTest(null);

    private static double MothPrice(string tag) => tag switch
    {
        "MOONDEW" => 733_000, "ENCHANTMENT_ULTIMATE_SUNSET_1" => 404_000, "SHARD_LUNAR_MOTH" => 141_000, _ => 0
    };

    private static Dictionary<string, double> Moths(double books, double moondew)
        => new() { ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = books, ["SHARD_LUNAR_MOTH"] = books / 0.35, ["MOONDEW"] = moondew };

    [Test]
    public void StartInstants_Are_2026_12_05_Utc()
    {
        var expected = new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);
        ScheduledChanges.FormulaCostTopUpFrom.Should().Be(expected);
        ScheduledChanges.FormulaPriorAgreementFrom.Should().Be(expected);
    }

    [Test]
    public void IsOn_SwitchesAtTheExactStartInstant()
    {
        ScheduledChanges.SetNowForTest(Before);
        ScheduledChanges.IsOn(ScheduledChanges.FormulaCostTopUpFrom).Should().BeFalse();
        ScheduledChanges.SetNowForTest(ScheduledChanges.FormulaCostTopUpFrom);
        ScheduledChanges.IsOn(ScheduledChanges.FormulaCostTopUpFrom).Should().BeTrue();
    }

    [Test]
    public void LunarMoth_BeforeTheStart_ObservedMoondewSkipsTheWholeFormulaCost()
    {
        ScheduledChanges.SetNowForTest(Before);
        // old: any observed spend drops the whole estimate (60 Moondew an hour seed, scale 1 at 21 Sunset books)
        new LunarMothTrappingTask().ScaledFormulaCost(Moths(21, -1), 1, MothPrice).Should().Be(0);
    }

    [Test]
    public void LunarMoth_FromTheStart_ObservedMoondewIsToppedUp()
    {
        ScheduledChanges.SetNowForTest(ScheduledChanges.FormulaCostTopUpFrom);
        new LunarMothTrappingTask().ScaledFormulaCost(Moths(4.2, -1), 1, MothPrice)
            .Should().BeApproximately(11 * 733_000, 1);
    }

    [Test]
    public void LunarMoth_BeforeTheStart_UsesTheOldSeedsAndNoCostBound()
    {
        ScheduledChanges.SetNowForTest(Before);
        var task = new LunarMothTrappingTask();
        task.FormulaCostsForTest.Should().ContainSingle(c => c.ItemTag == "MOONDEW" && c.RatePerHour == 60);
        task.FormulaDropsForTest.Should().Contain(d => d.ItemTag == "ENCHANTMENT_ULTIMATE_SUNSET_1" && Math.Abs(d.RatePerHour - 21) < 1e-9);
        task.FormulaDropsForTest.Should().Contain(d => d.ItemTag == "SHARD_LUNAR_MOTH" && d.RatePerHour == 60);
        // unbounded: 10x the seed charges 10x the 60 Moondew
        task.ScaledFormulaCost(Moths(210, 0), 1, MothPrice).Should().BeApproximately(600 * 733_000, 1);
    }

    [Test]
    public void Fossil_BeforeTheStart_ObservedScrapReplacesTheWholeEstimate()
    {
        ScheduledChanges.SetNowForTest(Before);
        // 7.2x the seed with only a little scrap observed: old code charged neither side's excess
        var cost = new FossilExcavationTask().ScaledFormulaCost(
            new() { ["ESSENCE_FOSSIL"] = 2520 * 7.2, ["SUSPICIOUS_SCRAP"] = -10 }, 1, t => t == "SUSPICIOUS_SCRAP" ? 74_000 : 1_000);
        cost.Should().Be(0);
    }

    [Test]
    public void Prior_BeforeTheStart_IsTheFixedTwoPseudoHours()
    {
        ScheduledChanges.SetNowForTest(Before);
        // new code: 40 hours of data and a formula 10x off weighs 0; old: always 2
        var old = TaskEstimator.ShrinkTowardFormula(40, 10e6, 100e6, informative: true);
        old.Should().BeApproximately((40 * 10e6 + 2 * 100e6) / 42, 1e-3);
        TaskEstimator.ShrinkTowardFormula(1, 8e6, 0, informative: false).Should().BeApproximately(8e6 / 3 , 1e-3, "not informative still weighs 2 hours before the start");
    }

    [Test]
    public void Prior_FromTheStart_UsesTheAgreementWeight()
    {
        ScheduledChanges.SetNowForTest(ScheduledChanges.FormulaPriorAgreementFrom);
        TaskEstimator.ShrinkTowardFormula(40, 10e6, 100e6, informative: true).Should().Be(10e6);
        TaskEstimator.ShrinkTowardFormula(1, 8e6, 0, informative: false).Should().Be(8e6);
    }
}
