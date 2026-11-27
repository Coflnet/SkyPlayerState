using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: Kuudra tiers declared no costs (production audit, 104 h of T5 periods: -47,394 Toxic Arrow Poison worth
/// 89M, Kismet Feathers, Twilight Arrow Poison and Magma Arrow Bundles consumed) and the tier key, used from the Kuudra
/// menu and without a market price, was never charged. Catacombs floors consumed Dungeon Chest Keys and Architect's First
/// Drafts without declaring them.
/// </summary>
public class KuudraCostsTests
{
    private const string InfernalKey = "KUUDRA_INFERNAL_TIER_KEY";
    private static readonly KuudraT5Task T5 = new();

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ESSENCE_CRIMSON"] = 1_000,
        ["ENCHANTED_RED_SAND"] = 1_000,
        ["ENCHANTED_MYCELIUM"] = 2_000,
        ["CORRUPTED_NETHER_STAR"] = 50_000,
    };

    // 3M listed coins minus the 20% reputation discount, 80 of the cheaper faction material, 2 nether stars
    private const double InfernalKeyValue = 2_400_000 + 80 * 1_000 + 2 * 50_000;

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
    public void FilterOwnedItems_DropsAGainedSupply(string supply)
    {
        var items = new Dictionary<string, int> { [supply] = 1, ["ESSENCE_CRIMSON"] = 2_000 };

        var owned = TaskPeriodFolder.FilterOwnedItems(items, T5);

        owned.Should().NotContainKey(supply, "a bought or restocked supply is no loot");
        owned.Should().ContainKey("ESSENCE_CRIMSON");
    }

    [Test]
    public void EveryTier_ChargesItsOwnKey_AtItsRunsPerHour()
    {
        MethodTask[] tiers = [new KuudraT1Task(), new KuudraT2Task(), new KuudraT3Task(), new KuudraT4Task(), T5];
        string[] keys = ["KUUDRA_TIER_KEY", "KUUDRA_HOT_TIER_KEY", "KUUDRA_BURNING_TIER_KEY", "KUUDRA_FIERY_TIER_KEY", InfernalKey];
        double[] runsPerHour = [12, 10, 8, 6, 5];

        for (var i = 0; i < tiers.Length; i++)
            tiers[i].FormulaCostsForTest.Should().BeEquivalentTo([new MethodDrop(keys[i], runsPerHour[i])], tiers[i].Name);
    }

    [Test]
    public void KuudraChestClaims_ChargesNoKey()
    {
        new KuudraChestClaimsTask().FormulaCostsForTest.Should().BeEmpty("its periods have no known tier");
    }

    [Test]
    public void FormulaRate_T5_SubtractsFiveInfernalKeys()
    {
        var gross = 2_000 * Prices["ESSENCE_CRIMSON"];

        TaskEstimator.FormulaRate(T5, Prices).Should().BeApproximately(gross - 5 * InfernalKeyValue, 1);
    }

    [Test]
    public void ScaledFormulaCost_ChargesOneKeyPerRunOfEssence()
    {
        // 400 essence is one run at the T5 seed (2,000 essence/h over 5 runs/h), whatever the period length
        var oneRun = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 400 };
        var threeRuns = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 1_200 };

        T5.ScaledFormulaCost(oneRun, 0.25, PriceOf).Should().BeApproximately(InfernalKeyValue, 1);
        T5.ScaledFormulaCost(threeRuns, 0.5, PriceOf).Should().BeApproximately(3 * InfernalKeyValue, 1);
    }

    [Test]
    public void ScaledFormulaCost_KeyValueResolvesBeforeTheMarketPrice()
    {
        var oneRun = new Dictionary<string, double> { ["ESSENCE_CRIMSON"] = 400 };
        double WithBogusKeyPrice(string tag) => tag == InfernalKey ? 99 : PriceOf(tag);

        T5.ScaledFormulaCost(oneRun, 1, WithBogusKeyPrice).Should().BeApproximately(InfernalKeyValue, 1);
    }

    [Test]
    public void KeyValue_FollowsTheMaterialPrices()
    {
        double Pricier(string tag) => tag == "ENCHANTED_RED_SAND" ? 3_000 : PriceOf(tag);

        KuudraKeyCost.TryGetValue(InfernalKey, Pricier, out var value).Should().BeTrue();

        value.Should().Be(2_400_000 + 80 * 2_000 + 2 * 50_000, "mycelium is now the cheaper faction material");
    }

    [Test]
    public void KeyValue_WithoutMaterialPrices_IsTheCoinCost()
    {
        KuudraKeyCost.TryGetValue("KUUDRA_TIER_KEY", _ => 0, out var value).Should().BeTrue();

        value.Should().Be(200_000);
    }

    [Test]
    public void KeyValue_IsOnlyDefinedForKuudraKeys()
    {
        KuudraKeyCost.TryGetValue("ESSENCE_CRIMSON", PriceOf, out _).Should().BeFalse();
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
