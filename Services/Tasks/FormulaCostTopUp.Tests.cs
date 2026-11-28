using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression (audit S3, 2026-11-27): <see cref="MethodTask.ScaledFormulaCost"/> skipped a formula cost entirely as soon as any
/// negative count of its tag was observed, so one placed Summoning Eye among a dozen dragon fights or a little spent Moondew
/// undercharged the task many times over. Only the part of the formula cost above the observed spend is charged now, except for
/// tasks whose listener books the cost completely (<c>TopsUpObservedCosts</c> false: Ender Dragon).
/// </summary>
public class FormulaCostTopUpTests
{
    [SetUp]
    public void ScheduledChangesOn() => ScheduledChanges.SetNowForTest(ScheduledChanges.FormulaCostTopUpFrom);

    [TearDown]
    public void ScheduledChangesReset() => ScheduledChanges.SetNowForTest(null);

    private const double EyePrice = 1_200_000;
    private const double MoondewPrice = 733_000;
    private const double SunsetPrice = 404_000;
    private const double ShardPrice = 141_000;

    private static double DragonPrice(string tag) => tag == "SUMMONING_EYE" ? EyePrice : tag.EndsWith("_FRAGMENT") ? 6_000 : 0;

    private static double MothPrice(string tag) => tag switch
    {
        "MOONDEW" => MoondewPrice,
        "ENCHANTMENT_ULTIMATE_SUNSET_1" => SunsetPrice,
        "SHARD_LUNAR_MOTH" => ShardPrice,
        _ => 0
    };

    private static double FossilPrice(string tag) => tag == "SUSPICIOUS_SCRAP" ? 74_000 : tag == "ESSENCE_FOSSIL" ? 1_000 : 0;

    // ── the top-up, on Lunar Moth Trapping: 12 Moondew an hour at 4.2 Sunset I books an hour (scale 1) ──

    private static Dictionary<string, double> Moths(double books, double moondew)
        => new() { ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = books, ["SHARD_LUNAR_MOTH"] = books / 0.35, ["MOONDEW"] = moondew };

    [Test]
    public void ObservedBelowTheFormula_ChargesTheDifference()
        => new LunarMothTrappingTask().ScaledFormulaCost(Moths(4.2, -1), 1, MothPrice).Should().BeApproximately(11 * MoondewPrice, 1);

    [Test]
    public void ObservedBelowTheScaledFormula_ChargesTheDifference()
    {
        // two hours at half the ceiling: 12 Moondew expected, 5 of them observed
        new LunarMothTrappingTask().ScaledFormulaCost(Moths(4.2, -5), 2, MothPrice).Should().BeApproximately(7 * MoondewPrice, 1);
    }

    [TestCase(-12)]
    [TestCase(-30)]
    public void ObservedAtOrAboveTheFormula_ChargesNothingExtra(double observed)
        => new LunarMothTrappingTask().ScaledFormulaCost(Moths(4.2, observed), 1, MothPrice).Should().BeApproximately(0, 1e-3);

    // ── Ender Dragon: placed eyes are booked completely (CollectionListener.BookPlacedSummoningEyes), so no top-up ──

    /// <summary>
    /// Review regression: topped up to the formula scaled on YOUNG_FRAGMENT, a 15 minute row with 20 fragments and 3 placed eyes
    /// charged 17 eyes nobody placed (+20.4M against 6k of fragments).
    /// </summary>
    [Test]
    public void EnderDragon_PlacedEyes_AreTheWholeCost()
    {
        var counts = new Dictionary<string, double> { ["YOUNG_FRAGMENT"] = 20, ["SUMMONING_EYE"] = -3 };

        new EnderDragonTask().ScaledFormulaCost(counts, 0.25, DragonPrice).Should().Be(0);
    }

    [Test]
    public void EnderDragon_NoObservedEyes_StillChargesTheWholeFormulaCost()
    {
        var counts = new Dictionary<string, double> { ["YOUNG_FRAGMENT"] = 6 };

        new EnderDragonTask().ScaledFormulaCost(counts, 1, DragonPrice).Should().BeApproximately(6 * EyePrice, 1);
    }

    [Test]
    public void FormulaRate_IsUnchanged()
    {
        var task = new EnderDragonTask();
        var prices = task.FormulaDropsForTest.ToDictionary(d => d.ItemTag, d => DragonPrice(d.ItemTag));
        prices["SUMMONING_EYE"] = EyePrice;
        var drops = task.FormulaDropsForTest.Sum(d => d.RatePerHour * prices[d.ItemTag]);

        TaskEstimator.FormulaRate(task, prices).Should().BeApproximately(drops - 12 * EyePrice, 1);
    }

