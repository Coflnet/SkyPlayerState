using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Bazaar.Client.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression coverage for GetBazaarPrices: TaskExecutionService.BuildParameters used to set
/// <see cref="TaskParams.BazaarPrices"/> to a permanently empty list instead of fetching it, so every
/// bazaar-buy/sell-reading task (see CraftingTasks.cs and CraftingTasksBazaarPricesTests) always
/// computed with 0 - see GetBazaarPrices itself for the fix.
/// </summary>
public class TaskPriceServiceTests
{
    [Test]
    public async Task GetBazaarPrices_ReturnsPricesFromTheBazaarApi()
    {
        var prices = new List<ItemPrice>
        {
            new() { ProductId = "BLADESOUL_FRAGMENT", BuyPrice = 100, SellPrice = 90 },
            new() { ProductId = "BLADESOUL_BLADE", BuyPrice = 1100, SellPrice = 1000 }
        };
        var bazaarApi = new Mock<IBazaarApi>();
        bazaarApi.Setup(b => b.GetAllPricesAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(prices);

        var service = new TaskPriceService(null, bazaarApi.Object, NullLogger<TaskPriceService>.Instance);
        var result = await service.GetBazaarPrices();

        result.Should().BeEquivalentTo(prices,
            "TaskParams.BazaarPrices must actually be populated from the bazaar client, not left as an empty placeholder");
    }

    [Test]
    public async Task GetBazaarPrices_ApiUnavailableAndNothingCachedYet_ReturnsEmptyInsteadOfThrowing()
    {
        var bazaarApi = new Mock<IBazaarApi>();
        bazaarApi.Setup(b => b.GetAllPricesAsync(0, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("bazaar unavailable"));

        var service = new TaskPriceService(null, bazaarApi.Object, NullLogger<TaskPriceService>.Instance);
        var result = await service.GetBazaarPrices();

        result.Should().NotBeNull().And.BeEmpty(
            "a first-ever failed fetch has no prior value to fall back to, so it must degrade to empty, not throw and take the whole task run down with it");
    }
}
