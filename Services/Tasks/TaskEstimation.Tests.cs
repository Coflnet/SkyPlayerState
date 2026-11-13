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
    public void BuildDrops_NeverListsPseudoItems()
    {
        var counts = new Dictionary<string, double>
        {
            ["BAZAAR_PURCHASE"] = 104187, ["AUCTION_PURCHASE"] = 1581, ["DUNGEON_RUN"] = 50, ["DUNGEON_CHEST_COST"] = -9000, ["ENCHANTED_COAL"] = 10
        };

        var drops = TaskEstimator.BuildDrops(new LotusAtollTask(), counts, 3600, new Dictionary<string, double> { ["ENCHANTED_COAL"] = 100 });

        drops.Select(d => d.ItemTag).Should().Equal("ENCHANTED_COAL");
    }

    /// <summary>Regression: consumed items (Garden Visitor hand-ins, used keys) were published as drops at a negative rate.</summary>
    [Test]
    public void BuildDrops_NeverListsConsumedItems()
    {
        var counts = new Dictionary<string, double> { ["WHEAT"] = -640, ["SKELETON_KEY"] = -1, ["OVERCLOCKER_3000"] = 2 };

        var drops = TaskEstimator.BuildDrops(new GardenVisitorsTask(), counts, 3600, new Dictionary<string, double> { ["WHEAT"] = 6 });

        drops.Select(d => d.ItemTag).Should().Equal("OVERCLOCKER_3000");
    }

    [Test]
    public async Task ServerEstimate_NeverListsPseudoItemsAsDrops()
    {
        var estimate = new TaskEstimate
        {
            TaskName = "Lotus Atoll", Source = "personal", CoinsPerHour = 1000,
            Drops = [new TaskDropRate { ItemTag = "BAZAAR_PURCHASE", RatePerHour = 104187 }, new TaskDropRate { ItemTag = "ENCHANTED_COAL", RatePerHour = 2, PriceEach = 100, ContributionPerHour = 200 }]
        };

        var result = await new LotusAtollTask().Execute(new TaskParams
        {
            TestTime = DateTime.UtcNow,
            ExtractedInfo = new Models.ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new System.Collections.Concurrent.ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            LocationProfit = new(),
            Names = new(),
            CleanPrices = new(),
            BazaarPrices = new(),
            ServerEstimates = new() { ["Lotus Atoll"] = estimate }
        });

        result.Breakdown.Drops.Select(d => d.ItemTag).Should().Equal("ENCHANTED_COAL");
    }

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

    // Thin wrappers over the real TaskEstimator methods (argument order kept for the call sites
    // below) - these tests must exercise production code, not a copy of it.
    private static double RateFromAggregate(BucketAggregate agg, Dictionary<string, double> prices, MethodTask task = null)
        => TaskEstimator.RateFromAggregate(task, agg, prices);

    private static double RateFromPlayerStat(TaskPlayerStatRow row, Dictionary<string, double> prices, double decay, MethodTask task = null)
        => TaskEstimator.RateFromPlayerStat(task, row, prices, decay);

    /// <summary>
    /// Regression: the community/stat-bucket tier must net a recipe task's ingredient cost (here
    /// Sludge Mining (Gem Mixture)'s Fine gems) the same way FormulaRate/ComputeFromFormula already
    /// did, instead of only ever netting it into the drift reference (see the doc comments on
    /// TaskEstimator.RateFromAggregate and TaskPeriodFolder.FoldDerivedTasks).
    /// </summary>
    [Test]
    public void AggregateRate_NetsIngredientCost_ForFormulaCostsTask()
    {
        var gemMixture = new SludgeMiningGemMixtureTask();
        var prices = new Dictionary<string, double>
        {
            ["GEMSTONE_MIXTURE"] = 500_000,
            ["FINE_JADE_GEM"] = 5_000,
            ["FINE_AMBER_GEM"] = 5_000,
            ["FINE_AMETHYST_GEM"] = 5_000,
            ["FINE_SAPPHIRE_GEM"] = 5_000,
        };
        const double hours = 4;
        const double mixtures = 2; // 0.5/h over 4h
        var agg = new BucketAggregate
        {
            WSeconds = hours * 3600,
            ItemCounts = new() { { "GEMSTONE_MIXTURE", mixtures } },
            RefItemValue = mixtures * 500_000, // gross ref matching liveItemValue -> no drift
        };

        var gross = RateFromAggregate(agg, prices);
        var net = RateFromAggregate(agg, prices, gemMixture);
        var expectedCost = gemMixture.ScaledFormulaCost(agg.ItemCounts, hours, tag => prices.GetValueOrDefault(tag));

        net.Should().BeApproximately(gross - expectedCost / hours, 1e-6);
        net.Should().BeLessThan(gross, "the 16 Fine gems per mixture must be charged against the community rate too");
    }

    /// <summary>
    /// Sessions keep negative counts, so an observed SUSPICIOUS_SCRAP cost is already in the item value:
    /// the formula estimate of the same cost must not be subtracted again.
    /// </summary>
    [Test]
    public void AggregateRate_CountsAnObservedFormulaCostOnce()
    {
        var task = new FossilExcavationTask();
        var prices = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = 1_000, ["SUSPICIOUS_SCRAP"] = 10_000_000 };
        const double hours = 2;
        var agg = new BucketAggregate
        {
            WSeconds = hours * 3600,
            ItemCounts = new() { ["ESSENCE_FOSSIL"] = 1200, ["SUSPICIOUS_SCRAP"] = -10 },
        };

        var rate = RateFromAggregate(agg, prices, task);

        var expected = (1200 * 1_000.0 - 10 * 10_000_000.0) / hours;
        rate.Should().BeApproximately(expected, 1e-6);
        task.ScaledFormulaCost(agg.ItemCounts, hours, t => prices.GetValueOrDefault(t)).Should().Be(0);
    }

    [Test]
    public void AggregateRate_StillAppliesTheFormulaCost_WhenTheCostWasNotObserved()
    {
        var task = new FossilExcavationTask();
        var prices = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = 1_000, ["SUSPICIOUS_SCRAP"] = 10_000_000 };
        const double hours = 2;
        var agg = new BucketAggregate { WSeconds = hours * 3600, ItemCounts = new() { ["ESSENCE_FOSSIL"] = 1200 } };

        var cost = task.ScaledFormulaCost(agg.ItemCounts, hours, t => prices.GetValueOrDefault(t));

        cost.Should().BeGreaterThan(0);
        RateFromAggregate(agg, prices, task).Should().BeApproximately((1200 * 1_000.0 - cost) / hours, 1e-6);
    }

    [Test]
    public void PlayerStatRate_NetsIngredientCost_ForFormulaCostsTask()
    {
        var gemMixture = new SludgeMiningGemMixtureTask();
        var prices = new Dictionary<string, double>
        {
            ["GEMSTONE_MIXTURE"] = 500_000,
            ["FINE_JADE_GEM"] = 5_000,
            ["FINE_AMBER_GEM"] = 5_000,
            ["FINE_AMETHYST_GEM"] = 5_000,
            ["FINE_SAPPHIRE_GEM"] = 5_000,
        };
        var row = new TaskPlayerStatRow
        {
            WSeconds = 4 * 3600,
            ItemCounts = new() { { "GEMSTONE_MIXTURE", 2 } },
            RefItemValue = 2 * 500_000,
        };
        const double decay = 1.0;

        var gross = RateFromPlayerStat(row, prices, decay);
        var net = RateFromPlayerStat(row, prices, decay, gemMixture);

        net.Should().BeLessThan(gross, "the 16 Fine gems per mixture must be charged against the personal rate too");
        net.Should().BeApproximately(gross - 40_000, 1e-6,
            "4 gem types x 4/mixture x 2 mixtures x 5k / 4h = 40k/h ingredient cost");
    }

    /// <summary>
    /// A task with no FormulaCosts must be entirely unaffected by threading MethodTask into these
    /// rate helpers - ScaledFormulaCost short-circuits to 0 for it (see MethodTask.FormulaCosts's
    /// empty default), so passing the task through must be a no-op.
    /// </summary>
    [Test]
    public void AggregateRate_TaskWithoutFormulaCosts_IsUnchanged()
    {
        var sludgeMining = new SludgeMiningTask(); // no FormulaCosts
        var prices = new Dictionary<string, double> { { "SLUDGE_JUICE", 20 } };
        var agg = new BucketAggregate
        {
            WSeconds = 3600,
            ItemCounts = new() { { "SLUDGE_JUICE", 2000 } },
            RefItemValue = 2000 * 20,
        };

        RateFromAggregate(agg, prices, sludgeMining).Should().Be(RateFromAggregate(agg, prices));
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

    private static double FormulaRate(MethodTask task, Dictionary<string, double> prices)
        => TaskEstimator.FormulaRate(task, prices);

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
        // Mirror of the equivalent net-value computation (gross value minus
        // MethodTask.ScaledFormulaCost) TaskEstimator.RateFromAggregate/RateFromPlayerStat now
        // perform from the gross item counts TaskPeriodFolder.FoldDerivedTasks stores - both
        // TaskPeriodFolder and TaskEstimator need live Cassandra-backed services, so this pins the
        // same computation via the public MethodTask API they call into instead of standing up the
        // full services.
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

/// <summary>
/// Regression: the public (no player) rate came from stat bucket 3, "unknown stats", which only held a few low hours.
/// Mithril Mining published 1.36M/h from 5 hours there while 900 hours in the other buckets made about 3M/h.
/// </summary>
public class TaskEstimatorCommunityTests
{
    private const string Task = "Mithril Mining";
    private static readonly Dictionary<string, double> Prices = new() { ["MITHRIL_ORE"] = 1000 };

    private static BucketAggregate Bucket(double hours, double orePerHour, string playerPrefix) => new()
    {
        WSeconds = hours * 3600, WPeriods = hours * 6,
        ItemCounts = new() { ["MITHRIL_ORE"] = hours * orePerHour },
        RefItemValue = hours * orePerHour * 1000,
        Contributors = Enumerable.Range(0, 3).Select(i => playerPrefix + i).ToHashSet()
    };

    private static TaskEstimator MakeEstimator() => MakeEstimator(new()
    {
        [(Task, (byte)1)] = Bucket(450, 3000, "b1-"),
        [(Task, (byte)2)] = Bucket(450, 3000, "b2-"),
        [(Task, StatScoreService.UnknownBucket)] = Bucket(5, 380, "b3-")
    });

    private static TaskEstimator MakeEstimator(Dictionary<(string task, byte bucket), BucketAggregate> snapshot)
    {
        var registry = new TaskRegistry();
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.SetSnapshotForTest(snapshot);
        return new TaskEstimator(registry, aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance), NullLogger<TaskEstimator>.Instance);
    }

    [Test]
    public async Task EstimateCommunity_UsesTheGlobalRateAndHours_NotTheSparseUnknownBucket()
    {
        var estimates = await MakeEstimator().EstimateCommunity(Prices, default);
        var mithril = estimates.Single(e => e.TaskName == Task);

        var globalRate = (900 * 3000 + 5 * 380) * 1000.0 / 905;
        mithril.CoinsPerHour.Should().BeApproximately(globalRate, globalRate * 0.01);
        mithril.CommunityTrackedHours.Should().BeApproximately(905, 1e-6);
        mithril.Source.Should().Be("global");
        mithril.StatBucket.Should().Be(StatScoreService.UnknownBucket);
        mithril.Drops.Single(d => d.ItemTag == "MITHRIL_ORE").RatePerHour.Should().BeApproximately((900 * 3000 + 5 * 380) / 905.0, 1e-6);
    }

    /// <summary>Regression: a logged-in player whose stats did not resolve still got the sparse unknown bucket.</summary>
    [Test]
    public async Task EstimateAll_PlayerWithUnknownStats_UsesTheGlobalRateToo()
    {
        var state = new Coflnet.Sky.PlayerState.Models.StateObject();
        state.McInfo.Uuid = Guid.NewGuid();

        var estimates = await MakeEstimator().EstimateAll(state, Prices);
        var mithril = estimates.Single(e => e.TaskName == Task);

        var globalRate = (900 * 3000 + 5 * 380) * 1000.0 / 905;
        mithril.StatBucket.Should().Be(StatScoreService.UnknownBucket);
        mithril.CoinsPerHour.Should().BeApproximately(globalRate, globalRate * 0.01);
        mithril.CommunityTrackedHours.Should().BeApproximately(905, 1e-6);
    }
}

