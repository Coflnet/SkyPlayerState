using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression (audit S7, 2026-11-27): the global tier always weighed the formula rate as 2 pseudo-hours. With no priced
/// formula drop (Lapis Zombie, Spring Critters) or a stale seed (Ruby: 600 Rough at 2.5 coins) the formula rate is about 0
/// and pulled thin tasks toward 0; a high seed pulled others up. <see cref="TaskEstimator.PriorHours"/> now weighs the
/// formula by how well it agrees with the data (continuous in |ln(rF/rG)|, 0 at a 4x ratio) or by how little data there
/// is, whichever is more.
/// </summary>
public class FormulaPriorTests
{
    [SetUp]
    public void ScheduledChangesOn() => ScheduledChanges.SetNowForTest(ScheduledChanges.FormulaPriorAgreementFrom);

    [TearDown]
    public void ScheduledChangesReset() => ScheduledChanges.SetNowForTest(null);

    private static double Estimate(double hG, double rG, double rF) => TaskEstimator.ShrinkTowardFormula(hG, rG, rF, informative: true);

    [Test]
    public void NoPricedFormulaDrop_GivesNoWeight()
    {
        TaskEstimator.PriorHours(1, 8e6, 0, informative: false).Should().Be(0);
        TaskEstimator.ShrinkTowardFormula(1, 8e6, 0, informative: false).Should().Be(8e6, "one hour of data is the whole rate");
    }

    [TestCase(10e6)]
    [TestCase(-9e6)] // Fossil Excavation: a loss agrees with a loss
    public void AgreeingFormula_KeepsTheFullTwoHours(double rate)
        => TaskEstimator.PriorHours(40, rate, rate, informative: true).Should().Be(2);

    [TestCase(2.0, 1.0)]  // |ln 2| / ln 4 = 0.5
    [TestCase(0.5, 1.0)]
    [TestCase(4.0, 0.0)]
    [TestCase(0.25, 0.0)]
    [TestCase(10.0, 0.0)]
    public void AgreementWeight_FallsWithTheLogRatio(double ratio, double expected)
        => TaskEstimator.PriorHours(40, 10e6, ratio * 10e6, informative: true).Should().BeApproximately(expected, 1e-9);

    [TestCase(0, 2)]
    [TestCase(0.5, 1.5)]
    [TestCase(1, 1)]
    [TestCase(2, 0)]
    [TestCase(40, 0)]
    public void DisagreeingFormula_FadesOutOverTheFirstTwoHours(double hG, double expected)
    {
        // a high seed: formula 5x the community rate, beyond the agreement range
        TaskEstimator.PriorHours(hG, 5e6, 25e6, informative: true).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void TheLargerOfAgreementAndFade_Counts()
        => TaskEstimator.PriorHours(1, 10e6, 20e6, informative: true).Should().BeApproximately(1, 1e-9, "fade 1, agreement 1");

    [TestCase(0.5, 1.5)]
    [TestCase(1, 1.0)]
    [TestCase(40, 0.0)]
    public void OppositeSigns_OnlyFade(double hG, double expected)
        => TaskEstimator.PriorHours(hG, 5e6, -5e6, informative: true).Should().BeApproximately(expected, 1e-9);

    [Test]
    public void StaleSeed_HasNoWeightOnceThereIsData()
    {
        // Ruby: 600 Rough an hour at 2.5 coins against a 6M/h community rate
        Estimate(3, 6e6, 1_500).Should().Be(6e6);
        // before: (3 * 6M + 2 * 1.5k) / 5 = 3.6M
    }

    [Test]
    public void NoDataAndNoWeight_FallsBackToTheFormulaRate()
    {
        TaskEstimator.ShrinkTowardFormula(0, 0, 3e6, informative: false).Should().Be(3e6);
        Estimate(0, 0, 3e6).Should().Be(3e6);
    }

    /// <summary>Review regression: the old 0.5x/2x bounds and the 5% cut made the estimate jump.</summary>
    [TestCase(0.5)]
    [TestCase(2.0)]
    [TestCase(0.05)]
    [TestCase(0.25)]
    [TestCase(4.0)]
    public void Estimate_IsContinuousAtTheOldThresholds(double ratio)
    {
        foreach (var hG in new[] { 1.5, 3, 10 })
        {
            var below = Estimate(hG, 10e6, ratio * 10e6 * (1 - 1e-6));
            var above = Estimate(hG, 10e6, ratio * 10e6 * (1 + 1e-6));
            Math.Abs(above - below).Should().BeLessThan(100, $"hG {hG}, ratio {ratio}");
        }
    }

    /// <summary>
    /// Lowering rF never raises the estimate while the fade dominates (up to 1 hour of data). Beyond that a weight that
    /// reaches 0 at a 4x disagreement cannot be monotone: a formula far below the data has no pull, a closer one some.
    /// </summary>
    [TestCase(0.25)]
    [TestCase(0.5)]
    [TestCase(1.0)]
    public void Estimate_DoesNotRiseWhenTheFormulaFalls_WhileTheFadeDominates(double hG)
    {
        var previous = double.PositiveInfinity;
        for (var rF = 40e6; rF > -40e6; rF -= 0.1e6)
        {
            var estimate = Estimate(hG, 10e6, rF);
            estimate.Should().BeLessThanOrEqualTo(previous + 1e-6, $"rF {rF}");
            previous = estimate;
        }
    }

    [Test]
    public void PricedFormulaDrop_IsInformative()
    {
        var ruby = new RubyMiningTask();
        TaskEstimator.HasPricedFormulaDrop(ruby, new() { ["ROUGH_RUBY_GEM"] = 2.5 }).Should().BeTrue();
        TaskEstimator.HasPricedFormulaDrop(ruby, new Dictionary<string, double>()).Should().BeFalse("no drop has a price");
        TaskEstimator.HasPricedFormulaDrop(ruby, null).Should().BeFalse();
    }

    [Test]
    public void TaskWithoutFormulaDrops_IsNotInformative()
        => TaskEstimator.HasPricedFormulaDrop(new LapisZombieHuntingTask(), new() { ["SHARD_LAPIS_ZOMBIE"] = 50_000 }).Should().BeFalse();
}
