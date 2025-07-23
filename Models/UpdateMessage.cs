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
    [Key(8)]
    public string[]? Tab { get; set; }

    public enum UpdateKind
    {
        UNKOWN,
        CHAT,
        INVENTORY,
        API = 4,
        Setting = 8,
        Scoreboard = 16,
        Tab = 32,
    }
}

[MessagePackObject]
public class StateSettings
{
    [Key(0)]
    public bool DisableTradeTracking { get; set; }
    [Key(1)]
    public bool DisableBazaarTracking { get; set; }
    [Key(2)]
    public bool DisableKuudraTracking { get; set; }
    [Key(3)]
    public bool DebugEnabled { get; set; }
}

#nullable restore