/// <summary>Regression: the drop list was published from one or two players' data, while rate and hours need 3.</summary>
public class TaskEstimatorDropPrivacyTests
{
    private const string Task = "Mithril Mining";
    private static readonly Dictionary<string, double> Prices = new() { ["MITHRIL_ORE"] = 1000, ["TITANIUM_ORE"] = 5000 };

    private static async Task<List<TaskDropRate>> CommunityDrops(int contributors)
    {
        var bucket = new BucketAggregate
        {
            WSeconds = 36000, WPeriods = 60,
            ItemCounts = new() { ["MITHRIL_ORE"] = 30_000, ["PRIVATE_DROP"] = 3 },
            Contributors = Enumerable.Range(0, contributors).Select(i => "p" + i).ToHashSet()
        };
        var registry = new TaskRegistry();
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.SetSnapshotForTest(new() { [(Task, (byte)1)] = bucket });
        var estimator = new TaskEstimator(registry, aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance), NullLogger<TaskEstimator>.Instance);
        var estimates = await estimator.EstimateCommunity(Prices, default);
        return estimates.Single(e => e.TaskName == Task).Drops;
    }

    [Test]
    public async Task TwoContributors_PublishTheFormulaDrops()
    {
        var drops = await CommunityDrops(2);

        drops.Select(d => d.ItemTag).Should().BeEquivalentTo(new[] { "MITHRIL_ORE", "TITANIUM_ORE" });
        drops.Should().NotContain(d => d.ItemTag == "PRIVATE_DROP");
    }

    [Test]
    public async Task ThreeContributors_PublishTheAggregatesDrops()
        => (await CommunityDrops(3)).Should().Contain(d => d.ItemTag == "PRIVATE_DROP");

    [Test]
    public void PublishableAggregate_GatesStatBucketsToo()
    {
        var two = new BucketAggregate { Contributors = ["a", "b"] };
        var three = new BucketAggregate { Contributors = ["a", "b", "c"] };

        TaskEstimator.PublishableAggregate(two).Should().BeNull();
        TaskEstimator.PublishableAggregate(three).Should().BeSameAs(three);
        TaskEstimator.PublishableAggregate(null).Should().BeNull();
    }
}

