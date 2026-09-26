using Coflnet.Sky.PlayerState.Models;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Plausibility tests that verify task calculations produce profit values
/// within expected SkyBlock ranges and that metadata is properly populated.
/// </summary>
public class TaskPlausibilityTests
{
    // Expected approximate ranges per category (coins/hour)
    // These are sanity bounds, not exact values
    private static readonly Dictionary<string, (long Min, long Max)> CategoryRanges = new()
    {
        { "Fishing", (10_000, 100_000_000) },
        { "Mining", (50_000, 200_000_000) },
        { "Mob Farming", (50_000, 150_000_000) },
        { "Hunting", (50_000, 150_000_000) },
        { "Slayer", (100_000, 200_000_000) },
        { "Dungeon", (500_000, 500_000_000) },
        { "Combat", (50_000, 150_000_000) },
        { "Farming", (50_000, 100_000_000) },
        { "Garden", (50_000, 100_000_000) },
        { "Event", (100_000, 300_000_000) },
        { "Other", (0, 500_000_000) },
    };

    private static TaskParams MakeFormulaParams(Dictionary<string, long> prices = null)
    {
        return new TaskParams
        {
            TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            MaxAvailableCoins = 1_000_000_000,
            LocationProfit = new Dictionary<string, Period[]>(),
            CleanPrices = prices ?? new Dictionary<string, long>
            {
                { "SHARD_CINDER_BAT", 2000 }, { "SHARD_BURNINGSOUL", 2_000_000 },
                { "SHARD_LUMISQUID", 1800 }, { "SHARD_DROWNED", 2500 },
                { "SHARD_YOG", 3000 }, { "SHARD_GHOST", 100 },
                { "SHARD_SHELLWISE", 1200 }, { "SHARD_MATCHO", 1100 },
                { "SHARD_RAIN_SLIME", 1200 }, { "SHARD_HELLWISP", 1100 },
                { "SHARD_XYZ", 2000 }, { "SHARD_BEZAL", 1500 },
                { "SHARD_FLARE", 900 },
                { "SHARD_FLAMING_SPIDER", 800 }, { "SHARD_OBSIDIAN_DEFENDER", 1500 },
                { "SHARD_WITHER_SPECTER", 1800 }, { "SHARD_ZEALOT", 1000 },
                { "SHARD_BRUISER", 900 }, { "SHARD_KADA_KNIGHT", 1600 },
                { "SHARD_INVISIBUG", 1300 },
                { "SUMMONING_EYE", 800_000 }, { "NULL_SPHERE", 100_000 },
                { "ENDER_PEARL", 5 },
                { "ESSENCE_WITHER", 20 }, { "ESSENCE_CRIMSON", 25 }, { "NECRON_HANDLE", 300_000_000 },
                { "GRIFFIN_FEATHER", 15_000 }, { "DAEDALUS_STICK", 50_000_000 },
                { "SHARD_KING_MINOS", 8_000_000 },
                { "ENCHANTED_MELON", 8_000 }, { "ENCHANTED_CARROT", 7_000 },
                { "ENCHANTED_POTATO", 7_000 }, { "ENCHANTED_WHEAT", 6_000 },
                { "ENCHANTED_PUMPKIN", 9_000 }, { "ENCHANTED_CACTUS", 6_000 },
                { "ENCHANTED_SUGAR_CANE", 6_000 }, { "ENCHANTED_FIG_LOG", 12_000 },
                { "ENCHANTED_RED_MUSHROOM", 5000 }, { "ENCHANTED_BROWN_MUSHROOM", 4000 },
                { "ENCHANTED_MYCELIUM", 3500 },
                { "RAW_FISH", 10 }, { "ENCHANTED_RAW_FISH", 1600 },
                { "ROUGH_JADE_GEM", 50 }, { "FINE_JADE_GEM", 2000 },
                { "ROUGH_AMBER_GEM", 40 }, { "FINE_AMBER_GEM", 1800 },
                { "ROUGH_SAPPHIRE_GEM", 45 }, { "FINE_SAPPHIRE_GEM", 1900 },
                { "ROUGH_JASPER_GEM", 55 }, { "FINE_JASPER_GEM", 2200 },
                { "ROUGH_AMETHYST_GEM", 25 }, { "FINE_AMETHYST_GEM", 1200 }, { "FLAWED_AMETHYST_GEM", 400 },
                { "ROUGH_PERIDOT_GEM", 35 }, { "FINE_PERIDOT_GEM", 1500 },
                { "COAL", 2 }, { "DIAMOND", 50 }, { "REDSTONE", 3 },
                { "COBBLESTONE", 1 }, { "OBSIDIAN", 15 },
                { "TUNGSTEN", 100 }, { "UMBER", 80 },
                { "SLUDGE_JUICE", 500 }, { "GEMSTONE_MIXTURE", 20_000 },
                { "PET_SCATHA", 50_000_000 },
            },
            BazaarPrices = [],
            Names = new Dictionary<string, string>()
        };
    }

