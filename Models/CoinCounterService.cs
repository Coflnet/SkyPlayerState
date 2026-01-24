using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Models;

public interface ICoinCounterService
{
    Task IncrementCounter(string userId, DateTime timestamp, CoinCounterType type, long amount);
    Task<CoinCounter> GetCounter(string userId, DateTime timestamp);
}

public class CoinCounterService : ICoinCounterService
{
    private readonly ICassandraService cassandraService;
    private readonly ILogger<CoinCounterService> logger;
    private Table<CassandraCoinCounterRow> table;

    public CoinCounterService(ICassandraService cassandraService, ILogger<CoinCounterService> logger)
    {
        this.cassandraService = cassandraService;
        this.logger = logger;
    }

    private async Task<Table<CassandraCoinCounterRow>> GetTable()
    {
        if (table != null)
            return table;

        var session = await cassandraService.GetSession();
        var mapping = new MappingConfiguration()
            .Define(new Map<CassandraCoinCounterRow>()
                .PartitionKey(t => t.User, t=>t.Year)
                .ClusteringKey(
                    new Tuple<string, SortOrder>("day_of_year", SortOrder.Descending),
                    new Tuple<string, SortOrder>("type", SortOrder.Ascending))
                .Column(t => t.User, c => c.WithName("user_id"))
                .Column(t => t.DayOfYear, c => c.WithName("day_of_year"))
                .Column(t => t.Type, c => c.WithName("type"))
                .Column(t => t.Amount, c => c.WithName("amount").AsCounter())
                .TableName("coin_counters"));

        table = new Table<CassandraCoinCounterRow>(session, mapping);
        await table.CreateIfNotExistsAsync();
        return table;
    }

    public async Task IncrementCounter(string userId, DateTime timestamp, CoinCounterType type, long amount)
    {
        try
        {
            var (year, dayOfYear) = Services.CoinCounterParser.GetDayKey(timestamp);
            var typeStr = type.ToString().ToLower();

            // Use Cassandra LINQ update pattern to increment the counter
            var table = await GetTable();

            // The LINQ provider will generate an UPDATE that uses the existing column value
            await table
                .Where(x => x.User == userId && x.Year == year && x.DayOfYear == dayOfYear && x.Type == typeStr)
                .Select(x => new CassandraCoinCounterRow { Amount = amount })
                .Update()
                .ExecuteAsync();
            
            logger.LogDebug($"Incremented {type} counter for {userId} by {amount} (day {dayOfYear})");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to increment counter for {userId}");
        }
    }

    public async Task<CoinCounter> GetCounter(string userId, DateTime timestamp)
    {
        try
        {
            var (year, dayOfYear) = Services.CoinCounterParser.GetDayKey(timestamp);

            var table = await GetTable();
            var rows = await table
                .Where(c => c.User == userId && c.Year == year && c.DayOfYear == dayOfYear)
                .ExecuteAsync();
            var counter = new CoinCounter
            {
                Date = timestamp,
                NpcSold = 0,
                BazaarOrderSize = 0,
                TradeSent = 0,
                AuctionBidded = 0
            };

            foreach (var row in rows)
            {
                switch (row.Type?.ToLower())
                {
                    case "npc":
                        counter.NpcSold = row.Amount;
                        break;
                    case "bazaar":
                        counter.BazaarOrderSize = row.Amount;
                        break;
                    case "trade":
                        counter.TradeSent = row.Amount;
                        break;
                    case "auctionhouse":
                        counter.AuctionBidded = row.Amount;
                        break;
                }
            }

            return counter;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to get counter for {userId}");
            return new CoinCounter { Date = timestamp };
        }
    }
}

/// <summary>
/// Cassandra row for coin counter table
/// </summary>
public class CassandraCoinCounterRow
{
    public string User { get; set; }
    public short Year { get; set; }
    public int DayOfYear { get; set; }
    public string Type { get; set; }
    public long Amount { get; set; }
}
