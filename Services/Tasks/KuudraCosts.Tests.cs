using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: Kuudra tiers declared no costs (production audit, 104 h of T5 periods: -47,394 Toxic Arrow Poison worth
/// 89M, Kismet Feathers, Twilight Arrow Poison and Magma Arrow Bundles consumed) and the tier key, bought when the paid
/// chest is opened, was never charged. Catacombs floors consumed Dungeon Chest Keys and Architect's First Drafts without
/// declaring them.
/// </summary>
public class KuudraCostsTests
{
    private const string InfernalKey = "KUUDRA_INFERNAL_TIER_KEY";
    private static readonly KuudraT5Task T5 = new();

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ESSENCE_CRIMSON"] = 1_000,
        ["KUUDRA_TEETH"] = 10_000,
        ["SHARD_KRAKEN"] = 100_000,
        ["ENCHANTED_RED_SAND"] = 1_000,
        ["ENCHANTED_MYCELIUM"] = 2_000,
        ["CORRUPTED_NETHER_STAR"] = 50_000,
    };

    // 3M listed coins at the full reputation price, 80 of the cheaper faction material, 2 nether stars
    private const double InfernalKeyValue = 2_328_000 + 80 * 1_000 + 2 * 50_000;

    private static double PriceOf(string tag) => Prices.GetValueOrDefault(tag);

    [Test]
    public void FilterOwnedItems_KeepsConsumedArrowPoisonsAndKismet_AsCosts()
    {
        var items = new Dictionary<string, int>
        {
            ["TOXIC_ARROW_POISON"] = -450, ["TWILIGHT_ARROW_POISON"] = -64, ["ARROW_BUNDLE_MAGMA"] = -2,
            ["KISMET_FEATHER"] = -1, ["ESSENCE_CRIMSON"] = 2_000,
        };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, T5);

        owned.Should().BeEquivalentTo(items);
    }

    [TestCase("TOXIC_ARROW_POISON")]
    [TestCase("KISMET_FEATHER")]
    [TestCase(InfernalKey)]
    [TestCase("KUUDRA_TIER_KEY")]
    public void FilterOwnedItems_DropsAGainedSupply(string supply)
    {
        var items = new Dictionary<string, int> { [supply] = 1, ["ESSENCE_CRIMSON"] = 2_000 };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, T5);

        owned.Should().NotContainKey(supply, "a bought or fished supply is no loot");
        owned.Should().ContainKey("ESSENCE_CRIMSON");
    }

    [Test]
    public void FilterOwnedItems_KeepsTheObservedChestCost()
    {
        var items = new Dictionary<string, int> { [PseudoItems.DUNGEON_CHEST_COST] = -2_328_000, ["ESSENCE_CRIMSON"] = 2_000 };

        TaskPeriodFolder.FilterOwnedItems(items, T5).Should().BeEquivalentTo(items);
    }

    [Test]
    public void EveryTier_TreatsEveryKeyAsASupply()
    {
        MethodTask[] tiers = [new KuudraT1Task(), new KuudraT2Task(), new KuudraT3Task(), new KuudraT4Task(), T5, new KuudraChestClaimsTask()];

        foreach (var tier in tiers)
        {
            var gained = new Dictionary<string, int> { ["KUUDRA_HOT_TIER_KEY"] = 1, ["KUUDRA_TEETH"] = 3 };
            TaskPeriodFolder.FilterOwnedItems(gained, tier).Should().NotContainKey("KUUDRA_HOT_TIER_KEY", tier.Name);
        }
    }

    [Test]
    public void OnlyT5_ChargesAKeyInTheFormula_SinceOnlyItDeclaresThePaidChestLoot()
    {
        T5.FormulaCostsForTest.Should().BeEquivalentTo([new MethodDrop(InfernalKey, 5)]);
        MethodTask[] withoutChestLoot = [new KuudraT1Task(), new KuudraT2Task(), new KuudraT3Task(), new KuudraT4Task(), new KuudraChestClaimsTask()];
        foreach (var tier in withoutChestLoot)
            tier.FormulaCostsForTest.Should().BeEmpty(tier.Name);
    }

    [Test]
    public void FormulaRate_T5_IsTheGuaranteedLootMinusFiveInfernalKeys()
    {
        var loot = 11_000 * 1_000 + 15 * 10_000 + 5 * 100_000;

        TaskEstimator.FormulaRate(T5, Prices).Should().BeApproximately(loot - 5 * InfernalKeyValue, 1);
    }

    [Test]
    public void FormulaRate_T5_AtProductionPrices_IsNotNegative()
    {
        // production prices of the 2026-11-27 audit (bazaar sell)
        var production = new Dictionary<string, double>
        {
            ["ESSENCE_CRIMSON"] = 1_111.8, ["KUUDRA_TEETH"] = 8_616.8, ["SHARD_KRAKEN"] = 136_338.2,
            ["ENCHANTED_RED_SAND"] = 1_335.5, ["ENCHANTED_MYCELIUM"] = 1_678.1, ["CORRUPTED_NETHER_STAR"] = 9_751.6,
        };

        TaskEstimator.FormulaRate(T5, production).Should().BePositive("the guaranteed paid chest loot pays for the key");
    }

    [Test]
    public void ScaledFormulaCost_WithoutAnObservedChestCost_ChargesOneKeyPerRunOfEssence()
    {
        // 2,200 essence is one run at the T5 seed (11,000 essence/h over 5 runs/h), whatever the period length
        var oneRun = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 2_200 };
        var threeRuns = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 6_600 };

        T5.ScaledFormulaCost(oneRun, 0.25, PriceOf).Should().BeApproximately(InfernalKeyValue, 1);
        T5.ScaledFormulaCost(threeRuns, 0.5, PriceOf).Should().BeApproximately(3 * InfernalKeyValue, 1);
    }

    [Test]
    public void ScaledFormulaCost_WithAnObservedChestCost_EstimatesNothing()
    {
        var observed = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 2_000, [PseudoItems.DUNGEON_CHEST_COST] = -2_328_000 };

        T5.ScaledFormulaCost(observed, 0.2, PriceOf).Should().Be(0, "the key bought on opening is already counted");
    }

    [Test]
    public void ScaledFormulaCost_KeyValueResolvesBeforeTheMarketPrice()
    {
        var oneRun = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 2_200 };
        double WithBogusKeyPrice(string tag) => tag == InfernalKey ? 99 : PriceOf(tag);

        T5.ScaledFormulaCost(oneRun, 1, WithBogusKeyPrice).Should().BeApproximately(InfernalKeyValue, 1);
    }

    [Test]
    public void KeyValue_FollowsTheMaterialPrices()
    {
        double Pricier(string tag) => tag == "ENCHANTED_RED_SAND" ? 3_000 : PriceOf(tag);

        KuudraKeyCost.TryGetValue(InfernalKey, Pricier, out var value).Should().BeTrue();

        value.Should().Be(2_328_000 + 80 * 2_000 + 2 * 50_000, "mycelium is now the cheaper faction material");
    }

    [Test]
    public void KeyValue_WithoutMaterialPrices_IsTheCoinCost()
    {
        KuudraKeyCost.TryGetValue("KUUDRA_TIER_KEY", _ => 0, out var value).Should().BeTrue();

        value.Should().Be(155_200);
    }

    [Test]
    public void KeyValue_IsOnlyDefinedForKuudraKeys()
    {
        KuudraKeyCost.TryGetValue("ESSENCE_CRIMSON", PriceOf, out _).Should().BeFalse();
    }

    [TestCase("Kuudra Key", 1, 155_200)]
    [TestCase("Hot Kuudra Key", 2, 310_400)]
    [TestCase("Burning Kuudra Key", 3, 582_000)]
    [TestCase("Fiery Kuudra Key", 4, 1_164_000)]
    [TestCase("Infernal Kuudra Key", 5, 2_328_000)]
    public void KeyOfName_MatchesTheTrackersCoinPrices(string name, int tier, long coins)
    {
        var key = KuudraKeyCost.OfName(name);

        key.Tier.Should().Be(tier);
        key.Coins.Should().Be(coins);
    }

    [Test]
    public void CostTags_AreNoDeclaredDrops()
    {
        string[] costs = ["TOXIC_ARROW_POISON", "TWILIGHT_ARROW_POISON", "ARROW_BUNDLE_MAGMA", "KISMET_FEATHER", InfernalKey];

        foreach (var tag in costs)
            T5.ProducesItem(tag).Should().BeFalse(tag);
        T5.ProducesItem("KUUDRA_TEETH").Should().BeTrue("the paid chest loot stays declared");
    }

    [Test]
    public void Dungeon_ConsumedChestKeyAndArchitectDraft_AreCosts()
    {
        var items = new Dictionary<string, int>
        {
            ["DUNGEON_CHEST_KEY"] = -27, ["ARCHITECT_FIRST_DRAFT"] = -41, ["ESSENCE_WITHER"] = 300,
        };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, new M5Task());

        owned.Should().BeEquivalentTo(items);
    }

    [TestCase("DUNGEON_CHEST_KEY")]
    [TestCase("ARCHITECT_FIRST_DRAFT")]
    public void Dungeon_GainedChestKeyOrDraft_IsASupply(string supply)
    {
        var items = new Dictionary<string, int> { [supply] = 3, ["ESSENCE_WITHER"] = 300 };

        TaskPeriodFolder.FilterOwnedItems(items, new F5Task()).Should().NotContainKey(supply);
    }

    [Test]
    public void Dungeon_ChestKeyHasNoFormulaRate()
    {
        var m5 = new M5Task();

        m5.FormulaCostsForTest.Should().BeEmpty("a second chest is the player's choice");
        m5.ScaledFormulaCost(new() { ["ESSENCE_WITHER"] = 300, ["DUNGEON_CHEST_KEY"] = -2 }, 1, _ => 1_000_000).Should().Be(0);
    }
}
