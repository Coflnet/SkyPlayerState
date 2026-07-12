using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Merged clean price lookup (bazaar sell overlaid with auction clean prices)
/// for the estimate endpoint, cached so each request does not refetch.
/// </summary>
public class TaskPriceService
{
    private readonly ISniperApi sniperApi;
    private readonly IBazaarApi bazaarApi;
    private readonly ILogger<TaskPriceService> logger;
    private Dictionary<string, double> cached;
    private DateTime fetchedAt = DateTime.MinValue;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public TaskPriceService(ISniperApi sniperApi, IBazaarApi bazaarApi, ILogger<TaskPriceService> logger)
    {
        this.sniperApi = sniperApi;
        this.bazaarApi = bazaarApi;
        this.logger = logger;
    }

    public async Task<Dictionary<string, double>> GetPrices()
    {
        if (cached != null && DateTime.UtcNow - fetchedAt < CacheDuration)
            return cached;
        await refreshLock.WaitAsync();
        try
        {
            if (cached != null && DateTime.UtcNow - fetchedAt < CacheDuration)
                return cached;
            var prices = new Dictionary<string, double>();
            // bound each dependency so one being slow or down degrades to partial prices
            // instead of hanging the estimate request
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                var bazaar = await bazaarApi.GetAllPricesAsync(0, cts.Token);
                foreach (var item in bazaar)
                    prices[item.ProductId] = (double)item.SellPrice;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "bazaar prices unavailable for task estimates");
            }
            try
            {
                var clean = await sniperApi.ApiSniperPricesCleanGetAsync(0, cts.Token);
                foreach (var item in clean)
                    if (item.Value > 0)
                        prices[item.Key] = item.Value;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "sniper clean prices unavailable for task estimates");
            }
            cached = prices;
            fetchedAt = DateTime.UtcNow;
            return cached;
        }
        catch (Exception e)
        {
            fetchedAt = DateTime.UtcNow - CacheDuration + TimeSpan.FromSeconds(15);
            logger.LogError(e, "failed to refresh task prices, serving {count} stale", cached?.Count ?? 0);
            return cached ?? new Dictionary<string, double>();
        }
    }
}