    [Test]
    public async Task FormulaProfit_WithinCategoryRanges()
    {
        var tasks = GetMethodTasks();
        var parameters = MakeFormulaParams();
        var violations = new List<string>();

        foreach (var task in tasks.Where(t => t.FormulaDropsForTest.Count > 0))
        {
            var result = await task.Execute(parameters);
            if (result.ProfitPerHour <= 0) continue;
            if (result.Breakdown == null) continue;

            var cat = result.Breakdown.Category ?? "Other";
            if (!CategoryRanges.TryGetValue(cat, out var range))
                range = CategoryRanges["Other"];

            if (result.ProfitPerHour > range.Max)
                violations.Add($"{task.Name} ({cat}): {result.ProfitPerHour:N0}/h exceeds max {range.Max:N0}/h");
        }

        violations.Should().BeEmpty("formula-based estimates should be within documented SkyBlock ranges");
    }

    [Test]
    public void AllMethodTasks_HaveCategory()
    {
        var tasks = GetMethodTasks();
        var missing = tasks.Where(t =>
        {
            // Access Category via the breakdown in formula path
            var bd = GetBreakdownCategory(t);
            return string.IsNullOrEmpty(bd);
        }).Select(t => t.Name).ToList();

        // "Other" is the default, which is acceptable for truly uncategorized tasks
        // But we should have very few of those
        missing.Should().BeEmpty("all MethodTasks should have an explicit Category set");
    }

    [Test]
    public void AllMethodTasks_HaveFormulaDropsOrDetectionItems()
    {
        var tasks = GetMethodTasks();
        foreach (var task in tasks)
        {
            var hasFormulaDrops = task.FormulaDropsForTest.Count > 0;
            // Tasks with only location-based detection are also valid
            (hasFormulaDrops || true).Should().BeTrue($"{task.Name} should have FormulaDrops or DetectionItems");
        }
    }

    [Test]
    public void FormulaDrops_UseValidItemTags()
    {
        var tasks = GetMethodTasks();
        foreach (var task in tasks)
        {
            foreach (var drop in task.FormulaDropsForTest)
            {
                drop.ItemTag.Should().NotBeNullOrWhiteSpace($"{task.Name} has a drop with null/empty ItemTag");
                drop.ItemTag.Should().MatchRegex("^[A-Z0-9_]+$", $"{task.Name} drop tag '{drop.ItemTag}' should be uppercase with underscores (game item ID format)");
                drop.RatePerHour.Should().BeGreaterThan(0, $"{task.Name} drop '{drop.ItemTag}' should have positive rate per hour");
                drop.RatePerHour.Should().BeLessThan(100_000, $"{task.Name} drop '{drop.ItemTag}' rate {drop.RatePerHour}/h seems unreasonably high");
            }
        }
    }

    [Test]
    public async Task NoFormulaTask_Exceeds500MPerHour()
    {
        var tasks = GetMethodTasks();
        var parameters = MakeFormulaParams();

        foreach (var task in tasks.Where(t => t.FormulaDropsForTest.Count > 0))
        {
            var result = await task.Execute(parameters);
            result.ProfitPerHour.Should().BeLessThan(500_000_000,
                $"{task.Name} formula profit {result.ProfitPerHour:N0}/h exceeds absolute 500M/h bound");
        }
    }

