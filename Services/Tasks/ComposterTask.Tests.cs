using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: an unpriced fuel/crop matter (price 0) used to win the "cheapest per unit" pick,
/// GetBestFlip returned (null, null, 0) and the task threw ArgumentNullException looking up the
/// null tag in Names - seen live for every player once SkyPlayerState's price list lacked a fossil.
/// </summary>
public class ComposterTaskTests
{
    private static TaskParams MakeParams(Dictionary<string, long> cleanPrices)
    {
        var services = new ServiceCollection().AddSingleton<ComposterService>().BuildServiceProvider();
        return new TaskParams
        {
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<System.Type, TaskParams.CalculationCache>(),
            CleanPrices = cleanPrices,
            BazaarPrices = new List<ItemPrice>(),
            Names = new Dictionary<string, string>(),
            ServiceProvider = services
        };
    }

    [Test]
    public void GetBestFlip_IgnoresUnpricedItems()
    {
        var service = new ComposterService();
        // only VOLTA is priced among fuels, only one matter is priced - unpriced ones must not win
        var prices = new Dictionary<string, float> { { "VOLTA", 10_000 }, { "COMPOST", 100_000 } };
        var anyMatter = new List<string>(service.MatterTable.Keys)[0];
        prices[anyMatter] = 1_000;

        var (crop, fuel, _) = service.GetBestFlip(prices, 100_000, new Composter());

        fuel.Should().Be("VOLTA");
        crop.Should().Be(anyMatter);
    }

    [Test]
    public async Task Execute_WithMissingPrices_ReportsUnavailableInsteadOfThrowing()
    {
        var result = await new ComposterTask().Execute(MakeParams(new() { { "COMPOST", 100_000 } }));

        result.ProfitPerHour.Should().Be(0);
        result.Message.Should().Contain("unavailable");
    }
}
