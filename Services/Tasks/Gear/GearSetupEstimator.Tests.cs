using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

public class GearSetupEstimatorTests
{
    private const double C = 1_000_000;
    private static readonly TaskRegistry Registry = new();

    private static TaskEstimate Community(string task, double coinsPerHour, int contributors = 5, string source = "global") =>
        new() { TaskName = task, CoinsPerHour = coinsPerHour, Contributors = contributors, Source = source };

    private static ResolvedGearProfile Resolve(GearProfileCatalog catalog, string task)
    {
        catalog.TryGet(task, out var bound).Should().BeTrue();
        return GearBindingResolver.Resolve(bound.Profile, bound.Binding);
    }

    private static TaskGearSetups Estimate(string task, RevenueShares shares, TaskEstimate? community = null,
        Func<string, TaskEstimate?>? estimateOf = null, IReadOnlyDictionary<string, double>? prices = null) =>
        GearSetupEstimator.Estimate(new GearEstimateInput(task, Resolve(GearFixtures.All(), task),
            community ?? Community(task, C), shares, estimateOf ?? (_ => null), prices));

    private static GearSetupEstimate Setup(TaskGearSetups result, string id) =>
        id == "baseline" ? result.Baseline : result.Setups.Single(s => s.Id == id);

    // ── scaled models ──

    [Test]
    public void AnchorSetup_EarnsTheCommunityRate()
    {
        var result = Estimate("Wheat Farming", new RevenueShares(0.5, 0.3, 0.2, true));

        Setup(result, "budget").Multiplier!.Value.Should().BeApproximately(1, 1e-12);
        Setup(result, "budget").CoinsPerHour!.Value.Should().BeApproximately(C, 1e-6);
        result.Anchor.Should().Be("budget");
    }

    [Test]
    public void Multiplier_WeighsEachComponentByItsRevenueShare()
    {
        // Wheat fixture: budget FF 100 MF 0 (anchor), endgame FF 200 MF 20; fortune model throughput is 1
        var result = Estimate("Wheat Farming", new RevenueShares(0.5, 0.3, 0.2, true));

        var expected = 0.5 * (3.0 / 2) + 0.3 * 1.2 + 0.2 * 1;
        Setup(result, "endgame").Multiplier!.Value.Should().BeApproximately(expected, 1e-12);
        Setup(result, "endgame").CoinsPerHour!.Value.Should().BeApproximately(C * expected, 1e-6);
    }

    [Test]
    public void Delta_IsTheRateAboveTheBaseline()
    {
        var result = Estimate("Wheat Farming", RevenueShares.Unknown);

        // baseline FF 50 -> 1.5/2 = 0.75 of the anchor, mid FF 150 -> 2.5/2 = 1.25
        result.BaselineCoinsPerHour!.Value.Should().BeApproximately(0.75 * C, 1e-6);
        result.Baseline.DeltaCoinsPerHour.Should().Be(0);
        Setup(result, "budget").DeltaCoinsPerHour!.Value.Should().BeApproximately(0.25 * C, 1e-6);
        Setup(result, "mid").DeltaCoinsPerHour!.Value.Should().BeApproximately(0.5 * C, 1e-6);
        result.Setups.Select(s => s.Tier).Should().Equal(GearTiers.Upgrades);
    }

    [Test]
    public void Status_IsBaselineOrUpgradeForScaledModels()
    {
        var result = Estimate("Wheat Farming", RevenueShares.Unknown);

        result.Baseline.Status.Should().Be(GearSetupStatus.Baseline);
        result.Setups.Should().OnlyContain(s => s.Status == GearSetupStatus.Upgrade);
    }

    [Test]
    public void TaskName_IsTheCanonicalNameGiven()
    {
        var result = GearSetupEstimator.Estimate(new GearEstimateInput("Wheat Farming",
            Resolve(GearFixtures.All(), "wheat farming"), Community("Wheat Farming", C), RevenueShares.Unknown, _ => null, null));

        result.TaskName.Should().Be("Wheat Farming");
    }

