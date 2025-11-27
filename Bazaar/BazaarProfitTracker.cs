using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

/// <summary>
/// Represents a completed bazaar buy order that can be matched with sell orders for profit tracking
/// </summary>
public class BazaarBuyRecord
{
    /// <summary>
    /// Player UUID who made the purchase
    /// </summary>
    public Guid PlayerUuid { get; set; }
    /// <summary>
    /// Item tag (e.g., "COAL", "DIAMOND")
    /// </summary>
    public string ItemTag { get; set; } = null!;
    /// <summary>
    /// Amount of items purchased
    /// </summary>
    public int Amount { get; set; }
    /// <summary>
    /// Amount of items remaining (not yet sold)
    /// </summary>
    public int RemainingAmount { get; set; }
    /// <summary>
    /// Total price paid for the items (in coins * 10 for precision)
    /// </summary>
    public long TotalPrice { get; set; }
    /// <summary>
    /// Timestamp when the buy order was claimed
    /// </summary>
    public DateTime ClaimedAt { get; set; }
}

/// <summary>
/// Represents a completed bazaar flip (buy then sell for profit)
/// </summary>
public class BazaarFlip
{
    /// <summary>
    /// Player UUID who made the flip
    /// </summary>
    public Guid PlayerUuid { get; set; }
    /// <summary>
    /// Year when the flip was made (for partitioning)
    /// </summary>
    public int Year { get; set; }
    /// <summary>
    /// Item tag (e.g., "COAL", "DIAMOND")
    /// </summary>
    public string ItemTag { get; set; } = null!;
    /// <summary>
    /// Item display name
    /// </summary>
    public string ItemName { get; set; } = null!;
    /// <summary>
    /// Amount of items flipped
    /// </summary>
    public int Amount { get; set; }
    /// <summary>
    /// Total buy price (in coins * 10)
    /// </summary>
    public long BuyPrice { get; set; }
    /// <summary>
    /// Total sell price (in coins * 10)
    /// </summary>
    public long SellPrice { get; set; }
    /// <summary>
    /// Profit from the flip (sell - buy, in coins * 10)
    /// </summary>
    public long Profit { get; set; }
    /// <summary>
    /// When the sell order was claimed
    /// </summary>
    public DateTime SoldAt { get; set; }
}

public interface IBazaarProfitTracker
{
    /// <summary>
    /// Records a claimed buy order
    /// </summary>
    Task RecordBuyOrder(Guid playerUuid, string itemTag, int amount, long totalPrice, DateTime claimedAt);
    
    /// <summary>
    /// Records a claimed sell order and calculates profit by matching with buy orders
    /// </summary>
    Task<BazaarFlip?> RecordSellOrder(Guid playerUuid, string itemTag, string itemName, int amount, long totalPrice, DateTime claimedAt);
    
    /// <summary>
    /// Gets all flips for a player
    /// </summary>
    Task<List<BazaarFlip>> GetFlips(Guid playerUuid, int limit = 100);
    
    /// <summary>
    /// Gets outstanding (unsold) buy orders for a player
    /// </summary>
    Task<List<BazaarBuyRecord>> GetOutstandingOrders(Guid playerUuid);
}

public class BazaarProfitTracker : IBazaarProfitTracker
{
    private readonly ISession _session;
    private readonly ILogger<BazaarProfitTracker> _logger;
    private Table<BazaarBuyRecord>? _buyTable;
    private Table<BazaarFlip>? _flipTable;
    private static readonly TimeSpan BuyOrderTtl = TimeSpan.FromDays(14);

    public BazaarProfitTracker(ISession session, ILogger<BazaarProfitTracker> logger)
    {
        _session = session;
        _logger = logger;
    }

