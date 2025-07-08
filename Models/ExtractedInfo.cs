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
    public KatStatus? KatStatus { get; set;}
    [Key(3)]
    public List<ForgeItem>? ForgeItems = [];
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