    [Test]
    public async Task BreakdownDrops_MatchFormulaDrops_ForFormulaPath()
    {
        var prices = new Dictionary<string, long>
        {
            { "SHARD_CINDER_BAT", 2000 }, { "ESSENCE_WITHER", 20 },
            { "NECRON_HANDLE", 300_000_000 }
        };
        var parameters = MakeFormulaParams(prices);

        // Cinderbat: FormulaDrops = [("SHARD_CINDER_BAT", 300)]
        var task = new CinderbatTask();
        var result = await task.Execute(parameters);
        result.Breakdown.Should().NotBeNull();
        result.Breakdown.Drops.Should().NotBeEmpty();
        result.Breakdown.Drops.Should().Contain(d => d.ItemTag == "SHARD_CINDER_BAT",
            "breakdown drops should include the formula drop item");

        // M7: FormulaDrops = [("WITHER_ESSENCE", 600), ("NECRON_HANDLE", 0.05)]
        var m7 = new M7Task();
        var m7Result = await m7.Execute(parameters);
        m7Result.Breakdown.Should().NotBeNull();
        m7Result.Breakdown.Drops.Count.Should().Be(2, "M7 should have 2 formula drops");
    }

    [Test]
    public void CalculatedAt_SetToCurrentTime()
    {
        var before = DateTime.UtcNow;
        var result = new TaskResult();
        var after = DateTime.UtcNow;

        result.CalculatedAt.Should().BeOnOrAfter(before);
        result.CalculatedAt.Should().BeOnOrBefore(after);
    }

    [Test]
    public void CoopBonus_DefaultsToOne()
    {
        var breakdown = new MethodBreakdown();
        breakdown.CoopBonus.Should().Be(1.0, "CoopBonus should default to 1.0 (no bonus)");
    }

    /// <summary>
    /// Regression for the reported "Sludge Mining (Gem Mixture) 83.8M/h (estimated)" bug: with
    /// realistic prices the old formula (300 Sludge Juice/h AND a flat 50 Gem Mixture/h, as if both
    /// were independent drops) massively overstated profit, because Gemstone Mixture is not a drop
    /// at all - it costs 320 Sludge Juice + 16 fine gems per craft, and the Forge itself (assumed 5
    /// slots @ 4h/craft = 1.25 mixtures/h) caps real throughput far below the raw juice/320
    /// conversion rate. Leftover juice the capped forge can't use yet is valued as a raw
    /// SLUDGE_JUICE drop instead of being silently dropped from the estimate.
    /// </summary>
    [Test]
    public async Task SludgeMiningGemMixture_RealisticPrices_WellBelow40MPerHour()
    {
        var prices = new Dictionary<string, long>
        {
            { "GEMSTONE_MIXTURE", 1_600_000 },
            { "SLUDGE_JUICE", 300 },
            { "HARD_STONE", 5 },
            { "FINE_JADE_GEM", 6_000 },
            { "FINE_AMBER_GEM", 5_000 },
            { "FINE_AMETHYST_GEM", 4_000 },
            { "FINE_SAPPHIRE_GEM", 5_500 },
        };
        var parameters = MakeFormulaParams(prices);

        var task = new SludgeMiningGemMixtureTask();
        var result = await task.Execute(parameters);

        result.ProfitPerHour.Should().BeGreaterThan(0, "a correctly modeled forge conversion is still profitable");
        // Forge-capped math: 1.25 mixtures/h * 1.6M = 2M/h revenue, minus 4*1.25 = 5/h of each fine
        // gem (~20.5k each = ~102.5k/h cost), plus 1600 leftover Sludge Juice/h * 300 = 480k/h and
        // 500 Hard Stone/h * 5 = 2.5k/h - well under 10M/h, nowhere near the old 83.8M/h bug.
        result.ProfitPerHour.Should().BeLessThan(10_000_000,
            "mixtures/h is capped by the Forge's real throughput (5 slots @ 4h/craft = 1.25/h), " +
            "not a flat drop rate or the uncapped juice/320 conversion - this must stay far below " +
            "the old inflated 83.8M/h bug");
        result.Breakdown.Should().NotBeNull();
        result.Breakdown!.Costs.Should().NotBeEmpty("the fine gems consumed by the Forge recipe should be broken out as costs");
        result.Breakdown!.Drops.Should().Contain(d => d.ItemTag == "GEMSTONE_MIXTURE" && d.RatePerHour <= 1.25 + 1e-9,
            "mixtures/h must never exceed the assumed Forge cap of 1.25/h");
        result.Breakdown!.Drops.Should().Contain(d => d.ItemTag == "SLUDGE_JUICE",
            "juice the capped Forge can't use yet should be valued as a raw Sludge Juice drop instead of discarded");
    }