    private async Task EnsureTablesExist()
    {
        if (_buyTable != null && _flipTable != null)
            return;

        // Buy records table - partitioned by player+item for efficient querying
        var buyMapping = new MappingConfiguration()
            .Define(new Map<BazaarBuyRecord>()
                .TableName("bazaar_buy_records")
                .PartitionKey(t => t.PlayerUuid, t => t.ItemTag)
                .ClusteringKey(t => t.ClaimedAt, SortOrder.Ascending)
                .Column(t => t.Amount, cm => cm.WithName("amount"))
                .Column(t => t.RemainingAmount, cm => cm.WithName("remaining_amount"))
                .Column(t => t.TotalPrice, cm => cm.WithName("total_price"))
            );
        _buyTable = new Table<BazaarBuyRecord>(_session, buyMapping);
        await _buyTable.CreateIfNotExistsAsync();

        // Flip records table - partitioned by player and year to avoid overflow
        var flipMapping = new MappingConfiguration()
            .Define(new Map<BazaarFlip>()
                .TableName("bazaar_flips")
                .PartitionKey(t => t.PlayerUuid, t => t.Year)
                .ClusteringKey(t => t.SoldAt, SortOrder.Descending)
                .Column(t => t.ItemTag, cm => cm.WithName("item_tag"))
                .Column(t => t.ItemName, cm => cm.WithName("item_name"))
                .Column(t => t.Amount, cm => cm.WithName("amount"))
                .Column(t => t.BuyPrice, cm => cm.WithName("buy_price"))
                .Column(t => t.SellPrice, cm => cm.WithName("sell_price"))
                .Column(t => t.Profit, cm => cm.WithName("profit"))
            );
        _flipTable = new Table<BazaarFlip>(_session, flipMapping);
        await _flipTable.CreateIfNotExistsAsync();

        // Set TTL for buy records table (2 weeks)
        var ttlSeconds = (int)BuyOrderTtl.TotalSeconds;
        try
        {
            await _session.ExecuteAsync(new SimpleStatement(
                $"ALTER TABLE bazaar_buy_records WITH default_time_to_live = {ttlSeconds};"));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to set TTL on bazaar_buy_records table");
        }
    }

    public async Task RecordBuyOrder(Guid playerUuid, string itemTag, int amount, long totalPrice, DateTime claimedAt)
    {
        await EnsureTablesExist();

        var record = new BazaarBuyRecord
        {
            PlayerUuid = playerUuid,
            ItemTag = itemTag,
            Amount = amount,
            RemainingAmount = amount,
            TotalPrice = totalPrice,
            ClaimedAt = claimedAt.ToUniversalTime()
        };

        // Insert with TTL to ensure it expires even if amount changes
        var insert = _buyTable!.Insert(record);
        insert.SetTTL((int)BuyOrderTtl.TotalSeconds);
        await insert.ExecuteAsync();

        _logger.LogInformation("Recorded buy order for {Player}: {Amount}x {Item} for {Price}", 
            playerUuid, amount, itemTag, totalPrice / 10.0);
    }

    public async Task<BazaarFlip?> RecordSellOrder(Guid playerUuid, string itemTag, string itemName, int amount, long totalPrice, DateTime claimedAt)
    {
        await EnsureTablesExist();

        // Get all buy records for this player and item, ordered by date (FIFO)
        var buyRecords = (await _buyTable!
            .Where(r => r.PlayerUuid == playerUuid && r.ItemTag == itemTag)
            .ExecuteAsync())
            .Where(r => r.RemainingAmount > 0)
            .OrderBy(r => r.ClaimedAt)
            .ToList();

        if (buyRecords.Count == 0)
        {
            _logger.LogInformation("No buy records found for sell order: {Player} {Amount}x {Item}", 
                playerUuid, amount, itemTag);
            return null;
        }

        int remainingToMatch = amount;
        long totalBuyPrice = 0;
        var recordsToUpdate = new List<(BazaarBuyRecord Record, int UsedAmount)>();

        foreach (var record in buyRecords)
        {
            if (remainingToMatch <= 0)
                break;

            int useAmount = Math.Min(remainingToMatch, record.RemainingAmount);
            // Calculate proportional cost
            long proportionalCost = (long)((double)record.TotalPrice * useAmount / record.Amount);
            totalBuyPrice += proportionalCost;
            remainingToMatch -= useAmount;
            
            recordsToUpdate.Add((record, useAmount));
        }

        if (remainingToMatch > 0)
        {
            _logger.LogWarning("Could only match {Matched} of {Total} items for sell order: {Player} {Item}", 
                amount - remainingToMatch, amount, playerUuid, itemTag);
        }

        // Update buy records
        foreach (var (record, usedAmount) in recordsToUpdate)
        {
            record.RemainingAmount -= usedAmount;
            
            if (record.RemainingAmount <= 0)
            {
                // Delete the record
                await _buyTable!
                    .Where(r => r.PlayerUuid == playerUuid && r.ItemTag == itemTag && r.ClaimedAt == record.ClaimedAt)
                    .Delete()
                    .ExecuteAsync();
            }
            else
            {
                // Update with remaining TTL - calculate how much time has passed since creation
                var elapsed = DateTime.UtcNow - record.ClaimedAt;
                var remainingTtl = BuyOrderTtl - elapsed;
                if (remainingTtl <= TimeSpan.Zero)
                {
                    // Record should have expired, delete it
                    await _buyTable!
                        .Where(r => r.PlayerUuid == playerUuid && r.ItemTag == itemTag && r.ClaimedAt == record.ClaimedAt)
                        .Delete()
                        .ExecuteAsync();
                    continue;
                }

                // Delete and re-insert with updated amount and correct TTL
                await _buyTable!
                    .Where(r => r.PlayerUuid == playerUuid && r.ItemTag == itemTag && r.ClaimedAt == record.ClaimedAt)
                    .Delete()
                    .ExecuteAsync();
                
                var insert = _buyTable.Insert(record);
                insert.SetTTL((int)remainingTtl.TotalSeconds);
                await insert.ExecuteAsync();
            }
        }

        int matchedAmount = amount - remainingToMatch;
        if (matchedAmount == 0)
            return null;

        // Calculate profit
        long profit = totalPrice - totalBuyPrice;
        
        var flip = new BazaarFlip
        {
            PlayerUuid = playerUuid,
            Year = claimedAt.Year,
            ItemTag = itemTag,
            ItemName = itemName,
            Amount = matchedAmount,
            BuyPrice = totalBuyPrice,
            SellPrice = totalPrice,
            Profit = profit,
            SoldAt = claimedAt.ToUniversalTime()
        };

        await _flipTable!.Insert(flip).ExecuteAsync();

        _logger.LogInformation("Recorded flip for {Player}: {Amount}x {Item}, profit: {Profit} coins", 
            playerUuid, matchedAmount, itemTag, profit / 10.0);

        return flip;
    }

