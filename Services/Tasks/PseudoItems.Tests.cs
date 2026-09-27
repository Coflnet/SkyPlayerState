using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class PseudoItemsTests
{
    [Test]
    public void TryGetCoinValue_EvidenceTags_AreZero()
    {
        PseudoItems.TryGetCoinValue(PseudoItems.BAZAAR_PURCHASE, out var value).Should().BeTrue();
        value.Should().Be(0);
        PseudoItems.TryGetCoinValue(PseudoItems.AUCTION_PURCHASE, out value).Should().BeTrue();
        value.Should().Be(0);
    }

    [Test]
    public void TryGetCoinValue_CostTag_IsOnePerUnit()
    {
        PseudoItems.TryGetCoinValue(PseudoItems.DUNGEON_CHEST_COST, out var value).Should().BeTrue();
        value.Should().Be(1);
    }

    [Test]
    public void TryGetCoinValue_RealItem_ReturnsFalse()
    {
        PseudoItems.TryGetCoinValue("HYPERION", out var value).Should().BeFalse("it is not a pseudo tag, callers should fall back to their own price lookup");
        value.Should().Be(0);
    }

    [Test]
    public void ClassifierWeight_PseudoTags_AreOnePerUnit_RegardlessOfPrices()
    {
        var prices = new Dictionary<string, double> { { PseudoItems.BAZAAR_PURCHASE, 999 } };
        PseudoItems.ClassifierWeight(PseudoItems.BAZAAR_PURCHASE, prices).Should().Be(1,
            "a pseudo tag's weight comes from its own count (coins spent/cost), never a market price");
        PseudoItems.ClassifierWeight(PseudoItems.DUNGEON_CHEST_COST, null).Should().Be(1);
    }

    [Test]
    public void ClassifierWeight_RealItem_UsesPriceLookup()
    {
        var prices = new Dictionary<string, double> { { "HYPERION", 970_000_000 } };
        PseudoItems.ClassifierWeight("HYPERION", prices).Should().Be(970_000_000);
        PseudoItems.ClassifierWeight("SOME_UNPRICED_ITEM", prices).Should().Be(0);
        PseudoItems.ClassifierWeight("HYPERION", null).Should().Be(0);
    }

    // ── CoinValueRegistry routing (Value() must check PseudoItems before the override/market/unpriced path) ──

    [Test]
    public void CoinValueRegistry_Value_EvidenceTag_IsZero_WithoutTouchingUnpricedMetric()
    {
        var registry = new CoinValueRegistry(null!, NullLogger<CoinValueRegistry>.Instance);
        var before = CoinValueRegistry.UnpricedCounterValueForTest(PseudoItems.BAZAAR_PURCHASE);

        var value = registry.Value(PseudoItems.BAZAAR_PURCHASE, new Dictionary<string, double>());

        value.Should().Be(0);
        CoinValueRegistry.UnpricedCounterValueForTest(PseudoItems.BAZAAR_PURCHASE).Should().Be(before,
            "an EVIDENCE pseudo tag has a defined value of 0 - it must not be recorded as an unpriced real resource");
    }

    [Test]
    public void CoinValueRegistry_Value_CostTag_IsOnePerUnit()
    {
        var registry = new CoinValueRegistry(null!, NullLogger<CoinValueRegistry>.Instance);
        registry.Value(PseudoItems.DUNGEON_CHEST_COST, new Dictionary<string, double>()).Should().Be(1);
    }
}
