using System.Collections.Generic;
using System.Linq;
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

    private static readonly BaseKuudraTask[] Tiers = [new KuudraT1Task(), new KuudraT2Task(), new KuudraT3Task(), new KuudraT4Task(), T5];

    [Test]
    public void EveryTier_ChargesItsKeyPerRunInTheFormula_SinceItDeclaresThePaidChestLoot()
    {
        T5.FormulaCostsForTest.Should().BeEquivalentTo([new MethodDrop(InfernalKey, 5)]);
        int[] runsPerHour = [12, 10, 8, 6, 5];
        for (var i = 0; i < Tiers.Length; i++)
            Tiers[i].FormulaCostsForTest.Should().BeEquivalentTo([new MethodDrop(KuudraKeyCost.OfTier(i + 1).Tag, runsPerHour[i])], Tiers[i].Name);
        new KuudraChestClaimsTask().FormulaCostsForTest.Should().BeEmpty();
    }

    [Test]
    public void FormulaRate_T5_IsTheGuaranteedLootMinusFiveInfernalKeys()
    {
        var loot = 16 * 10_000 + 11_000 * 1_000 + 5 * 100_000;

        TaskEstimator.FormulaRate(T5, Prices).Should().BeApproximately(loot - 5 * InfernalKeyValue, 1);
    }

    // production prices of the 2026-11-27 audit (bazaar sell)
    private static readonly Dictionary<string, double> ProductionPrices = new()
    {
        ["ESSENCE_CRIMSON"] = 1_111.8, ["KUUDRA_TEETH"] = 8_616.8, ["SHARD_KRAKEN"] = 136_338.2,
        ["ENCHANTED_RED_SAND"] = 1_335.5, ["ENCHANTED_MYCELIUM"] = 1_678.1, ["CORRUPTED_NETHER_STAR"] = 9_751.6,
    };

    [Test]
    public void FormulaRate_T5_AtProductionPrices_IsNotNegative()
        => TaskEstimator.FormulaRate(T5, ProductionPrices).Should().BePositive("the guaranteed paid chest loot pays for the key");

    /// <summary>
    /// Per run the paid chest's teeth, essence and shard plus the free chest's essence, minus one key. Fiery's guaranteed
    /// loot at the lower sourced 800 essence does not pay for its key: its profit is the rolled loot in the rare pool.
    /// </summary>
    [TestCase(1, 12, 1, 81.5, 0.70)]
    [TestCase(2, 10, 1, 218, 0.52)]
    [TestCase(3, 8, 2.34, 427.5, 0.07)]
    [TestCase(4, 6, 2.43, 900, -0.47)]
    [TestCase(5, 5, 3.2, 2_200, 0.78)]
    public void FormulaRate_AtProductionPrices_IsTheGuaranteedLootMinusTheKeys(int tier, int runs, double teeth,
        double essence, double millionsPerHour)
    {
        double PriceOf(string tag) => ProductionPrices.GetValueOrDefault(tag);
        KuudraKeyCost.TryGetValue(KuudraKeyCost.OfTier(tier).Tag, PriceOf, out var key);
        var loot = teeth * 8_616.8 + essence * 1_111.8 + 136_338.2;

        var rate = TaskEstimator.FormulaRate(Tiers[tier - 1], ProductionPrices);

        rate.Should().BeApproximately(runs * (loot - key), 1);
        (rate / 1e6).Should().BeApproximately(millionsPerHour, 0.01);
    }

    /// <summary>
    /// 3.2 teeth stand for one paid chest. <paramref name="confirmed"/> opens booked <paramref name="coinsEach"/> each (the
    /// listener books the actual purse drop) and one KUUDRA_PAID_CHEST each. Confirmed and inventory key opens booked their
    /// 2,000 essence, as DungeonRewardListener does, the estimated chests none. The cost is the window total.
    /// </summary>
    private static Dictionary<string, double> Chests(double paidChests, int confirmed = 0, double inventoryKeys = 0,
        double coinsEach = 2_328_000)
    {
        var counts = new Dictionary<string, double> { ["KUUDRA_TEETH"] = 3.2 * paidChests };
        if (confirmed + inventoryKeys > 0)
            counts["ESSENCE_CRIMSON"] = 2_000 * (confirmed + inventoryKeys);
        if (confirmed > 0)
        {
            counts[PseudoItems.DUNGEON_CHEST_COST] = -confirmed * coinsEach;
            counts[PseudoItems.KUUDRA_PAID_CHEST] = -confirmed;
        }
        if (inventoryKeys > 0)
            counts[InfernalKey] = -inventoryKeys;
        return counts;
    }

    /// <summary>The read time lookup with FullValueRates on: the chest cost is worth a coin per unit.</summary>
    private static double ChestCostValued(string tag) => tag == PseudoItems.DUNGEON_CHEST_COST ? 1 : PriceOf(tag);

    private const double Materials = 80 * 1_000 + 2 * 50_000;
    /// <summary>The Infernal paid chest's guaranteed 2,000 essence and Kraken Shard, never booked for an estimated chest.</summary>
    private const double InfernalUnseenLoot = 2_000 * 1_000 + 100_000;
    private const double InfernalNetKey = InfernalKeyValue - InfernalUnseenLoot;

    [Test]
    public void ScaledFormulaCost_ChargesAKeyPerPaidChestTheTeethStandFor_LessItsUnseenLoot()
    {
        T5.ScaledFormulaCost(Chests(1), 0.25, PriceOf).Should().BeApproximately(InfernalNetKey, 1);
        T5.ScaledFormulaCost(Chests(3), 0.5, PriceOf).Should().BeApproximately(3 * InfernalNetKey, 1);
    }

    /// <summary>
    /// Regression: a teeth-estimated T5 chest was charged its whole key while its guaranteed 2,000 essence (essence storage)
    /// and Kraken Shard never reach a claim's counts, understating T5 by about 2.36M per chest at production prices.
    /// </summary>
    [Test]
    public void T5_EstimatedChest_IsNoLongerUnderstatedByItsGuaranteedEssenceAndShard()
    {
        double PriceOf(string tag) => ProductionPrices.GetValueOrDefault(tag);
        var teethOnly = new Dictionary<string, double> { ["KUUDRA_TEETH"] = 3.2 };
        KuudraKeyCost.TryGetValue(InfernalKey, PriceOf, out var key);

        var cost = T5.ScaledFormulaCost(teethOnly, 1, PriceOf);

        cost.Should().BeApproximately(key - (2_000 * 1_111.8 + 136_338.2), 1);
        (key - cost).Should().BeApproximately(2_360_000, 1_000);
    }

    /// <summary>One chest's teeth alone: its key less the paid chest's guaranteed essence and shard at the test prices.</summary>
    [TestCase(1, 1, 80)]
    [TestCase(2, 1, 200)]
    [TestCase(3, 2.34, 400)]
    [TestCase(4, 2.43, 800)]
    [TestCase(5, 3.2, 2_000)]
    public void TeethOnlyRow_ChargesTheKeyLessTheUnseenGuaranteedLoot(int tier, double teeth, double essence)
    {
        var task = Tiers[tier - 1];
        KuudraKeyCost.TryGetValue(KuudraKeyCost.OfTier(tier).Tag, PriceOf, out var key);

        var cost = task.ScaledFormulaCost(new() { ["KUUDRA_TEETH"] = teeth }, 1, PriceOf);

        cost.Should().BeApproximately(key - essence * 1_000 - 100_000, 1, task.Name);
    }

    [Test]
    public void ObservedEssence_LowersTheCredit_NeverBelowZero()
    {
        var t2 = new KuudraT2Task();
        KuudraKeyCost.TryGetValue(KuudraKeyCost.OfTier(2).Tag, PriceOf, out var key);
        double Cost(double essence) => t2.ScaledFormulaCost(new() { ["KUUDRA_TEETH"] = 2, ["ESSENCE_CRIMSON"] = essence }, 1, PriceOf);

        Cost(150).Should().BeApproximately(2 * key - (400 - 150) * 1_000 - 2 * 100_000, 1, "150 of the 400 essence were booked");
        Cost(10_000).Should().BeApproximately(2 * key - 2 * 100_000, 1, "booked essence leaves only the shards to credit");
        Cost(399.9).Should().BeApproximately(Cost(400), 101, "continuous in the counts");
        Cost(10_000).Should().Be(Cost(400), "the essence credit stops at 0");
    }

    [Test]
    public void ObservedKrakenShards_LowerTheShardCredit()
    {
        var t1 = new KuudraT1Task();
        KuudraKeyCost.TryGetValue(KuudraKeyCost.OfTier(1).Tag, PriceOf, out var key);

        var cost = t1.ScaledFormulaCost(new() { ["KUUDRA_TEETH"] = 2, ["SHARD_KRAKEN"] = 1.5 }, 1, PriceOf);

        cost.Should().BeApproximately(2 * key - 160 * 1_000 - 0.5 * 100_000, 1);
    }

    /// <summary>Confirmed opens book their essence themselves: they get no credit, and their essence is not credited twice.</summary>
    [Test]
    public void ConfirmedOpens_StillWin_AndTheirBookedEssenceIsNotCreditedAgain()
    {
        var t2 = new KuudraT2Task();
        KuudraKeyCost.TryGetValue(KuudraKeyCost.OfTier(2).Tag, PriceOf, out var key);
        var materials = 4 * 1_000 + 2 * 50_000;
        Dictionary<string, double> Counts(double teeth, int confirmed) => new()
        {
            ["KUUDRA_TEETH"] = teeth, ["ESSENCE_CRIMSON"] = 200 * confirmed,
            [PseudoItems.KUUDRA_PAID_CHEST] = -confirmed, [PseudoItems.DUNGEON_CHEST_COST] = -confirmed * 310_400,
        };

        t2.ScaledFormulaCost(Counts(3, 3), 1, ChestCostValued).Should().BeApproximately(3 * materials, 1);
        t2.ScaledFormulaCost(Counts(3, 1), 1, ChestCostValued)
            .Should().BeApproximately(materials + 2 * (key - 200 * 1_000 - 100_000), 1, "two estimated chests are credited");
    }

    [Test]
    public void UnpricedEssenceAndShard_CreditNothing()
    {
        double KeyMaterialsOnly(string tag) => tag is "ESSENCE_CRIMSON" or "SHARD_KRAKEN" ? 0 : PriceOf(tag);

        T5.ScaledFormulaCost(Chests(1), 1, KeyMaterialsOnly).Should().BeApproximately(InfernalKeyValue, 1);
        T5.ScaledFormulaCost(Chests(1), 1, _ => 0).Should().Be(2_328_000);
        T5.ScaledFormulaCost(Chests(1), 1, _ => double.NaN).Should().Be(2_328_000, "a NaN price counts as unpriced");
    }

    /// <summary>
    /// Production 2026-11-28: 2,000 teeth over 4.48 h of T5 stood for 625 paid chests (1.52B of keys, -190 M/h). A player
    /// opens at most one paid chest per run, so the estimate is capped at twice the formula's 5 runs/h.
    /// </summary>
    [Test]
    public void ScaledFormulaCost_TeethBeyondTheRunsAnHourCanHold_ChargeAtMostOneChestPerRun()
    {
        var cost = T5.ScaledFormulaCost(Chests(625), 4.48, PriceOf);

        cost.Should().BeApproximately(2 * 5 * 4.48 * InfernalNetKey, 1);
    }

    [TestCase(1, 12)]
    [TestCase(4, 6)]
    public void LowerTier_TeethBeyondTheRunsAnHourCanHold_ChargeAtMostTwoChestsPerRun(int tier, int runs)
    {
        var task = Tiers[tier - 1];
        var oneChest = task.ScaledFormulaCost(new() { ["KUUDRA_TEETH"] = 3 }, 1, PriceOf) / (3 / TeethOf(tier));

        task.ScaledFormulaCost(new() { ["KUUDRA_TEETH"] = 500 }, 1, PriceOf).Should().BeApproximately(2 * runs * oneChest, 1);
    }

    private static double TeethOf(int tier) => new[] { 1, 1, 2.34, 2.43, 3.2 }[tier - 1];

    [Test]
    public void ScaledFormulaCost_ConfirmedOpensCountTowardTheRunCap()
        => T5.ScaledFormulaCost(Chests(100, confirmed: 8), 1, ChestCostValued)
            .Should().BeApproximately(2 * InfernalNetKey + 8 * Materials, 1, "10 chests fit into an hour, 8 of them confirmed");

    /// <summary>Review probe: 50 runs with one confirmed open turned the whole key estimate off (-11.46M/h to +1.03M/h).</summary>
    [Test]
    public void ScaledFormulaCost_PartialConfirmation_StillChargesTheUnconfirmedChests()
    {
        var cost = T5.ScaledFormulaCost(Chests(50, confirmed: 1), 6, ChestCostValued);

        cost.Should().BeApproximately(49 * InfernalNetKey + Materials, 1, "the confirmed chest's coins are in the counts, its materials are not");
    }

    [Test]
    public void ScaledFormulaCost_AllConfirmed_ChargesOnlyTheMaterials()
        => T5.ScaledFormulaCost(Chests(2, confirmed: 2), 0.5, ChestCostValued)
            .Should().BeApproximately(2 * Materials, 1);

    /// <summary>
    /// Review probe (flag on): 5 chests, 4 confirmed at 3M each. Derived from the coins, the 12M counted as 5.15 chests at
    /// 2.328M and the 5th chest went free; the open count says 4.
    /// </summary>
    [Test]
    public void ScaledFormulaCost_ConfirmedAboveTheFullReputationPrice_StillChargesTheFifthChest()
        => T5.ScaledFormulaCost(Chests(5, confirmed: 4, coinsEach: 3_000_000), 1, ChestCostValued)
            .Should().BeApproximately(InfernalNetKey + 4 * Materials, 1);

    /// <summary>
    /// With FullValueRates off (production now) the chest cost is worth 0 when a rate is read: the confirmed opens are
    /// charged their booked coins here, besides their materials.
    /// </summary>
    [Test]
    public void ScaledFormulaCost_ChestCostValuedAtZero_ChargesTheBookedCoins()
        => T5.ScaledFormulaCost(Chests(2, confirmed: 2, coinsEach: 2_400_000), 0.5, PriceOf)
            .Should().BeApproximately(2 * (2_400_000 + Materials), 1);

    [Test]
    public void ScaledFormulaCost_ChestCostValuedAtZero_ChargesNoStrayDungeonChestCost()
    {
        var counts = Chests(1, confirmed: 1);
        counts[PseudoItems.DUNGEON_CHEST_COST] -= 8_000_000; // a dungeon chest cost attributed into the Kuudra period

        T5.ScaledFormulaCost(counts, 0.5, PriceOf).Should().BeApproximately(3_030_000 + Materials, 1,
            "at most the listed key price (+1%) per confirmed open");
    }

    [Test]
    public void ScaledFormulaCost_KeyTakenFromTheInventory_IsChargedInFull()
    {
        T5.ScaledFormulaCost(Chests(2, inventoryKeys: 1), 0.5, PriceOf).Should().BeApproximately(InfernalKeyValue + InfernalNetKey, 1,
            "one chest opened with an inventory key and booked its essence, the other estimated");
        T5.ScaledFormulaCost(Chests(0, inventoryKeys: 2), 0.5, PriceOf).Should().BeApproximately(2 * InfernalKeyValue, 1,
            "consumed keys count even without teeth");
    }

    [Test]
    public void ScaledFormulaCost_LowerTier_KeyFromTheInventoryCoversTheChestItsTeethStandFor()
    {
        var t2 = new KuudraT2Task();
        var hotKey = KuudraKeyCost.OfTier(2);
        var counts = new Dictionary<string, double> { ["KUUDRA_TEETH"] = 1, [hotKey.Tag] = -1, ["ESSENCE_CRIMSON"] = 200 };

        t2.ScaledFormulaCost(counts, 1, PriceOf).Should().BeApproximately(hotKey.Coins + 4 * 1_000 + 2 * 50_000, 1,
            "the one chest was opened with the key, nothing is estimated");
    }

    /// <summary>
    /// Review probe (flag off): T3 at 8 confirmed runs/h credited their essence with no key cost, because T3 estimates no
    /// chests and the chest cost was worth 0 (+4.72M/h).
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void ScaledFormulaCost_LowerTier_ChargesEveryConfirmedOpen(bool chestCostValued)
    {
        var t3 = new KuudraT3Task();
        var burning = KuudraKeyCost.OfTier(3);
        var counts = new Dictionary<string, double>
        {
            ["ESSENCE_CRIMSON"] = 4_000, [PseudoItems.DUNGEON_CHEST_COST] = -8 * 600_000, [PseudoItems.KUUDRA_PAID_CHEST] = -8
        };
        var materials = 16 * 1_000 + 2 * 50_000;

        var cost = t3.ScaledFormulaCost(counts, 1, chestCostValued ? ChestCostValued : PriceOf);

        cost.Should().BeApproximately(chestCostValued ? 8 * materials : 8 * (600_000 + materials), 1);
        burning.Coins.Should().BeLessThan(600_000);
    }

    [Test]
    public void FilterOwnedItems_KeepsTheConfirmedOpenCount_AndItIsNoLoot()
    {
        var items = new Dictionary<string, int> { [PseudoItems.KUUDRA_PAID_CHEST] = -2, ["ESSENCE_CRIMSON"] = 4_000 };

        TaskPeriodFolder.FilterOwnedItems(items, T5).Should().BeEquivalentTo(items);
        PseudoItems.TryGetCoinValue(PseudoItems.KUUDRA_PAID_CHEST, out var value).Should().BeTrue();
        value.Should().Be(0);
        T5.ProducesItem(PseudoItems.KUUDRA_PAID_CHEST).Should().BeFalse();
    }

    [Test]
    public void RowTrimming_KeepsTheConfirmedOpenCount()
    {
        var counts = Enumerable.Range(0, 40).ToDictionary(i => $"LOOT_{i}", i => 1_000.0 + i);
        counts[PseudoItems.KUUDRA_PAID_CHEST] = -3;

        TaskAggregateService.KeepCostsAndTop(counts, 20, tag => 1).Should().ContainKey(PseudoItems.KUUDRA_PAID_CHEST);
    }

    [Test]
    public void ScaledFormulaCost_KeyValueResolvesBeforeTheMarketPrice()
    {
        double WithBogusKeyPrice(string tag) => tag == InfernalKey ? 99 : PriceOf(tag);

        T5.ScaledFormulaCost(Chests(1), 1, WithBogusKeyPrice).Should().BeApproximately(InfernalNetKey, 1);
    }

    [Test]
    public void TeethAreTheCostReference_SoRowTrimmingKeepsThem()
        => T5.CostReferenceTag(Chests(1)).Should().Be("KUUDRA_TEETH");

    [Test]
    public void KeyOfTier_OutsideTheTiers_IsNull()
    {
        KuudraKeyCost.OfTier(0).Should().BeNull();
        new KuudraChestClaimsTask().ScaledFormulaCost(Chests(5), 1, PriceOf).Should().Be(0);
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

        TaskPeriodFolder.FilterOwnedItems(items, new F3Task()).Should().NotContainKey(supply);
    }

    /// <summary>Dungeon Secrets drop the Dungeon Chest Key (0.6%) from Floor IV on (wiki Dungeon_Chest_Key).</summary>
    [Test]
    public void Dungeon_ChestKeyFromASecret_IsLootFromFloorFour()
    {
        var found = new Dictionary<string, int> { ["DUNGEON_CHEST_KEY"] = 1, ["ESSENCE_WITHER"] = 300 };
        var bought = new Dictionary<string, int> { ["DUNGEON_CHEST_KEY"] = 1, [PseudoItems.BAZAAR_PURCHASE] = 2_000_000 };

        TaskPeriodFolder.FilterOwnedItems(found, new F5Task()).Should().ContainKey("DUNGEON_CHEST_KEY");
        TaskPeriodFolder.FilterOwnedItems(found, new M4Task()).Should().ContainKey("DUNGEON_CHEST_KEY");
        TaskPeriodFolder.FilterOwnedItems(bought, new F5Task()).Should().NotContainKey("DUNGEON_CHEST_KEY", "bought to be used");
    }

    [Test]
    public void Dungeon_ChestKeyHasNoFormulaRate()
    {
        var m5 = new M5Task();

        m5.FormulaCostsForTest.Should().BeEmpty("a second chest is the player's choice");
        m5.ScaledFormulaCost(new() { ["ESSENCE_WITHER"] = 300, ["DUNGEON_CHEST_KEY"] = -2 }, 1, _ => 1_000_000).Should().Be(0);
    }
}