    public async Task<List<BazaarFlip>> GetFlips(Guid playerUuid, int limit = 100)
    {
        await EnsureTablesExist();
        
        // Query current year and previous year to ensure recent flips are included
        var currentYear = DateTime.UtcNow.Year;
        var results = new List<BazaarFlip>();
        
        // Query current year
        var currentYearFlips = await _flipTable!
            .Where(f => f.PlayerUuid == playerUuid && f.Year == currentYear)
            .Take(limit)
            .ExecuteAsync();
        results.AddRange(currentYearFlips);
        
        // If we need more, query previous year
        if (results.Count < limit)
        {
            var previousYearFlips = await _flipTable!
                .Where(f => f.PlayerUuid == playerUuid && f.Year == currentYear - 1)
                .Take(limit - results.Count)
                .ExecuteAsync();
            results.AddRange(previousYearFlips);
        }
        
        return results.OrderByDescending(f => f.SoldAt).Take(limit).ToList();
    }

    public async Task<List<BazaarBuyRecord>> GetOutstandingOrders(Guid playerUuid)
    {
        await EnsureTablesExist();
        
        // We need to query all items for this player, but Cassandra requires partition key
        // Since we partition by (PlayerUuid, ItemTag), we need to use ALLOW FILTERING or a different approach
        // For simplicity, we'll use a separate table or accept the limitation
        // Here we use a workaround by maintaining a secondary index or accepting that
        // the caller needs to know which items to query
        
        // Alternative: Use a statement with ALLOW FILTERING (not ideal for large datasets)
        var statement = new SimpleStatement(
            $"SELECT * FROM bazaar_buy_records WHERE playeruuid = ? ALLOW FILTERING", 
            playerUuid);
        
        var result = await _session.ExecuteAsync(statement);
        var records = new List<BazaarBuyRecord>();
        
        foreach (var row in result)
        {
            records.Add(new BazaarBuyRecord
            {
                PlayerUuid = row.GetValue<Guid>("playeruuid"),
                ItemTag = row.GetValue<string>("itemtag"),
                Amount = row.GetValue<int>("amount"),
                RemainingAmount = row.GetValue<int>("remaining_amount"),
                TotalPrice = row.GetValue<long>("total_price"),
                ClaimedAt = row.GetValue<DateTime>("claimedat")
            });
        }
        
        return records.Where(r => r.RemainingAmount > 0).ToList();
    }
}
