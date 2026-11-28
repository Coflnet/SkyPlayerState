using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class KuudraRewardAttributionTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
    private const string T5 = "Kuudra's Hollow (T5)";
    // a paid chest's armor piece is the claim evidence; tentacles and an untyped book alone are none (see HasClaimEvidence)
    private static readonly Dictionary<string, int> Loot = new() { { "KUUDRA_TENTACLE", 10 }, { "ENCHANTED_BOOK", 5 }, { "TERROR_CHESTPLATE", 1 } };

    [TestCase("Dungeon Hub")]
    [TestCase("Forgotten Skull")]
    public void ClaimLoot_WithinWindow_ResolvesToLastTier(string location)
    {
        KuudraRewardAttribution.ResolveLocation(location, Loot, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    /// <summary>
    /// Regression (production, 7 days of T5): a Dungeon Hub period holding only teeth moved from storage or a bazaar order
    /// was attributed as a T5 chest claim, 1,730 teeth against 7 in the tier zone.
    /// </summary>
    [TestCase("Dungeon Hub")]
    [TestCase("Forgotten Skull")]
    public void OnlyTeeth_AfterATierRun_NotAttributed(string location)
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 500 } };
        KuudraRewardAttribution.ResolveLocation(location, items, T5, Base, Base.AddMinutes(3)).Should().Be(location);
    }

    [Test]
    public void OnlyBulkChestLoot_NotAttributed()
    {
        var items = new Dictionary<string, int>
        {
            { "KUUDRA_TEETH", 40 }, { "ESSENCE_CRIMSON", 2_000 }, { "KUUDRA_MANDIBLE", 2 }, { "KUUDRA_TENTACLE", 3 }, { "SHARD_KRAKEN", 1 }
        };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be("Dungeon Hub");
    }

    [Test]
    public void TeethWithATerrorChestplate_Attributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 3 }, { "TERROR_CHESTPLATE", 1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [TestCase("MOLTEN_BELT")]
    [TestCase("ENCHANTMENT_ULTIMATE_FATAL_TEMPO_1")]
    [TestCase("ENCHANTMENT_STRONG_MANA_4")]
    public void TeethWithPaidChestEquipmentOrBook_Attributed(string slotLoot)
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 3 }, { slotLoot, 1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [Test]
    public void TeethWithAConfirmedPaidChestOpen_Attributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 3 }, { PseudoItems.KUUDRA_PAID_CHEST, -1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    /// <summary>
    /// Review probe: a paid chest opened with a key from the inventory books no KUUDRA_PAID_CHEST (only its essence), and
    /// its shard is no slot item, so the claim stayed unattributed and its pending run time carried into the next claim.
    /// </summary>
    [Test]
    public void TeethWithAKeyUsedFromTheInventory_Attributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 3 }, { "KUUDRA_INFERNAL_TIER_KEY", -1 }, { "SHARD_KRAKEN", 1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [Test]
    public void ArmorPieceLeavingTheInventory_IsNoEvidence()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TEETH", 300 }, { "TERROR_CHESTPLATE", -1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be("Dungeon Hub");
    }

    [Test]
    public void ClaimLoot_AfterWindow_NotAttributed()
    {
        var start = Base + KuudraRewardAttribution.MaxClaimDelay + TimeSpan.FromMinutes(1);
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, T5, Base, start).Should().Be("Dungeon Hub");
    }

    [Test]
    public void NoKuudraLoot_NotAttributed()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 40 }, { "KUUDRA_TENTACLE", -1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be("Dungeon Hub");
    }

    [Test]
    public void NoKnownTier_NotAttributed()
    {
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, null, default, Base).Should().Be("Dungeon Hub");
    }

    [Test]
    public void KuudraLoot_BeatsCatacombsFloorResolution()
    {
        // the listener applies the Dungeon resolution first, then the Kuudra one over the raw location
        var floor = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", "The Catacombs (F4)", Base, Base.AddMinutes(3));
        floor.Should().Be("The Catacombs (F4)");
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [TestCase("KUUDRA_TIER_KEY", 1)]
    [TestCase("KUUDRA_HOT_TIER_KEY", 2)]
    [TestCase("KUUDRA_BURNING_TIER_KEY", 3)]
    [TestCase("KUUDRA_FIERY_TIER_KEY", 4)]
    [TestCase("KUUDRA_INFERNAL_TIER_KEY", 5)]
    public void KeyPurchase_ResolvesToMatchingTier(string key, int tier)
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_MYCELIUM", -3840 }, { "NETHER_STAR", -96 }, { key, 4 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base)
            .Should().Be($"Kuudra's Hollow (T{tier})");
    }

    [Test]
    public void KeyPurchase_HighestTierWins()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_HOT_TIER_KEY", 1 }, { "KUUDRA_FIERY_TIER_KEY", 2 }, { "KUUDRA_TIER_KEY", 3 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base).Should().Be("Kuudra's Hollow (T4)");
    }

    [Test]
    public void KeyConsumed_NotAttributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_INFERNAL_TIER_KEY", -1 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base).Should().Be("Scarleton");
    }

    [Test]
    public void KeyPurchaseWithMaterialsOnly_ClassifiesToKuudraTierNotMycelium()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_MYCELIUM", -3840 }, { "NETHER_STAR", -96 }, { "KUUDRA_INFERNAL_TIER_KEY", 4 } };
        var location = KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base);
        new TaskClassifier(new TaskRegistry()).Classify(location, items, 10)!.TaskName.Should().Be("Kuudra T5");
    }

    [Test]
    public void ClaimLootOnly_ResolvedToTier_ClassifiesToKuudraTier()
    {
        var location = KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, "Kuudra's Hollow (T4)", Base, Base.AddMinutes(3));
        new TaskClassifier(new TaskRegistry()).Classify(location, Loot, 10)!.TaskName.Should().Be("Kuudra T4");
    }

    [TestCase("Village")]
    [TestCase("Hub")]
    public void KeyGainOutsideTheCrimsonIsle_AloneOrWithPseudoTags_ResolvesToTier(string location)
    {
        var items = new Dictionary<string, int>
        {
            { "KUUDRA_HOT_TIER_KEY", 1 }, { "KUUDRA_TIER_KEY", -1 }, { "ENCHANTED_BOOK", -2 },
            { PseudoItems.BAZAAR_PURCHASE, 5 }
        };
        KuudraRewardAttribution.ResolveLocation(location, items, null, default, Base).Should().Be("Kuudra's Hollow (T2)");
    }

    /// <summary>
    /// Regression (audit 2026-11-27): keys are lava fishing treasure, and a Crimson Isle fishing period that caught one
    /// was moved to the tier zone with all its fish (4-9 such periods per tier).
    /// </summary>
    [TestCase("Blazing Volcano")]
    [TestCase("Scarleton")]
    public void KeyCaughtWhileLavaFishingOnTheCrimsonIsle_StaysWhereItIs(string location)
    {
        var items = new Dictionary<string, int> { { "KUUDRA_TIER_KEY", 1 }, { "MAGMA_FISH", 300 }, { "LUMP_OF_MAGMA", 12 } };
        KuudraRewardAttribution.ResolveLocation(location, items, null, default, Base).Should().Be(location);
    }

    [Test]
    public void KeyGainOutsideTheCrimsonIsle_WithOtherRealItem_NotAttributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_HOT_TIER_KEY", 1 }, { "ENCHANTED_WHEAT", 300 } };
        KuudraRewardAttribution.ResolveLocation("Village", items, null, default, Base).Should().Be("Village");
    }

    [Test]
    public void EmptyTierFragmentsAccumulate_AndAClaimConsumesThem()
    {
        var now = Base;
        var pending = KuudraRewardAttribution.UpdatePendingRun(0, now, now, true, TimeSpan.FromMinutes(3), false, out var shift);
        pending = KuudraRewardAttribution.UpdatePendingRun(pending, now, now, true, TimeSpan.FromMinutes(2), false, out shift);
        pending.Should().Be(300);
        shift.Should().Be(TimeSpan.Zero);

        pending = KuudraRewardAttribution.UpdatePendingRun(pending, now, now.AddMinutes(1), false, TimeSpan.FromSeconds(8), true, out shift);
        pending.Should().Be(0);
        shift.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Test]
    public void StalePendingIsDropped_AndKeyOnlyPeriodDoesNotConsume()
    {
        var stale = Base + KuudraRewardAttribution.MaxClaimDelay + TimeSpan.FromMinutes(1);
        KuudraRewardAttribution.UpdatePendingRun(300, Base, stale, false, TimeSpan.FromSeconds(8), true, out var shift).Should().Be(0);
        shift.Should().Be(TimeSpan.Zero);

        // a key purchase is not a claim (claimedToTier false): pending stays
        KuudraRewardAttribution.UpdatePendingRun(300, Base, Base.AddMinutes(5), false, TimeSpan.FromSeconds(8), false, out shift).Should().Be(300);
        shift.Should().Be(TimeSpan.Zero);
    }

    [TestCase(0)]
    [TestCase(-30)]
    [TestCase(7200)]
    public void NonPositiveOrAbsurdFragmentsAreIgnored(int seconds)
    {
        KuudraRewardAttribution.UpdatePendingRun(10, Base, Base, true, TimeSpan.FromSeconds(seconds), false, out _).Should().Be(10);
    }

    [Test]
    public void IsClaimLocation_OnlyForHubAndSkull()
    {
        KuudraRewardAttribution.IsClaimLocation("Dungeon Hub").Should().BeTrue();
        KuudraRewardAttribution.IsClaimLocation("Forgotten Skull").Should().BeTrue();
        KuudraRewardAttribution.IsClaimLocation("Scarleton").Should().BeFalse();
    }
}
