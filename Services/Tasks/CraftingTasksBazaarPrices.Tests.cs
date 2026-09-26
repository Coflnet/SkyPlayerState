using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression coverage for BazaarCraftTask (CraftingTasks.cs) reading
/// <see cref="TaskParams.BazaarPrices"/> - TaskExecutionService.BuildParameters used to always set
/// it to an empty list (see TaskPriceService.Tests.cs for the fix), so every bazaar-buy/sell-reading
/// crafting task always computed with a buy/sell price of 0 regardless of the real market.
/// </summary>
public class CraftingTasksBazaarPricesTests
{
    private static TaskParams MakeParams(List<ItemPrice> bazaarPrices)
    {
        return new TaskParams
        {
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<System.Type, TaskParams.CalculationCache>(),
            CleanPrices = new Dictionary<string, long>(),
            BazaarPrices = bazaarPrices,
            Names = new Dictionary<string, string>()
        };
    }

    [Test]
    public async Task Execute_WithPopulatedBazaarPrices_ComputesNonZeroProfit()
    {
        // BladeSoulBzTask: InputPerCraft = 8, CraftsPerHour = 120 - buy 8 fragments per blade,
        // sell price comfortably above 8x the buy price so the craft is profitable.
        var parameters = MakeParams(new List<ItemPrice>
        {
            new() { ProductId = "BLADESOUL_FRAGMENT", BuyPrice = 100, SellPrice = 90 },
            new() { ProductId = "BLADESOUL_BLADE", BuyPrice = 1_100, SellPrice = 1_000 }
        });

        var result = await new BladeSoulBzTask().Execute(parameters);

        // profitPerCraft = 1000 - 100*8 = 200; profitPerHour = 200*120 = 24_000
        result.ProfitPerHour.Should().Be(24_000,
            "the task must actually use the fetched bazaar buy/sell prices instead of treating them as unavailable");
    }

    [Test]
    public async Task Execute_WithEmptyBazaarPrices_ReportsUnavailableInsteadOfAPhantomZeroProfit()
    {
        // The pre-fix behaviour: TaskParams.BazaarPrices was always `new()`, so every such task
        // silently reported "not currently profitable" no matter the real market.
        var parameters = MakeParams(new List<ItemPrice>());

        var result = await new BladeSoulBzTask().Execute(parameters);

        result.ProfitPerHour.Should().Be(0);
        result.Message.Should().Contain("price data unavailable");
    }
}