    // ── Fossil Excavation: MaxCostScale 1 still bounds the top-up ──

    private const double EssenceSeed = FossilExcavationTask.EssencePerScrap * FossilExcavationTask.ScrapsPerHour;

    [Test]
    public void FossilExcavation_TopUpIsBoundedByMaxCostScale()
    {
        // essence at 7.2x the seed, 50 of the at most 120 scraps an hour observed
        var counts = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = EssenceSeed * 7.2, ["SUSPICIOUS_SCRAP"] = -50 };

        var cost = new FossilExcavationTask().ScaledFormulaCost(counts, 1, FossilPrice);

        // 50 scraps buy less than the seeded essence, so all essence above the bound is withheld
        var withheldEssence = (EssenceSeed * 7.2 - EssenceSeed) * 1_000;
        cost.Should().BeApproximately((120 - 50) * 74_000 + withheldEssence, 1);
    }

    /// <summary>Review regression: the withheld essence jumped by 15.6M between 119 and 120 observed scraps (counts are decayed doubles).</summary>
    [TestCase(120)]
    [TestCase(864)]
    public void FossilExcavation_WithheldValueIsContinuous(double scrap)
    {
        double Cost(double observed) => new FossilExcavationTask().ScaledFormulaCost(
            new() { ["ESSENCE_FOSSIL"] = EssenceSeed * 7.2, ["SUSPICIOUS_SCRAP"] = -observed }, 1, FossilPrice);

        Math.Abs(Cost(scrap - 0.01) - Cost(scrap + 0.01)).Should().BeLessThan(5_000);
        Math.Abs(Cost(scrap - 1) - Cost(scrap)).Should().BeLessThan(100_000, "one scrap (74k), or the essence it buys (33k)");
    }

    /// <summary>
    /// Review regression: once 120 scraps were observed the essence at 7.2x the seed counted in full. An observed spend pays
    /// only for the essence it bought: <see cref="FossilExcavationTask.EssencePerScrap"/> per scrap.
    /// </summary>
    [TestCase(120)]
    [TestCase(500)]
    [TestCase(864)]
    [TestCase(1000)]
    public void FossilExcavation_ObservedScrapPaysOnlyForTheEssenceItBought(double scrap)
    {
        var essence = EssenceSeed * 7.2;
        var counts = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = essence, ["SUSPICIOUS_SCRAP"] = -scrap };
        var bought = Math.Max(EssenceSeed, scrap * FossilExcavationTask.EssencePerScrap);

        new FossilExcavationTask().ScaledFormulaCost(counts, 1, FossilPrice)
            .Should().BeApproximately(Math.Max(0, essence - bought) * 1_000, 1);
    }

    /// <summary>
    /// Regression (2026-11-28): with the old seed of 21 essence per scrap, the live community aggregate (35.4 h, 282,532 essence,
    /// 8,414 scraps spent = 33.6 essence per scrap) had 207M of its essence withheld once the top-up starts (2026-12-05): -5.9M/h
    /// on a task whose scrap is all observed. The seed matches the observed payout per scrap now.
    /// </summary>
    [Test]
    public void FossilExcavation_LiveAggregate_KeepsTheEssenceItsObservedScrapBought()
    {
        var counts = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = 282_532, ["SUSPICIOUS_SCRAP"] = -8_414 };

        var withheld = new FossilExcavationTask().ScaledFormulaCost(counts, 35.4, FossilPrice);

        withheld.Should().BeLessThan(0.02 * 282_532 * 1_000, "all scrap is observed, so at most rounding of the per scrap seed is withheld");
    }

    // ── Gem Mixture: no negative counts in its derived counts, so nothing changes ──

    [Test]
    public void GemMixture_DerivedCountsWithoutObservedGems_ChargeTheScaledRecipe()
    {
        // 2.5 mixtures in one hour = 2x the forge capped 1.25/h; 16 fine gems per mixture
        var counts = new Dictionary<string, double> { ["GEMSTONE_MIXTURE"] = 2.5, ["HARD_STONE"] = 500 };

        var cost = new SludgeMiningGemMixtureTask().ScaledFormulaCost(counts, 1, _ => 1_000);

        cost.Should().BeApproximately(16 * 2.5 * 1_000, 1e-6);
    }

    // ── Lunar Moth: one Moondew per moth, Sunset I 35% of them, at most 12 baits an hour ──

    [Test]
    public void LunarMoth_Declaration_MatchesTheTrapCeiling()
    {
        var task = new LunarMothTrappingTask();

        task.FormulaCostsForTest.Should().ContainSingle(c => c.ItemTag == "MOONDEW" && c.RatePerHour == 12,
            "3 traps catching about one pest per 15 minutes each, one bait per moth");
        task.FormulaDropsForTest.Should().Contain(d => d.ItemTag == "ENCHANTMENT_ULTIMATE_SUNSET_1" && d.RatePerHour == 12 * 0.35);
    }

    [Test]
    public void LunarMoth_PartlyObservedBait_ChargesTheMissingMoondew()
    {
        // one hour at the ceiling: 4.2 books, 12 moths, 4 Moondew seen spent (the rest was loaded into the traps earlier)
        var counts = new Dictionary<string, double>
        {
            ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = 4.2, ["SHARD_LUNAR_MOTH"] = 12, ["MOONDEW"] = -4
        };

        var cost = new LunarMothTrappingTask().ScaledFormulaCost(counts, 1, MothPrice);

        cost.Should().BeApproximately(8 * MoondewPrice, 1);
    }

    [Test]
    public void LunarMoth_BooksFarAboveTheTrapCeiling_ChargeAtMostTheCeilingAndWithholdTheExcess()
    {
        // 75 books an hour (18x what 3 traps can produce) with 10 Moondew seen: before the bound this charged 204 Moondew (-150M/h)
        var counts = new Dictionary<string, double>
        {
            ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = 75, ["SHARD_LUNAR_MOTH"] = 20, ["MOONDEW"] = -10
        };

        var cost = new LunarMothTrappingTask().ScaledFormulaCost(counts, 1, MothPrice);

        cost.Should().BeApproximately((12 - 10) * MoondewPrice + (75 - 4.2) * SunsetPrice, 1);
    }

    /// <summary>
    /// Review regression: with 12 Moondew observed (a normal hour at the ceiling) all 70.8 impossible books counted. Observed
    /// Moondew pays only for the books it bought: 0.35 per bait, so 75 books need about 214 Moondew.
    /// </summary>
    [TestCase(10, 75 - 4.2)]
    [TestCase(12, 75 - 4.2)]
    [TestCase(100, 75 - 35)]
    [TestCase(75 / 0.35, 0)]
    [TestCase(300, 0)]
    public void LunarMoth_ObservedMoondewPaysOnlyForTheBooksItBought(double moondew, double withheldBooks)
    {
        var counts = new Dictionary<string, double>
        {
            ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = 75, ["SHARD_LUNAR_MOTH"] = 20, ["MOONDEW"] = -moondew
        };
        var topUp = Math.Max(0, 12 - moondew) * MoondewPrice;

        new LunarMothTrappingTask().ScaledFormulaCost(counts, 1, MothPrice).Should().BeApproximately(topUp + withheldBooks * SunsetPrice, 1);
    }

    /// <summary>Review regression: 11.99 against 12 observed Moondew swung the withheld books by about 28.6M.</summary>
    [TestCase(12)]
    [TestCase(75 / 0.35)]
    public void LunarMoth_WithheldValueIsContinuous(double moondew)
    {
        double Cost(double observed) => new LunarMothTrappingTask().ScaledFormulaCost(
            new() { ["ENCHANTMENT_ULTIMATE_SUNSET_1"] = 75, ["SHARD_LUNAR_MOTH"] = 20, ["MOONDEW"] = -observed }, 1, MothPrice);

        Math.Abs(Cost(moondew - 0.01) - Cost(moondew + 0.01)).Should().BeLessThan(20_000, "0.01 Moondew (7k) and the 0.0035 books it buys");
    }

    // ── Kuudra: its own ScaledFormulaCost, the formula key is never added on top ──

    [Test]
    public void Kuudra_ConfirmedPaidChests_AreNotChargedTheFormulaKeyAgain()
    {
        var t5 = new KuudraT5Task();
        // two paid chests (3.2 teeth each), both confirmed: their coins are in DUNGEON_CHEST_COST already
        var counts = new Dictionary<string, double>
        {
            ["KUUDRA_TEETH"] = 6.4, ["ESSENCE_CRIMSON"] = 4_400,
            [PseudoItems.KUUDRA_PAID_CHEST] = -2, [PseudoItems.DUNGEON_CHEST_COST] = -2 * 2_328_000
        };
        double PriceOf(string tag) => tag switch
        {
            PseudoItems.DUNGEON_CHEST_COST => 1,
            "ENCHANTED_RED_SAND" => 1_000,
            "CORRUPTED_NETHER_STAR" => 50_000,
            _ => 0
        };

        var cost = t5.ScaledFormulaCost(counts, 0.4, PriceOf);

        cost.Should().BeApproximately(2 * (80 * 1_000 + 2 * 50_000), 1, "only the materials of the two confirmed keys");
    }
}
