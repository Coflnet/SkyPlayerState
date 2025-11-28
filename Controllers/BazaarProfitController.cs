using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Bazaar;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Controller for bazaar profit tracking endpoints
/// </summary>
[ApiController]
[Route("[controller]")]
public class BazaarProfitController : ControllerBase
{
    private readonly IBazaarProfitTracker _profitTracker;

    /// <summary>
    /// Creates a new instance of <see cref="BazaarProfitController"/>
    /// </summary>
    public BazaarProfitController(IBazaarProfitTracker profitTracker)
    {
        _profitTracker = profitTracker;
    }

    /// <summary>
    /// Gets the list of bazaar flips (completed buy-sell cycles) for a player
    /// </summary>
    /// <param name="playerUuid">The player UUID</param>
    /// <param name="from">Optional start of time range (inclusive). Defaults to 7 days ago if omitted.</param>
    /// <param name="to">Optional end of time range (inclusive). Defaults to now if omitted.</param>
    /// <param name="limit">Maximum number of flips to return (default 100)</param>
    /// <returns>List of completed bazaar flips with profit information</returns>
    [HttpGet]
    [Route("flips/{playerUuid}")]
    public async Task<List<BazaarFlip>> GetFlips(Guid playerUuid, DateTime? from = null, DateTime? to = null, int limit = 100)
    {
        return await _profitTracker.GetFlips(playerUuid, from, to, limit);
    }

    /// <summary>
    /// Gets the outstanding (unsold) buy orders for a player.
    /// These are items that were bought but not yet sold.
    /// Orders expire after 2 weeks.
    /// </summary>
    /// <param name="playerUuid">The player UUID</param>
    /// <returns>List of outstanding buy orders</returns>
    [HttpGet]
    [Route("outstanding/{playerUuid}")]
    public async Task<List<BazaarBuyRecord>> GetOutstandingOrders(Guid playerUuid)
    {
        return await _profitTracker.GetOutstandingOrders(playerUuid);
    }

    /// <summary>
    /// Gets a summary of bazaar profit for a player
    /// </summary>
    /// <param name="playerUuid">The player UUID</param>
    /// <param name="from">Optional start of time range (inclusive). Defaults to 7 days ago if omitted.</param>
    /// <param name="to">Optional end of time range (inclusive). Defaults to now if omitted.</param>
    /// <param name="limit">Maximum number of flips to analyze (default 100)</param>
    /// <returns>Summary of profit from bazaar flips</returns>
    [HttpGet]
    [Route("summary/{playerUuid}")]
    public async Task<BazaarProfitSummary> GetProfitSummary(Guid playerUuid, DateTime? from = null, DateTime? to = null, int limit = 100)
    {
        var flips = await _profitTracker.GetFlips(playerUuid, from, to, limit);
        var outstanding = await _profitTracker.GetOutstandingOrders(playerUuid);
        
        var totalProfit = 0L;
        var totalBought = 0L;
        var totalSold = 0L;
        var flipCount = 0;
        
        foreach (var flip in flips)
        {
            totalProfit += flip.Profit;
            totalBought += flip.BuyPrice;
            totalSold += flip.SellPrice;
            flipCount++;
        }
        
        var outstandingValue = 0L;
        var outstandingItems = 0;
        foreach (var order in outstanding)
        {
            outstandingValue += (long)((double)order.TotalPrice * order.RemainingAmount / order.Amount);
            outstandingItems += order.RemainingAmount;
        }
        
        return new BazaarProfitSummary
        {
            TotalProfit = totalProfit / 10.0,
            TotalBought = totalBought / 10.0,
            TotalSold = totalSold / 10.0,
            FlipCount = flipCount,
            OutstandingValue = outstandingValue / 10.0,
            OutstandingItemCount = outstandingItems,
            OutstandingOrderCount = outstanding.Count
        };
    }
}

/// <summary>
/// Summary of bazaar profit for a player
/// </summary>
public class BazaarProfitSummary
{
    /// <summary>
    /// Total profit from all tracked flips (in coins)
    /// </summary>
    public double TotalProfit { get; set; }
    
    /// <summary>
    /// Total amount spent on buy orders (in coins)
    /// </summary>
    public double TotalBought { get; set; }
    
    /// <summary>
    /// Total amount received from sell orders (in coins)
    /// </summary>
    public double TotalSold { get; set; }
    
    /// <summary>
    /// Number of completed flips
    /// </summary>
    public int FlipCount { get; set; }
    
    /// <summary>
    /// Value of outstanding (unsold) items (in coins)
    /// </summary>
    public double OutstandingValue { get; set; }
    
    /// <summary>
    /// Number of outstanding items
    /// </summary>
    public int OutstandingItemCount { get; set; }
    
    /// <summary>
    /// Number of outstanding orders
    /// </summary>
    public int OutstandingOrderCount { get; set; }
}
