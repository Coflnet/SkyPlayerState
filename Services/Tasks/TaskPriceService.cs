using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Bazaar.Client.Model;
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

    private static readonly TimeSpan BazaarPricesCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BazaarPricesTimeout = TimeSpan.FromSeconds(2);
    private List<ItemPrice> cachedBazaarPrices;
    private DateTime bazaarPricesFetchedAt = DateTime.MinValue;
    private readonly SemaphoreSlim bazaarPricesRefreshLock = new(1, 1);

    public TaskPriceService(ISniperApi sniperApi, IBazaarApi bazaarApi, ILogger<TaskPriceService> logger)
    {
        this.sniperApi = sniperApi;
        this.bazaarApi = bazaarApi;
        this.logger = logger;
    }

    public async Task<Dictionary<string, double>> GetPrices(CancellationToken cancellationToken = default)
    {
        if (cached != null && DateTime.UtcNow - fetchedAt < CacheDuration)
            return cached;
        if (!await refreshLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            return cached ?? new Dictionary<string, double>();
        try
        {
            if (cached != null && DateTime.UtcNow - fetchedAt < CacheDuration)
                return cached;
            var prices = new Dictionary<string, double>();
            // Bound the whole refresh so unavailable price services cannot hold up estimates.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var bazaarTask = bazaarApi.GetAllPricesAsync(0, cts.Token);
            var cleanTask = sniperApi.ApiSniperPricesCleanGetAsync(0, cts.Token);
            try
            {
                var bazaar = await bazaarTask.WaitAsync(cts.Token);
                foreach (var item in bazaar)
                    prices[item.ProductId] = (double)item.SellPrice;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "bazaar prices unavailable for task estimates");
            }
            try
            {
                var clean = await cleanTask.WaitAsync(cts.Token);
                foreach (var item in clean)
                    if (item.Value > 0)
                        prices[item.Key] = item.Value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "sniper clean prices unavailable for task estimates");
            }
            cached = prices;
            fetchedAt = DateTime.UtcNow;
            return cached;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            fetchedAt = DateTime.UtcNow - CacheDuration + TimeSpan.FromSeconds(15);
            logger.LogError(e, "failed to refresh task prices, serving {count} stale", cached?.Count ?? 0);
            return cached ?? new Dictionary<string, double>();
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Raw bazaar prices (buy and sell, per product) for <see cref="Tasks.TaskParams.BazaarPrices"/> -
    /// used by the bazaar crafting tasks (see CraftingTasks.cs) that need both sides of the spread
    /// rather than the single merged clean price <see cref="GetPrices"/> returns. Cached the same way
    /// as <see cref="TaskExecutionService.GetNames"/>: a short TTL, and on a timeout/failure the last
    /// good value is served (falling back to an empty list only when nothing has ever been fetched).
    /// </summary>
    public async Task<List<ItemPrice>> GetBazaarPrices(CancellationToken cancellationToken = default)
    {
        if (cachedBazaarPrices != null && DateTime.UtcNow - bazaarPricesFetchedAt < BazaarPricesCacheDuration)
            return cachedBazaarPrices;
        if (!await bazaarPricesRefreshLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            return cachedBazaarPrices ?? new List<ItemPrice>();
        try
        {
            if (cachedBazaarPrices != null && DateTime.UtcNow - bazaarPricesFetchedAt < BazaarPricesCacheDuration)
                return cachedBazaarPrices;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(BazaarPricesTimeout);
            var prices = await bazaarApi.GetAllPricesAsync(0, cts.Token);
            cachedBazaarPrices = prices?.ToList() ?? new List<ItemPrice>();
            bazaarPricesFetchedAt = DateTime.UtcNow;
            return cachedBazaarPrices;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            bazaarPricesFetchedAt = DateTime.UtcNow - BazaarPricesCacheDuration + TimeSpan.FromSeconds(30);
            logger.LogWarning(e, "bazaar prices unavailable for task results, serving {count} stale", cachedBazaarPrices?.Count ?? 0);
            return cachedBazaarPrices ?? new List<ItemPrice>();
        }
        finally
        {
            bazaarPricesRefreshLock.Release();
        }
    }
}
