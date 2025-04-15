using System;
using System.Collections.Generic;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

[MessagePackObject]
public class UpdateMessage
{
    [Key(0)]
    public UpdateKind Kind;

    [Key(1)]
    public DateTime ReceivedAt;
    [Key(2)]
    public ChestView? Chest;
    [Key(3)]
    public List<string>? ChatBatch;
    [Key(4)]
    public string PlayerId;
    [Key(5)]
    public string UserId { get; set; }
    [Key(6)]
    public StateSettings Settings { get; set; }
    [Key(7)]
    public string[]? Scoreboard { get; set; }

    public enum UpdateKind
    {
        UNKOWN,
        CHAT,
        INVENTORY,
        API = 4,
        Setting = 8,
        Scoreboard = 16
    }
}

[MessagePackObject]
public class StateSettings
{
    [Key(0)]
    public bool DisableTradeTracking { get; set; }
    [Key(1)]
    public bool DisableBazaarTracking { get; set; }
}

#nullable restore