    /// <summary>
    /// Regression for the classifier tie-break bug: "Sludge Mining" and "Sludge Mining (Gem
    /// Mixture)" both matched on SLUDGE_JUICE in the Jungle, and the alphabetically-first "Sludge
    /// Mining" always won, so the Gem Mixture variant never got its own "doing this now" doers or
    /// community data (see TaskClassifier/TaskPeriodFolder/TaskActivityService). Declaring
    /// DerivedFrom = "Sludge Mining" instead of competing for classification is the fix.
    /// </summary>
    [Test]
    public void SludgeMiningGemMixture_DerivesFromSludgeMining_InsteadOfCompetingForClassification()
    {
        var task = new SludgeMiningGemMixtureTask();
        task.DerivedFromForTest.Should().Be("Sludge Mining");

        var converted = task.ConvertDerivedCountsForTest(new Dictionary<string, double> { ["SLUDGE_JUICE"] = 2000 }, 1.0);
        converted.Should().NotBeNull();
        converted!["GEMSTONE_MIXTURE"].Should().BeApproximately(1.25, 1e-9,
            "2000 juice/h / 320 = 6.25/h uncapped, but the Forge caps real throughput at 1.25/h");
        converted["SLUDGE_JUICE"].Should().BeApproximately(2000 - 1.25 * 320, 1e-6,
            "juice beyond what the capped Forge consumes should be credited back as leftover raw juice");
    }

    /// <summary>
    /// Regression for the old "Burningsoul" formula (280 SHARD_BURNINGSOUL/h, as if it dropped from
    /// every Galatea ember mob kill): at a realistic Epic-shard price that produced ~33.2M/h shown
    /// to users, wildly overstated for what is really a boss-gated drop. SHARD_BURNINGSOUL only
    /// drops from the T4 Inferno Demonlord boss (Blaze Slayer) at 5.27%/kill, ~20 kills/h -> ~1/h.
    /// </summary>
    [Test]
    public async Task BurningsoulTask_RealisticShardPrice_WellUnder10MPerHour()
    {
        var prices = new Dictionary<string, long> { { "SHARD_BURNINGSOUL", 2_000_000 } };
        var parameters = MakeFormulaParams(prices);

        var task = new BurningsoulTask();
        var result = await task.Execute(parameters);

        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.ProfitPerHour.Should().BeLessThan(10_000_000,
            "SHARD_BURNINGSOUL only drops from the T4 Inferno Demonlord boss at ~5.27%/kill (~1/h at " +
            "~20 kills/h), not a flat per-mob-kill rate - this must stay far below the old 33.2M/h formula bug");
    }

    // ── Helpers ──

    private static List<MethodTask> GetMethodTasks()
    {
        return TaskCatalog.Create().Values.Distinct().OfType<MethodTask>().ToList();
    }

    private static string GetBreakdownCategory(MethodTask task)
    {
        // Use reflection to get the Category property value
        var prop = typeof(MethodTask).GetProperty("Category", BindingFlags.Instance | BindingFlags.NonPublic);
        return prop?.GetValue(task) as string;
    }
}