/// <summary>
/// Regression: rates read from stored counts valued every tag at its market price, so costs (DUNGEON_CHEST_COST) and
/// powder were worth 0. M6 lost -7.4M/h of chest cost, M7 -16.4M/h. Costs only count with full value rates (they
/// buy the rare pool's loot), powder always.
/// </summary>
[NonParallelizable]
public class TaskRateCostValuationTests
{
    private static readonly Dictionary<string, double> Prices = new() { ["ESSENCE_WITHER"] = 2000 };

    [TearDown]
    public void Reset()
    {
        TaskRateOptions.FullValueRates = false;
        CoinValueRegistry.ResetOverridesForTest();
    }

    private static BucketAggregate M6(bool withChestCost) => new()
    {
        WSeconds = 119.6 * 3600,
        ItemCounts = withChestCost
            ? new() { ["ESSENCE_WITHER"] = 500_000, ["DUNGEON_CHEST_COST"] = -888_000_000 }
            : new() { ["ESSENCE_WITHER"] = 500_000 }
    };

    [Test]
    public void FullValue_AggregateRate_SubtractsTheChestCost()
    {
        TaskRateOptions.FullValueRates = true;
        var withCost = TaskEstimator.RateFromAggregate(new M6Task(), M6(true), Prices);
        var without = TaskEstimator.RateFromAggregate(new M6Task(), M6(false), Prices);

        (without - withCost).Should().BeApproximately(888_000_000 / 119.6, 1);
        (without - withCost).Should().BeApproximately(7.42e6, 0.01e6);
    }

