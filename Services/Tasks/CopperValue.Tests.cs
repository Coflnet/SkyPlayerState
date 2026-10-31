using Moq;
using System.Threading.Tasks;
using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class CopperValueTests
{
    [TearDown]
    public void Reset() => CopperValue.Reset();

    private static NpcCost Row(string tag, int copper, int count = 1, params (string, int)[] extra)
    {
        var row = new NpcCost { ItemTag = tag, NpcName = "SkyMart Farming Tools", ResultCount = count };
        row.Costs["Copper"] = copper;
        foreach (var (k, v) in extra)
            row.Costs[k] = v;
        return row;
    }

    [Test]
    public void Derive_TakesTheMedianRatio()
    {
        var rows = new[] { Row("A", 1000), Row("B", 1000), Row("C", 1000), Row("D", 1000), Row("E", 1000) };
        var prices = new Dictionary<string, double> { ["A"] = 1_000_000, ["B"] = 2_000_000, ["C"] = 3_000_000, ["D"] = 4_000_000, ["E"] = 900_000_000 };

        var info = CopperValue.Derive(rows, prices);

        info.IsFallback.Should().BeFalse();
        info.CoinsPerCopper.Should().Be(3000); // outlier E does not matter
        info.Items.Should().HaveCount(5);
    }

    [Test]
    public void Derive_EvenCountAveragesTheMiddle_AndResultCountScales()
    {
        var rows = new[] { Row("A", 1000), Row("B", 1000), Row("C", 1000), Row("D", 500, 2) };
        var prices = new Dictionary<string, double> { ["A"] = 1_000_000, ["B"] = 2_000_000, ["C"] = 3_000_000, ["D"] = 1_000_000 };
        // ratios 1000, 2000, 3000, 1000*2/500=4000... D = 1_000_000*2/500 = 4000
        CopperValue.Derive(rows, prices).CoinsPerCopper.Should().Be(2500);
    }

    [Test]
    public void Derive_FiltersUnqualifiedRows()
    {
        var rows = new List<NpcCost>
        {
            Row("GOOD1", 1000), Row("GOOD2", 1000),
            Row("CHEAP", 5),                                  // below 100 copper
            Row("ENCHANTED_BOOK", 1500),                      // unknown enchant
            Row("MIXED", 10000, 1, ("Peony Cloak", 1)),       // not copper only
            Row("NOPRICE", 1000), Row("ZEROPRICE", 1000),
            Row("GOOD1", 1000),                               // duplicate tag
        };
        var prices = new Dictionary<string, double>
        {
            ["GOOD1"] = 1_000_000, ["GOOD2"] = 1_000_000, ["CHEAP"] = 96_000, ["ENCHANTED_BOOK"] = 5_000_000,
            ["MIXED"] = 50_000_000, ["ZEROPRICE"] = 0
        };

        var info = CopperValue.Derive(rows, prices);

        info.Items.Should().HaveCount(2);
        info.IsFallback.Should().BeTrue(); // only 2 qualify
        info.CoinsPerCopper.Should().Be(CopperValue.Fallback);
    }

    [Test]
    public void Derive_NothingQualifies_UsesFallback()
    {
        var info = CopperValue.Derive(new List<NpcCost>(), new Dictionary<string, double>());
        info.IsFallback.Should().BeTrue();
        info.CoinsPerCopper.Should().Be(1000);
    }

    [Test]
    public void CoinValueRegistry_ValuesCopperFromDerivedValue()
    {
        var registry = new CoinValueRegistry(null!, NullLogger<CoinValueRegistry>.Instance);
        registry.Value(PseudoItems.COPPER, new Dictionary<string, double>()).Should().Be(CopperValue.Fallback);

        CopperValue.Set(new CopperValueInfo { CoinsPerCopper = 1500 });
        registry.Value(PseudoItems.COPPER, null!).Should().Be(1500);
    }

    [Test]
    public void ShouldReplace_FallbackNeverOverwritesADerivedValue()
    {
        var derived = new CopperValueInfo { CoinsPerCopper = 1300, IsFallback = false };
        var fallback = new CopperValueInfo { CoinsPerCopper = CopperValue.Fallback, IsFallback = true };
        CopperValueRefresher.ShouldReplace(derived, fallback).Should().BeFalse();
        CopperValueRefresher.ShouldReplace(fallback, fallback).Should().BeTrue();
        CopperValueRefresher.ShouldReplace(fallback, derived).Should().BeTrue();
        CopperValueRefresher.ShouldReplace(derived, derived).Should().BeTrue();
    }

    [Test]
    public void ComputeProfit_ValuesCopperThroughThePriceMap()
    {
        var prices = new Dictionary<string, double> { { PseudoItems.COPPER, 1300 } };
        CollectionListener.ComputeProfit(new() { { PseudoItems.COPPER, 10 } }, prices).Should().Be(13_000);
    }

    [Test]
    public void ClassifierWeight_CopperIsZero()
        => PseudoItems.ClassifierWeight(PseudoItems.COPPER, new() { { PseudoItems.COPPER, 1300 } }).Should().Be(0);

    [Test]
    public async Task TaskPriceService_PricesContainCopper()
    {
        CopperValue.Set(new CopperValueInfo { CoinsPerCopper = 1234, IsFallback = false });
        var bazaar = new Mock<Coflnet.Sky.Bazaar.Client.Api.IBazaarApi>();
        bazaar.Setup(b => b.GetAllPricesAsync(0, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<Coflnet.Sky.Bazaar.Client.Model.ItemPrice>());
        var sniper = new Mock<Coflnet.Sky.Sniper.Client.Api.ISniperApi>();
        sniper.Setup(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, long>());
        var service = new TaskPriceService(sniper.Object, bazaar.Object, NullLogger<TaskPriceService>.Instance);
        (await service.GetPrices())[PseudoItems.COPPER].Should().Be(1234);
    }
}
