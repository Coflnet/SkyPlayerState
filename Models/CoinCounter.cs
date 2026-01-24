using System;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// Represents a daily coin counter for different transaction types
/// </summary>
[MessagePackObject]
public class CoinCounter
{
    [Key(0)]
    public long NpcSold { get; set; }
    [Key(1)]
    public long BazaarOrderSize { get; set; }
    [Key(2)]
    public long TradeSent { get; set; }
    [Key(3)]
    public long AuctionBidded { get; set; }
    [Key(4)]
    public DateTime Date { get; set; }
}

/// <summary>
/// Cassandra model for coin counter storage
/// </summary>
public class CassandraCoinCounter
{
    /// <summary>
    /// Partition key: userId + year
    /// </summary>
    public string UserYear { get; set; }
    
    /// <summary>
    /// Clustering key: day of year (descending)
    /// </summary>
    public int DayOfYear { get; set; }
    
    /// <summary>
    /// Clustering key: counter type (npc, bazaar, trade, ah)
    /// </summary>
    public string Type { get; set; }
    
    /// <summary>
    /// The counter value
    /// </summary>
    public long Amount { get; set; }
}

public enum CoinCounterType
{
    Npc,
    Bazaar,
    Trade,
    AuctionHouse
}
