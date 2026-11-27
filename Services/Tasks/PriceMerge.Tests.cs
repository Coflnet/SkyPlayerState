using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Model;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class PriceMergeTests
{
    private static ItemPrice Bazaar(string tag, double sell) => new() { ProductId = tag, SellPrice = sell };

    [Test]
    public void BazaarSellPriceZero_IsSkippedAndCleanPriceUsed()
    {
        var result = PriceMerge.Merge(new[] { Bazaar("A", 0) }, new Dictionary<string, double> { { "A", 500 } }, 1);

        result["A"].Should().Be(500);
    }

    [Test]
    public void BazaarSellPriceZeroWithoutCleanPrice_IsAbsentInsteadOfZero()
    {
        PriceMerge.Merge(new[] { Bazaar("A", 0) }, new Dictionary<string, double>(), 1).Should().NotContainKey("A");
    }

    [Test]
    public void CleanPrice_DoesNotOverrideBazaar()
    {
        var result = PriceMerge.Merge(new[] { Bazaar("A", 10) }, new Dictionary<string, double> { { "A", 500 }, { "B", 7 } }, 1);

        result["A"].Should().Be(10);
        result["B"].Should().Be(7);
    }

    [Test]
    public void Copper_IsStillSet()
    {
        PriceMerge.Merge(null, null, 1234)[PseudoItems.COPPER].Should().Be(1234);
    }
}
