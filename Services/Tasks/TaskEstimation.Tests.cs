using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskEstimationTests
{
    [Test]
    public void EstimateTaskName_MatchesClassifierAndAggregateKey()
    {
        TaskEstimator.GetTaskName(new LotusAtollTask()).Should().Be("Lotus Atoll");
    }

    [Test]
    public void RegistryLooksUpMethodTaskByClassifierName()
    {
        new TaskRegistry().GetByName("Lotus Atoll").Should().BeOfType<LotusAtollTask>();
    }

    /// <summary>
    /// TaskPeriodFolder.FoldDerivedTasks finds derived tasks via
    /// registry.MethodTasks.Where(t => t.DerivedFromForTest == primaryTask) - pin that lookup so a
    /// future registry/task change can't silently stop the Gem Mixture variant from ever getting
    /// its own community/personal data folded (see TaskClassifier's derived-task tests too).
    /// </summary>
    [Test]
    public void Registry_FindsGemMixtureAsDerivedFromSludgeMining()
    {
        var registry = new TaskRegistry();
        var derived = registry.MethodTasks.Where(t => t.DerivedFromForTest == "Sludge Mining").ToList();
        derived.Should().ContainSingle().Which.Should().BeOfType<SludgeMiningGemMixtureTask>();
    }

    [Test]
    public async Task PersonalStats_EmptyUuidDoesNotQueryCassandra()
    {
        var queried = false;

        var stats = await TaskEstimator.LoadPlayerStats(Guid.Empty, _ =>
        {
            queried = true;
            return Task.FromResult(new List<TaskPlayerStatRow>());
        }, TimeSpan.FromMilliseconds(20), NullLogger.Instance);

        queried.Should().BeFalse();
        stats.Should().BeEmpty();
    }

    [Test]
    public async Task PersonalStats_TimeoutFallsBackToCommunityEstimate()
    {
        var neverCompletes = new TaskCompletionSource<List<TaskPlayerStatRow>>();
        var stopwatch = Stopwatch.StartNew();

        var stats = await TaskEstimator.LoadPlayerStats(Guid.NewGuid(), _ => neverCompletes.Task,
            TimeSpan.FromMilliseconds(20), NullLogger.Instance);

        stats.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // ── Confidence ramp: 5min => 50%, 30min => 100% ──

    [TestCase(0, 0)]
    [TestCase(1.5, 0)]
    [TestCase(2, 0.2)]
    [TestCase(5, 0.5)]
    [TestCase(17.5, 0.75)]
    [TestCase(30, 1.0)]
    [TestCase(60, 1.0)]
    public void Ramp_MatchesSpec(double minutes, double expected)
    {
        TaskPeriodFolder.Ramp(minutes).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void Ramp_IsMonotonic()
    {
        double prev = -1;
        for (double m = 0; m <= 40; m += 0.5)
        {
            var w = TaskPeriodFolder.Ramp(m);
            w.Should().BeGreaterThanOrEqualTo(prev);
            w.Should().BeInRange(0, 1);
            prev = w;
        }
    }

    // ── Stat bucketing ──

    [TestCase(0.0, (byte)0)]
    [TestCase(0.32, (byte)0)]
    [TestCase(0.34, (byte)1)]
    [TestCase(0.5, (byte)1)]
    [TestCase(0.65, (byte)1)]
    [TestCase(0.67, (byte)2)]
    [TestCase(1.0, (byte)2)]
    public void ScoreToBucket_SplitsInThirds(double score, byte expected)
    {
        StatScoreService.ScoreToBucket(score).Should().Be(expected);
    }

    // ── Aggregate rate: items re-priced live + price drift scaling of pools ──

    [Test]
    public void AggregateRate_RepricesItemsLive()
    {
        var agg = new BucketAggregate
        {
            WSeconds = 3600, // one hour of data
            ItemCounts = new() { { "RAW_FISH", 1000 } },
            RefItemValue = 1000 * 5,
        };
        var prices = new Dictionary<string, double> { { "RAW_FISH", 10 } }; // price doubled since tracking
        // 1000 fish/hour at live price 10 = 10_000/h
        RateFromAggregate(agg, prices).Should().BeApproximately(10_000, 1e-6);
    }

    [Test]
    public void AggregateRate_ScalesCoinPoolsByPriceDrift()
    {
        var agg = new BucketAggregate
        {
            WSeconds = 3600,
            ItemCounts = new() { { "RAW_FISH", 1000 } },
            RefItemValue = 1000 * 5, // items were worth 5k at fold
            RareCoins = 1000,        // a rare drop pool worth 1000 coins at fold
        };
        // live item value doubled => drift scale 2 => rare pool becomes 2000
        var prices = new Dictionary<string, double> { { "RAW_FISH", 10 } };
        // (1000*10 + 2000) / 1h
        RateFromAggregate(agg, prices).Should().BeApproximately(12_000, 1e-6);
    }

    [Test]
    public void PriceDrift_IsClamped()
    {
        var agg = new BucketAggregate
        {
            WSeconds = 3600,
            ItemCounts = new() { { "X", 100 } },
            RefItemValue = 1, // absurdly low ref => huge raw ratio
            RareCoins = 100,
        };
        var prices = new Dictionary<string, double> { { "X", 100 } }; // live value 10_000, ratio 10_000 -> clamp 4
        // items 100*100=10000 + rare 100*4=400 => 10400
        RateFromAggregate(agg, prices).Should().BeApproximately(10_400, 1e-6);
    }

    // Mirror of TaskEstimator.RateFromAggregate for isolated math testing.
    private static double RateFromAggregate(BucketAggregate agg, Dictionary<string, double> prices)
    {
        double liveItemValue = 0;
        foreach (var (tag, count) in agg.ItemCounts)
            liveItemValue += (prices.GetValueOrDefault(tag)) * count;
        var driftScale = agg.RefItemValue > 0 && liveItemValue > 0
            ? Math.Clamp(liveItemValue / agg.RefItemValue, 0.25, 4)
            : 1;
        var pools = (agg.ResidualCoins + agg.RareCoins) * driftScale;
        return (liveItemValue + pools) / (agg.WSeconds / 3600.0);
    }

    // ── Saturation penalty ──

    [TestCase(0, 1.0)]
    [TestCase(1, 0.99)]
    [TestCase(10, 0.9043820750088)]
    [TestCase(50, 0.75)]  // 0.99^50 ~ 0.605, floored to 0.75
    [TestCase(200, 0.75)]
    public void Saturation_OnePercentPerUser_Floored(int doers, double expected)
    {
        var saturation = Math.Max(Math.Pow(0.99, doers), 0.75);
        saturation.Should().BeApproximately(expected, 1e-9);
    }

    // ── Shrinkage chain: empty system falls back to formula, gated by contributors ──

    [Test]
    public void EmptySystem_FallsBackToStatAdjustedFormula()
    {
        // no community data: rBucketStar = (0 + s_b * m_b * rGlobalStar) / (0 + s_b) = m_b * rGlobalStar
        // rGlobalStar = (0 + s_g * rFormula) / s_g = rFormula
        double rFormula = 1000, mB = 1.35;
        double hG = 0, rG = 0, hB = 0, rB = 0;
        var rGlobalStar = (hG * rG + 2 * rFormula) / (hG + 2);
        var rBucketStar = (hB * rB + 4 * mB * rGlobalStar) / (hB + 4);
        rGlobalStar.Should().BeApproximately(1000, 1e-9);
        rBucketStar.Should().BeApproximately(1350, 1e-9);
    }

    [Test]
    public void FewContributors_UseFormulaDespiteHours()
    {
        // with < 3 contributors the community rate must not enter the chain,
        // so hG/hB stay 0 and the result is m_b * rFormula regardless of accumulated hours.
        // This mirrors the gating in TaskEstimator (contributors >= 3).
        int contributors = 2;
        double rFormula = 500, mB = 1.0;
        double hG = contributors >= 3 ? 100 : 0;
        double rG = contributors >= 3 ? 9999 : 0;
        var rGlobalStar = (hG * rG + 2 * rFormula) / (hG + 2);
        rGlobalStar.Should().BeApproximately(500, 1e-9, "2 contributors are ignored, so the whale rate 9999 never applies");
        var rBucketStar = (0 * 0 + 4 * mB * rGlobalStar) / (0 + 4);
        rBucketStar.Should().BeApproximately(500, 1e-9);
    }

    // ── Effects-seeded prior multiplier differentiates buckets ──

    [Test]
    public void EffectPrior_DifferentiatesBucketsWithNoData()
    {
        var multipliers = new List<double> { 1.3, 1.2, 1.15 };
        double[] midpoints = { 1.0 / 6, 0.5, 5.0 / 6 };
        double M(double e) => multipliers.Aggregate(1.0, (acc, m) => acc * (1 + (m - 1) * e));
        var values = new[] { M(midpoints[0]), M(midpoints[1]), M(midpoints[2]) };
        var mean = (values[0] + values[1] + values[2]) / 3;
        var mLow = values[0] / mean;
        var mHigh = values[2] / mean;
        mLow.Should().BeLessThan(1);
        mHigh.Should().BeGreaterThan(1);
        (mHigh / mLow).Should().BeGreaterThan(1.4, "a high stat player should see a materially higher prior");
    }

    // ── Ingredient costs (MethodTask.FormulaCosts / ScaledFormulaCost) must be netted out
    // everywhere a derived task's converted output is priced, not just in ComputeFromFormula -
    // otherwise "Sludge Mining (Gem Mixture)" looks more profitable than plain Sludge Mining even
    // when the 16 fine gems it consumes per mixture cost more than the mixture (net of the 320
    // Sludge Juice that mixture consumed) is worth. ──

    private static readonly Dictionary<string, double> ExpensiveGemsPrices = new()
    {
        { "SLUDGE_JUICE", 20 },
        { "GEMSTONE_MIXTURE", 500_000 },
        { "FINE_JADE_GEM", 50_000 },
        { "FINE_AMBER_GEM", 50_000 },
        { "FINE_AMETHYST_GEM", 50_000 },
        { "FINE_SAPPHIRE_GEM", 50_000 },
    };

    /// <summary>Mirror of TaskEstimator.FormulaRate (private) for isolated testing via the public MethodTask API it now delegates cost-netting to.</summary>
    private static double FormulaRate(MethodTask task, Dictionary<string, double> prices)
    {
        double total = 0;
        var dropRates = new Dictionary<string, double>();
        foreach (var drop in task.FormulaDropsForTest)
        {
            var price = prices.GetValueOrDefault(drop.ItemTag);
            if (price > 0)
                total += drop.RatePerHour * price;
            dropRates[drop.ItemTag] = dropRates.GetValueOrDefault(drop.ItemTag) + drop.RatePerHour;
        }
        var cost = task.ScaledFormulaCost(dropRates, 1.0, tag => prices.GetValueOrDefault(tag));
        return total - cost;
    }

    [Test]
    public void FormulaEstimate_GemMixture_IsLowerThanPlainSludgeMining_WhenGemsAreExpensive()
    {
        var sludgeMining = new SludgeMiningTask();
        var gemMixture = new SludgeMiningGemMixtureTask();

        var sludgeRate = FormulaRate(sludgeMining, ExpensiveGemsPrices);
        var gemMixtureRate = FormulaRate(gemMixture, ExpensiveGemsPrices);

        gemMixtureRate.Should().BeLessThan(sludgeRate,
            "16 expensive fine gems per mixture must be charged against the mixture's formula estimate, " +
            "not silently dropped the way it would be if only ComputeFromFormula netted FormulaCosts");
    }

    [Test]
    public void FoldedValue_GemMixture_IsLowerThanPlainSludgeMining_WhenGemsAreExpensive()
    {
        // Mirror of the relevant slice of TaskPeriodFolder.FoldDerivedTasks (gross value minus
        // MethodTask.ScaledFormulaCost) - TaskPeriodFolder itself needs a live Cassandra-backed
        // TaskAggregateService/CoinValueRegistry, so this pins the same computation via the public
        // MethodTask API it calls into instead of standing up the full service.
        var gemMixture = new SludgeMiningGemMixtureTask();
        const double hours = 4;
        const double observedJuicePerHour = 400; // exactly saturates the 1.25 mixtures/h Forge cap
        var primaryCounts = new Dictionary<string, double> { { "SLUDGE_JUICE", observedJuicePerHour * hours } };

        var derivedCounts = gemMixture.ConvertDerivedCountsForTest(primaryCounts, hours);
        derivedCounts.Should().NotBeNull();
        var derivedGrossValue = derivedCounts!.Sum(c => ExpensiveGemsPrices.GetValueOrDefault(c.Key) * c.Value);
        var derivedCost = gemMixture.ScaledFormulaCost(derivedCounts, hours, tag => ExpensiveGemsPrices.GetValueOrDefault(tag));
        var netFoldedValue = derivedGrossValue - derivedCost;

        // What plain Sludge Mining would have folded for the same observed juice: the raw juice sold
        // directly, no ingredient cost involved.
        var sludgeMiningFoldedValue = primaryCounts["SLUDGE_JUICE"] * ExpensiveGemsPrices["SLUDGE_JUICE"];

        netFoldedValue.Should().BeLessThan(sludgeMiningFoldedValue,
            "converting to Gemstone Mixture at these gem prices is a net loss versus just selling the raw Sludge Juice");
    }
}
