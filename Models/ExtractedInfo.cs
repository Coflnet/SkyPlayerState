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
    [Key(12)]
    public Composter? Composter { get; set; } = null;
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
        Composter = extractedInfo.Composter == null ? null : new Composter
        {
            NextCompostAt = extractedInfo.Composter.NextCompostAt,
            FuelStored = extractedInfo.Composter.FuelStored,
            MatterStored = extractedInfo.Composter.MatterStored,
            LastCompostToClaim = extractedInfo.Composter.LastCompostToClaim,
            SpeedPercentIncrease = extractedInfo.Composter.SpeedPercentIncrease,
            MultiDropChance = extractedInfo.Composter.MultiDropChance,
            FuelCap = extractedInfo.Composter.FuelCap,
            MatterCap = extractedInfo.Composter.MatterCap,
            CostReductionPercent = extractedInfo.Composter.CostReductionPercent
        };
    }
}

[MessagePackObject]
public class Composter
{
    [Key(0)]
    public DateTime NextCompostAt;
    [Key(1)]
    public int FuelStored;
    [Key(2)]
    public int MatterStored;
    [Key(3)]
    public int LastCompostToClaim;
    /// <summary>
    /// From Composter Upgrades
    /// </summary>
    [Key(4)]
    public int SpeedPercentIncrease;
    [Key(5)]
    public int MultiDropChance;
    [Key(6)]
    public int FuelCap;
    [Key(7)]
    public int MatterCap;
    [Key(8)]
    public int CostReductionPercent;
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