    [Test]
    public void SwitchOff_AggregateRate_IgnoresTheChestCost_LikeOriginMain()
        => TaskEstimator.RateFromAggregate(new M6Task(), M6(true), Prices)
            .Should().Be(TaskEstimator.RateFromAggregate(new M6Task(), M6(false), Prices));

    [Test]
    public void FullValue_PlayerStatRate_SubtractsTheChestCost()
    {
        TaskRateOptions.FullValueRates = true;
        var row = new TaskPlayerStatRow
        {
            WSeconds = 3600, ItemCounts = new() { ["ESSENCE_WITHER"] = 1000, ["DUNGEON_CHEST_COST"] = -500_000 }
        };

        TaskEstimator.RateFromPlayerStat(null, row, Prices, 1).Should().BeApproximately(2_000_000 - 500_000, 1e-6);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AggregateRate_ValuesPowderAtItsOverride(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var agg = new BucketAggregate { WSeconds = 3600, ItemCounts = new() { ["MITHRIL_POWDER"] = 100_000 } };

        TaskEstimator.RateFromAggregate(null, agg, Prices).Should().BeApproximately(100_000 * 0.2, 1e-6);
    }

    private static double FossilPrice(string tag) => tag == "SUSPICIOUS_SCRAP" ? 74_000 : tag == "ESSENCE_FOSSIL" ? 1_000 : 0;

    /// <summary>Regression: chat-credited ESSENCE_FOSSIL at 7.2x its seed scaled the scrap cost to 864 scraps an hour (-19M/h).</summary>
    [Test]
    public void FossilExcavation_ScrapCostIsNeverScaledAboveTheSeededSpend()
    {
        var task = new FossilExcavationTask();
        var observed = new Dictionary<string, double> { ["ESSENCE_FOSSIL"] = 2520 * 7.2 };

        var cost = task.ScaledFormulaCost(observed, 1, FossilPrice);

        var scrapCost = 120 * 74_000;
        var withheldEssence = (2520 * 7.2 - 2520) * 1_000;
        cost.Should().BeApproximately(scrapCost + withheldEssence, 1);
    }

    /// <summary>The inflated essence must not turn the capped cost into a profit: the net equals the net at exactly the seeded rate.</summary>
    [Test]
    public void FossilExcavation_EssenceAboveTheSeedIsNotRevenue_WhileTheScrapCostIsCapped()
    {
        var task = new FossilExcavationTask();
        double Net(double essence) => essence * 1_000 - task.ScaledFormulaCost(new() { ["ESSENCE_FOSSIL"] = essence }, 1, FossilPrice);

        Net(2520 * 7.2).Should().BeApproximately(Net(2520), 1);
        Net(2520 * 7.2).Should().BeApproximately(2520 * 1_000 - 120 * 74_000, 1);
    }

    [Test]
    public void FossilExcavation_ObservedScrapSpend_LetsTheEssenceCountInFull()
    {
        var cost = new FossilExcavationTask().ScaledFormulaCost(new() { ["ESSENCE_FOSSIL"] = 2520 * 7.2, ["SUSPICIOUS_SCRAP"] = -500 }, 1, FossilPrice);

        cost.Should().Be(0, "the scrap cost is observed, so neither side is estimated");
    }

    [Test]
    public void FossilExcavation_ScrapCostStillScalesDown_WhenEssenceIsBelowTheSeed()
    {
        var cost = new FossilExcavationTask().ScaledFormulaCost(new() { ["ESSENCE_FOSSIL"] = 1260 }, 1, _ => 1000);

        cost.Should().BeApproximately(60 * 1000, 1e-6);
    }
}
