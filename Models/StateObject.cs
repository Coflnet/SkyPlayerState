using System;
using System.Collections.Generic;
using System.Threading;
using Coflnet.Sky.PlayerState.Bazaar;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// Container for the player state
/// </summary>
[MessagePackObject]
public class StateObject
{
    /// <summary>
    /// Items in the inventory
    /// </summary>
    [Key(0)]
    public List<Item> Inventory = new List<Item>();
    /// <summary>
    /// Items in some kind of storage (enderchest, backpack etc)
    /// </summary>
    [Key(1)]
    public List<List<Item>> Storage = new List<List<Item>>();
    /// <summary>
    /// The last few views uploaded (for context)
    /// </summary>
    [Key(2)]
    public Queue<ChestView> RecentViews = new();
    /// <summary>
    /// last chat contents
    /// </summary>
    [Key(3)]
    public Queue<ChatMessage> ChatHistory = new();
    /// <summary>
    /// Purse changes
    /// </summary>
    [Key(4)]
    public Queue<PurseUpdate> PurseHistory = new();
    [Key(5)]
    public McInfo McInfo = new();
    [Key(6)]
    public string PlayerId;
    /// <summary>
    /// List of profiles the one at index 0 is used as active profile
    /// </summary>
    [Key(7)]
    public List<Profile> Profiles;
    [Key(8)]
    public List<Offer> BazaarOffers = new();
    [Key(9)]
    public ExtractedInfo ExtractedInfo = new();
    [Key(10)]
    public StateSettings Settings = new();
    [Key(11)]
    public LimitsSummary Limits = new();
    [IgnoreMember]
    public SemaphoreSlim Lock = new SemaphoreSlim(1);
    [IgnoreMember]
    public DateTime LastAccess { get; internal set; }

    public StateObject()
    {
    }
    // deep copy constructor with null checks
    public StateObject(StateObject other)
    {
        if (other.Inventory != null)
            Inventory = new List<Item>(other.Inventory);
        if (other.Storage != null)
            Storage = new List<List<Item>>(other.Storage);
        if (other.RecentViews != null)
            RecentViews = new Queue<ChestView>(other.RecentViews);
        if (other.ChatHistory != null)
        {
            var intermediate = new ChatMessage[other.ChatHistory.Count];
            // prevent concurrent modification
            other.ChatHistory.CopyTo(intermediate, 0);
            ChatHistory = new Queue<ChatMessage>(intermediate);
        }
        if (other.PurseHistory != null)
            PurseHistory = new Queue<PurseUpdate>(other.PurseHistory);
        if (other.McInfo != null)
            McInfo = other.McInfo;
        if (other.PlayerId != null)
            PlayerId = other.PlayerId;
        if (other.Profiles != null)
            Profiles = new List<Profile>(other.Profiles);
        if (other.BazaarOffers != null)
            BazaarOffers = new List<Offer>(other.BazaarOffers);
        if(other.ExtractedInfo != null)
            ExtractedInfo = new ExtractedInfo(other.ExtractedInfo);
        Settings = other.Settings;
    }
}

[MessagePackObject]
public class LimitsSummary
{
    [Key(0)]
    public Queue<Limit> Bazaar { get; set; } = new();
    [Key(1)]
    public Queue<Limit> AuctionHouse { get; set; } = new();
    [Key(2)]
    public Queue<Limit> Trade { get; set; } = new();

    [MessagePackObject]
    public class Limit
    {
        [Key(0)]
        public long Amount { get; set; }
        [Key(1)]
        public DateTime Time { get; set; }
        [Key(2)]
        public string Message { get; set; }
    }
}

[MessagePackObject]
public class McInfo
{
    [Key(0)]
    public Guid Uuid;
    [Key(1)]
    public string Name;
}
[MessagePackObject]
public class Profile
{
    [Key(0)]
    public Guid Uuid;
    [Key(1)]
    public string Name;
}
[MessagePackObject]
public class PurseUpdate
{
    [Key(0)]
    public double Amount;
    [Key(1)]
    public DateTime Time;
}
#nullable restore