    [Test]
    public void ZeroCommunityRate_GivesNoNumbersAndLowConfidence()
    {
        var result = Estimate("Wheat Farming", RevenueShares.Unknown, Community("Wheat Farming", 0));

        result.BaselineCoinsPerHour.Should().BeNull();
        result.Setups.Should().OnlyContain(s => s.CoinsPerHour == null && s.DeltaCoinsPerHour == null
                                                && s.Multiplier == null && s.Confidence == "low");
        result.Setups.Should().OnlyContain(s => s.Assumptions.Any(a => a.Contains("no community coins/hour estimate")));
        result.Source.Should().Be("global");
    }

    [Test]
    public void MissingCommunityEstimate_GivesNoNumbers()
    {
        var result = GearSetupEstimator.Estimate(new GearEstimateInput("Wheat Farming", Resolve(GearFixtures.All(), "Wheat Farming"),
            null, RevenueShares.Unknown, _ => null, null));

        result.CommunityCoinsPerHour.Should().Be(0);
        result.Source.Should().Be("none");
        result.Setups.Should().OnlyContain(s => s.CoinsPerHour == null && s.Confidence == "low");
    }

    [Test]
    public void NoNetRevenue_GivesNoNumbers()
    {
        var result = Estimate("Wheat Farming", RevenueShares.NoNetRevenue);

        result.BaselineCoinsPerHour.Should().BeNull();
        result.Setups.Should().OnlyContain(s => s.CoinsPerHour == null && s.DeltaCoinsPerHour == null && s.Confidence == "low");
        result.Setups.Should().OnlyContain(s => s.Assumptions.Any(a => a.Contains("nets no coins")));
    }

    [Test]
    public void ExtremeMultiplier_IsClampedAndMarkedLow()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["setups"]![2]!["stats"]!["Farming Fortune"] = 5000; // endgame: 51x the anchor's drops
        var catalog = GearFixtures.Catalog(profile.ToJsonString());

        var result = GearSetupEstimator.Estimate(new GearEstimateInput("Wheat Farming", Resolve(catalog, "Wheat Farming"),
            Community("Wheat Farming", C), RevenueShares.Unknown, _ => null, null));

