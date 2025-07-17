using System.Collections.Generic;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// Player specific variables extracted from chat/chests
/// </summary>
[MessagePackObject]
public class ExtractedInfo
{
    [Key(0)]
    public DateTime BoosterCookieExpires;
    [Key(1)]
    public DateTime KuudraStart;
    [Key(2)]
    public KatStatus? KatStatus;
    [Key(3)]
    public List<ForgeItem?>? ForgeItems = [];
    [Key(4)]
    public string CurrentServer { get; set; }
    [Key(5)]
    public string CurrentLocation { get; set; } = "Unknown";
    [Key(6)]
    public DateTime LastLocationChange { get; set; } = DateTime.UtcNow;
    [Key(7)]
    public HeartOfThe? HeartOfTheMountain { get; set; } = null;
    [Key(8)]
    public HeartOfThe? HeartOfTheForest { get; set; } = null;
    [Key(9)]
    public int AgathaLevel { get; set; }
    [Key(10)]
    public Dictionary<string, int>? ShardCounts { get; set; }
    [Key(11)]
    public Dictionary<string, int>? AttributeLevel { get; set; }
    public ExtractedInfo()
    {
    }
    public ExtractedInfo(ExtractedInfo extractedInfo)
    {
        BoosterCookieExpires = extractedInfo.BoosterCookieExpires;
        KuudraStart = extractedInfo.KuudraStart;
        KatStatus = extractedInfo.KatStatus == null ? null : new KatStatus
        {
            IsKatActive = extractedInfo.KatStatus.IsKatActive,
            KatEnd = extractedInfo.KatStatus.KatEnd,
            ItemName = extractedInfo.KatStatus.ItemName
        };
        ForgeItems = extractedInfo.ForgeItems == null ? null : [.. extractedInfo.ForgeItems];
        CurrentServer = extractedInfo.CurrentServer;
        CurrentLocation = extractedInfo.CurrentLocation;
        LastLocationChange = extractedInfo.LastLocationChange;
        HeartOfTheMountain = extractedInfo.HeartOfTheMountain;
        HeartOfTheForest = extractedInfo.HeartOfTheForest;
        AgathaLevel = extractedInfo.AgathaLevel;
        ShardCounts = extractedInfo.ShardCounts == null ? null : new(extractedInfo.ShardCounts);
        AttributeLevel = extractedInfo.AttributeLevel == null ? null : new(extractedInfo.AttributeLevel);
    }
}

[MessagePackObject]
public class KatStatus
{
    [Key(0)]
    public bool IsKatActive;
    [Key(1)]
    public DateTime KatEnd;
    [Key(2)]
    public string ItemName;
}

[MessagePackObject]
public class ForgeItem
{
    [Key(0)]
    public string ItemName;
    [Key(1)]
    public DateTime ForgeEnd;
    [Key(2)]
    public string Tag;
}
#nullable restore