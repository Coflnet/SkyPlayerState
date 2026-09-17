using System.Collections.Generic;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Bazaar;

[MessagePackObject]
public class Offer
{
    [Key(0)]
    public bool IsSell { get; set; }
    [Key(1)]
    public string ItemTag { get; set; } = null!;
    [Key(2)]
    public string ItemName { get; set; } = null!;
    [Key(3)]
    public long Amount { get; set; }
    [Key(4)]
    public double PricePerUnit { get; set; }
    [Key(5)]
    public DateTime Created { get; set; }
    [Key(7)]
    public long FilledAmount { get; set; }
    [Key(8)]
    public bool IsExpired { get; set; }
    [Key(9)]
    public long? ClaimedAmount { get; set; }
    // Withdrawals observed in lore that may still arrive in a later chat upload.
    [Key(10)]
    public long PendingObservedClaims { get; set; }
    [Key(6)]
    public List<Fill> Customers { get; set; } = new();
}

[MessagePackObject]
public class Fill
{
    [Key(0)]
    public string PlayerName { get; set; } = null!;
    [Key(1)]
    public long Amount { get; set; }
    [Key(2)]
    public DateTime TimeStamp { get; set; }
}