        var endgame = Setup(result, "endgame");
        endgame.Multiplier.Should().Be(GearSetupEstimator.MaxMultiplier);
        endgame.CoinsPerHour.Should().Be(C * GearSetupEstimator.MaxMultiplier);
        endgame.Confidence.Should().Be("low");
        endgame.Assumptions.Should().Contain(a => a.Contains("clamped"));
        Setup(result, "mid").Assumptions.Should().NotContain(a => a.Contains("clamped"));
    }

    [Test]
    public void ProfileMaxMultiplier_RaisesTheClampButCapsConfidence()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["maxMultiplier"] = 20;
        profile["setups"]![2]!["stats"]!["Farming Fortune"] = 1000; // endgame: 11/2 = 5.5x the anchor's drops
        var catalog = GearFixtures.Catalog(profile.ToJsonString());

        var result = GearSetupEstimator.Estimate(new GearEstimateInput("Wheat Farming", Resolve(catalog, "Wheat Farming"),
            Community("Wheat Farming", C), RevenueShares.Unknown, _ => null, null));

        var endgame = Setup(result, "endgame");
        endgame.Multiplier!.Value.Should().BeApproximately(5.5, 1e-12, "the profile allows up to 20x");
        endgame.Assumptions.Should().NotContain(a => a.Contains("clamped"));
        endgame.Assumptions.Should().Contain(a => a.Contains("very large effect"));
        result.Setups.Should().OnlyContain(s => s.Confidence == "medium", "the fortune model is high, the raised clamp caps it");
    }

    [TestCase("Wheat Farming", 5, "global", "high")]    // fortune with a formulaSource
    [TestCase("Wheat Farming", 1, "stat_bucket", "medium")]
    [TestCase("Wheat Farming", 0, "formula", "low")]
    [TestCase("Coal Mining", 5, "global", "medium")]    // mining without a formulaSource
    [TestCase("F7", 5, "global", "medium")]             // runTime
    [TestCase("Water Fishing", 5, "global", "medium")]  // sourced formula, but the profile caps modelConfidence
    [TestCase("Diana", 5, "global", "low")]             // fixed
    public void Confidence_IsTheLowerOfModelAndData(string task, int contributors, string source, string expected)
    {
        var result = Estimate(task, RevenueShares.Unknown, Community(task, C, contributors, source));

        Setup(result, "mid").Confidence.Should().Be(expected);
    }

    [Test]
    public void StatCaps_ApplyBeforeTheModelButNotToTheShownStats()
    {
        // fishing fixture caps Fishing Speed at 150: endgame's 200 counts as 150
        var result = Estimate("Water Fishing", new RevenueShares(0, 0, 1, true));

        double Catches(double fs, double scc) => 3600 / (20 / (1 + fs / 100) + 1 + scc / 100 * 2);
        Setup(result, "endgame").Multiplier!.Value.Should().BeApproximately(Catches(150, 40) / Catches(50, 20), 1e-9);
        Setup(result, "endgame").Stats["Fishing Speed"].Should().Be(200);
    }

    [Test]
    public void UntradeableItems_AreFlaggedAndUnpriced()
    {
        var prices = new Dictionary<string, double> { ["SYNTHETIC_SOULBOUND_PET"] = 5000 };

        var pet = Setup(Estimate("Water Fishing", RevenueShares.Unknown, prices: prices), "mid").Items.Single(i => i.Slot == "pet");

        pet.Tradeable.Should().BeFalse();
        pet.EstimatedPrice.Should().BeNull();
        Setup(Estimate("Water Fishing", RevenueShares.Unknown), "mid").Items.Single(i => i.Slot == "tool").Tradeable.Should().BeTrue();
    }

    [Test]
    public void Items_CarryPricesExceptForFilteredVariants()
    {
        var prices = new Dictionary<string, double> { ["SYNTHETIC_CARROT_TOOL"] = 100, ["SYNTHETIC_CARROT_TOOL_ENDGAME"] = 999 };

        var result = Estimate("Carrot Farming", RevenueShares.Unknown, prices: prices);

        Setup(result, "mid").Items.Single(i => i.Slot == "tool").EstimatedPrice.Should().Be(100);
        var endgameTool = Setup(result, "endgame").Items.Single(i => i.Slot == "tool");
        endgameTool.EstimatedPrice.Should().BeNull("variant priced items are priced by SkyApi with their filters");
        endgameTool.PriceFilters.Should().ContainKey("Rarity");
        Setup(result, "mid").Items.Single(i => i.Slot == "helmet").EstimatedPrice.Should().BeNull("no price known");
    }

    [Test]
    public void Sources_MergeProfileAndSetupSources()
    {
        Setup(Estimate("Wheat Farming", RevenueShares.Unknown), "mid").Sources
            .Should().Equal("https://example.com/synthetic-test-fixture");
    }

    // ── non finite values never reach the response ──

    private static ResolvedGearProfile WithSetupStats(string task, string setupId, string stat, double value)
    {
        var resolved = Resolve(GearFixtures.All(), task);
        var setups = resolved.Setups.Select(s => s.Id != setupId ? s
            : s with { Stats = new Dictionary<string, double>(s.Stats) { [stat] = value } }).ToList();
        return resolved with { Setups = setups };
    }

    private static ResolvedGearProfile WithMultipliers(string json)
    {
        var resolved = Resolve(GearFixtures.All(), "Diana");
        var spec = JsonSerializer.Deserialize<RateModelSpec>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return resolved with { Model = spec };
    }

    [Test]
    public void NonFiniteResults_BecomeNullAndSerialize()
    {
        // validation rejects both inputs; built directly here to prove the estimator's own guard
        var nanStat = WithSetupStats("Wheat Farming", "endgame", "Farming Fortune", double.NaN);
        var infiniteRatio = WithMultipliers("""{ "kind": "fixed", "multipliers": { "baseline": 1, "budget": 1e-320, "mid": 1, "endgame": 1e308 } }""");

        foreach (var (task, profile) in new[] { ("Wheat Farming", nanStat), ("Diana", infiniteRatio) })
        {
            var result = GearSetupEstimator.Estimate(new GearEstimateInput(task, profile, Community(task, C),
                new RevenueShares(0.5, 0.5, 0, true), _ => null, null));

            Setup(result, "endgame").CoinsPerHour.Should().BeNull($"{task} endgame has no finite rate");
            Setup(result, "endgame").Confidence.Should().Be("low");
            var serialize = () => JsonSerializer.Serialize(result); // default options throw on NaN and Infinity
            serialize.Should().NotThrow();
        }
    }

    // ── revenue shares (net of costs) ──

    /// <summary>
    /// Regression: shares were fractions of GROSS revenue but applied to the NET community rate, so a large recipe
    /// cost inflated every upgrade. Bulk 10, rare 1, recipe cost 9 (C = 2), fortune x1.5: true M is 1.25, not 1.45.
    /// </summary>
    [Test]
    public void LargeRecipeCost_ScalesWithBulk()
    {
        var shares = RevenueShares.FromComponents(bulkItems: 10, rareItems: 1, rarePool: 0, residualPool: 0, formulaCost: 9, itemCosts: 0);

        var m = GearSetupEstimator.RawMultiplier(shares, new RateFactors(1, 1.5, 1), RateFactors.Neutral);

        m.Should().BeApproximately((10 * 1.5 + 1 - 9 * 1.5) / 2, 1e-12);
        m.Should().BeApproximately(1.25, 1e-12);
        (shares.Bulk + shares.Rare + shares.Throughput).Should().BeApproximately(1, 1e-12);
    }

    [Test]
    public void Shares_AreSignedFractionsOfNetRevenue()
    {
        var aggregate = new BucketAggregate
        {
            WSeconds = 3600, RareCoins = 100, ResidualCoins = 200,
            ItemCounts = new() { ["COMMON"] = 10, ["RARE"] = 1, ["KEY"] = -1 }
        };
        var prices = new Dictionary<string, double> { ["COMMON"] = 50, ["RARE"] = 200, ["KEY"] = 400 };

        var shares = RevenueShares.FromAggregate(null, aggregate, prices, ["RARE"]);

        // net = 500 common + (200 rare item + 100 pool) + (200 residual - 400 key) = 600; the key scales with throughput
        shares.Bulk.Should().BeApproximately(500.0 / 600, 1e-12);
        shares.Rare.Should().BeApproximately(300.0 / 600, 1e-12);
        shares.Throughput.Should().BeApproximately(-200.0 / 600, 1e-12);
        shares.NetPositive.Should().BeTrue();
        // net matches the aggregate's own rate, so the anchor earns exactly the community rate
        (TaskEstimator.RateParts(null!, aggregate, prices).Rate).Should().BeApproximately(600, 1e-9);
    }

    [Test]
    public void Shares_WithoutDataOrNetRevenue()
    {
        RevenueShares.FromAggregate(null, null, null, null).Should().Be(RevenueShares.Unknown);
        RevenueShares.FromAggregate(null, new BucketAggregate { WSeconds = 0 }, null, null).Should().Be(RevenueShares.Unknown);
        RevenueShares.FromAggregate(null, new BucketAggregate { WSeconds = 60 }, null, null).Should().Be(RevenueShares.Unknown);
        RevenueShares.FromAggregate(null, new BucketAggregate { WSeconds = 60, ResidualCoins = -5 }, null, null)
            .Should().Be(RevenueShares.NoNetRevenue);
        RevenueShares.FromComponents(10, 0, 0, 0, 10, 0).Should().Be(RevenueShares.NoNetRevenue);
        Setup(Estimate("Wheat Farming", RevenueShares.Unknown), "mid").Assumptions
            .Should().Contain(a => a.Contains("No drop breakdown"));
    }

    // ── tierUnlock ──

    /// <summary>Synthetic, increasing tier rates: tier k (0 based) earns (k + 1) * C.</summary>
    private static TaskEstimate? TierRate(IReadOnlyList<string> tiers, string name)
    {
        var index = GearSetupEstimator.TierIndex(tiers, name);
        return index < 0 ? null : Community(tiers[index], (index + 1) * C);
    }

    private static TaskGearSetups TierPage(string task, int contributors = 5)
    {
        var own = TierRate(GearFixtures.KuudraTiers, task)!;
        own.Contributors = contributors;
        return GearSetupEstimator.Estimate(new GearEstimateInput(task, Resolve(GearFixtures.All(), task), own,
            RevenueShares.Unknown, name => TierRate(GearFixtures.KuudraTiers, name), null));
    }

    [Test]
    public void TierUnlock_LowestTierPage_ShowsEveryUpgrade()
    {
        var page = TierPage("Kuudra T1");

        page.BaselineCoinsPerHour.Should().Be(C, "the baseline earns the bound tier's own rate");
        page.Baseline.Assumptions.Should().Contain(x => x.Contains("whatever gear it takes"));
        page.Baseline.Status.Should().Be(GearSetupStatus.Baseline);
        page.Setups.Select(s => s.Status).Should().OnlyContain(s => s == GearSetupStatus.Upgrade);
        // budget unlocks T3, mid T4, endgame T5
        page.Setups.Select(s => s.DeltaCoinsPerHour).Should().Equal(2 * C, 3 * C, 4 * C);
        Setup(page, "endgame").Multiplier.Should().Be(5, "tierUnlock multipliers are rate ratios, not clamped");
    }

    [Test]
    public void TierUnlock_HigherTierPage_MarksLowerGearAsReached()
    {
        var page = TierPage("Kuudra T4");

        Setup(page, "budget").Status.Should().Be(GearSetupStatus.Reached, "T3 gear is below T4");
        Setup(page, "mid").Status.Should().Be(GearSetupStatus.Reached, "T4 gear is this tier's own");
        Setup(page, "mid").DeltaCoinsPerHour.Should().BeNull();
        Setup(page, "mid").CoinsPerHour.Should().BeNull();
        Setup(page, "mid").Multiplier.Should().BeNull();
        Setup(page, "endgame").Status.Should().Be(GearSetupStatus.Upgrade);
        Setup(page, "endgame").DeltaCoinsPerHour.Should().Be(5 * C - 4 * C);
    }

    [Test]
    public void TierUnlock_HighestTierPage_HasOnlyReachedSetups()
    {
        TierPage("Kuudra T5").Setups.Should().OnlyContain(s => s.Status == GearSetupStatus.Reached && s.DeltaCoinsPerHour == null);
    }

    [Test]
    public void TierUnlock_ConfidenceTakesBothTiersData()
    {
        TierPage("Kuudra T1", contributors: 1).Setups.Should().OnlyContain(s => s.Confidence == "medium");
        TierPage("Kuudra T1").Setups.Should().OnlyContain(s => s.Confidence == "high");
    }

    [Test]
    public void TierUnlock_WithoutDataForTheUnlockedTask_HasNoNumbers()
    {
        var result = Estimate("Kuudra T4", RevenueShares.Unknown, Community("Kuudra T4", C));

        Setup(result, "endgame").CoinsPerHour.Should().BeNull();
        Setup(result, "endgame").DeltaCoinsPerHour.Should().BeNull();
        Setup(result, "endgame").Confidence.Should().Be("low");
    }

    private static TaskGearSetups TierPage(string task, Func<string, double?> rateOf)
    {
        TaskEstimate? Rate(string name) => rateOf(name) is { } r ? Community(name, r) : null;
        return GearSetupEstimator.Estimate(new GearEstimateInput(task, Resolve(GearFixtures.All(), task), Rate(task),
            RevenueShares.Unknown, Rate, null));
    }

    /// <summary>Regression: gear for F6 (1.50M) was offered on F1-F4 although F5 (2.01M), which it also does, earns more.</summary>
    [Test]
    public void TierUnlock_UpgradeEarnsTheBestTierItReaches_NotJustTheUnlockedOne()
    {
        // T1 1, T2 2, T3 (budget unlock) 1.5, T4 4, T5 5
        var rates = new Dictionary<string, double> { ["Kuudra T1"] = C, ["Kuudra T2"] = 2 * C, ["Kuudra T3"] = 1.5 * C, ["Kuudra T4"] = 4 * C, ["Kuudra T5"] = 5 * C };

        var page = TierPage("Kuudra T1", name => rates.GetValueOrDefault(name));

        Setup(page, "budget").CoinsPerHour.Should().Be(2 * C);
        Setup(page, "budget").DeltaCoinsPerHour.Should().Be(C);
        Setup(page, "budget").Assumptions.Should().Contain(a => a.Contains("Kuudra T2"));
    }

    [Test]
    public void TierUnlock_GearThatOnlyUnlocksLowerEarningTiers_IsNotAnUpgrade()
    {
        var rates = new Dictionary<string, double> { ["Kuudra T1"] = 3 * C, ["Kuudra T2"] = C, ["Kuudra T3"] = 2 * C, ["Kuudra T4"] = 4 * C, ["Kuudra T5"] = 5 * C };

        var page = TierPage("Kuudra T1", name => rates.GetValueOrDefault(name));

        Setup(page, "budget").Status.Should().Be(GearSetupStatus.Reached);
        Setup(page, "budget").DeltaCoinsPerHour.Should().BeNull();
        Setup(page, "mid").Status.Should().Be(GearSetupStatus.Upgrade);
    }

    /// <summary>Regression: the M7 baseline claimed the Adaptive starter kit earns the M7 community rate.</summary>
    [Test]
    public void TierUnlock_BaselineOfATierTheStarterKitCannotDo_ClaimsNoGear()
    {
        var low = TierPage("Kuudra T1");
        var high = TierPage("Kuudra T4");

        low.Baseline.Items.Should().NotBeEmpty();
        high.Baseline.Name.Should().Be(GearSetupEstimator.UnknownGearBaselineName);
        high.Baseline.Items.Should().BeEmpty();
        high.BaselineCoinsPerHour.Should().Be(4 * C);
    }

    // ── every binding, shipped and synthetic ──

    private static List<BoundGearProfile> AllBindings()
    {
        var bindings = GearProfileCatalog.Embedded.Bindings.Concat(GearFixtures.All().Bindings).ToList();
        bindings.Should().NotBeEmpty();
        return bindings;
    }

    /// <summary>Estimates a binding with C for scaled models, or with synthetic increasing tier rates for tierUnlock.</summary>
    private static TaskGearSetups EstimateBinding(BoundGearProfile bound, RevenueShares shares)
    {
        var task = Registry.GetByName(bound.Binding.Task) as MethodTask;
        var name = task == null ? bound.Binding.Task : TaskEstimator.GetTaskName(task);
        var tiers = bound.Profile.Tiers ?? [];
        var community = tiers.Count > 0 ? TierRate(tiers, name) : Community(name, C);
        return GearSetupEstimator.Estimate(new GearEstimateInput(name, GearBindingResolver.Resolve(bound.Profile, bound.Binding),
            community, shares, other => TierRate(tiers, other), null));
    }

    /// <summary>
    /// Deliberate trade-offs (a setup worse than the cheaper one in some rate component), as (profile id, setup id).
    /// Empty on purpose: add an entry only with a sourced reason, every other pair must dominate.
    /// </summary>
    private static readonly HashSet<(string Profile, string Setup)> AllowedTradeOffs = [];

    /// <summary>
    /// Better tiers never earn less: for scaled models each setup, baseline included, must be at least as good as the
    /// next cheaper one in every rate component (then it wins under any non-negative revenue mix).
    /// </summary>
    [Test]
    public void EveryScaledBinding_EachTierDominatesTheCheaperOne()
    {
        foreach (var bound in AllBindings().Where(b => b.Profile.Tiers == null))
        {
            var resolved = GearBindingResolver.Resolve(bound.Profile, bound.Binding);
            var model = GearRateModels.Create(resolved.Model);
            // the baseline usually has no tier field, so it is put first explicitly
            var ordered = new[] { resolved.Baseline }.Concat(resolved.Setups.OrderBy(s => GearTiers.Rank(s.Tier))).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (AllowedTradeOffs.Contains((bound.Profile.Id, ordered[i].Id)))
                    continue;
                var better = GearSetupEstimator.FactorsOf(model, resolved.Profile, ordered[i]);
                var cheaper = GearSetupEstimator.FactorsOf(model, resolved.Profile, ordered[i - 1]);
                Dominates(better, cheaper).Should().BeTrue(
                    $"{bound.Profile.Id}/{bound.Binding.Task}: {ordered[i].Id} {better} must be at least {ordered[i - 1].Id} {cheaper} in every component");
            }
        }
    }

    /// <summary>tierUnlock: with increasing tier rates, the baseline and every upgrade earn strictly more, in order.</summary>
    [Test]
    public void EveryTierBinding_BetterTiersEarnMore()
    {
        foreach (var bound in AllBindings().Where(b => b.Profile.Tiers != null))
        {
            var result = EstimateBinding(bound, RevenueShares.Unknown);
            var rates = new[] { result.Baseline }.Concat(result.Setups.Where(s => s.Status == GearSetupStatus.Upgrade))
                .Select(s => s.CoinsPerHour).ToList();
            rates.Should().OnlyContain(r => r.HasValue, $"{bound.Profile.Id}/{bound.Binding.Task}");
            rates.Select(r => r!.Value).Should().BeInAscendingOrder($"{bound.Profile.Id}/{bound.Binding.Task}")
                .And.OnlyHaveUniqueItems();
        }
    }

    [Test]
    public void Dominates_RequiresEveryComponent()
    {
        Dominates(new RateFactors(1, 2, 1), new RateFactors(1, 1, 1)).Should().BeTrue();
        Dominates(new RateFactors(1, 2, 0.9), new RateFactors(1, 1, 1)).Should().BeFalse("a trade-off is not dominance");
    }

    private static bool Dominates(RateFactors a, RateFactors b) =>
        a.Bulk >= b.Bulk - 1e-9 && a.Rare >= b.Rare - 1e-9 && a.Throughput >= b.Throughput - 1e-9;

    [Test]
    public void EveryTierBinding_NeverShowsANegativeDelta()
    {
        foreach (var bound in AllBindings().Where(b => b.Profile.Tiers != null))
        {
            var result = EstimateBinding(bound, RevenueShares.Unknown);
            result.Setups.Where(s => s.DeltaCoinsPerHour.HasValue)
                .Should().OnlyContain(s => s.DeltaCoinsPerHour > 0, $"{bound.Profile.Id}/{bound.Binding.Task}");
            result.Setups.Where(s => s.Status == GearSetupStatus.Reached)
                .Should().OnlyContain(s => s.DeltaCoinsPerHour == null && s.CoinsPerHour == null);
        }
    }

    /// <summary>Assumptions are public, every number is premium only: digits may only come from task names (F7, Kuudra T5).</summary>
    [Test]
    public void EveryBinding_AssumptionsCarryNoNumbers()
    {
        var taskNames = Registry.PublicMethodTasks.Select(TaskEstimator.GetTaskName)
            .OrderByDescending(n => n.Length).Select(Regex.Escape).ToList();
        var names = new Regex(string.Join("|", taskNames), RegexOptions.IgnoreCase);
        foreach (var bound in AllBindings())
        foreach (var shares in new[] { RevenueShares.Unknown, RevenueShares.NoNetRevenue })
        {
            var result = EstimateBinding(bound, shares);
            foreach (var setup in result.Setups.Append(result.Baseline))
            {
                setup.Assumptions.Should().NotBeEmpty();
                foreach (var assumption in setup.Assumptions)
                    names.Replace(assumption, "").Any(char.IsDigit)
                        .Should().BeFalse($"{bound.Profile.Id}/{bound.Binding.Task} {setup.Id}: '{assumption}'");
            }
        }
        Setup(Estimate("Wheat Farming", RevenueShares.Unknown), "mid").Assumptions
            .Should().Contain(a => a.Contains("earned with the 'Synthetic budget' setup"));
    }
}
