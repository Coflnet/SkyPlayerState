using System;
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
    public string CurrentServer { get; set; } = string.Empty;
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
    [Key(13)]
    public ActivePet? ActivePet { get; set; } = null;
    [Key(14)]
    public List<PetState>? Pets { get; set; } = null;
    [Key(15)]
    public Item? WeaponInHuntaxe { get; set; } = null;
    [Key(16)]
    public Item[]? HuntingToolkitItems { get; set; } = null;
    [Key(17)]
    public PlayerElectionVote? PlayerElectionVote { get; set; } = null;
    [Key(18)]
    public int MithrilPowder { get; set; }
    /// <summary>
    /// Task the classifier currently attributes the player's activity to, null when unknown.
    /// </summary>
    [Key(19)]
    public string? CurrentTask { get; set; }
    /// <summary>
    /// When <see cref="CurrentTask"/> last changed to its current value.
    /// </summary>
    [Key(20)]
    public DateTime CurrentTaskSince { get; set; }
    /// <summary>
    /// Task the player manually claimed, biases the classifier. Null when none.
    /// </summary>
    [Key(21)]
    public string? ClaimedTask { get; set; }
    /// <summary>
    /// When <see cref="ClaimedTask"/> was set, used for claim expiry.
    /// </summary>
    [Key(22)]
    public DateTime ClaimedAt { get; set; }
    /// <summary>
    /// The task session currently being accumulated across location changes.
    /// Location fragments are folded per session (not per area) so tasks that
    /// span multiple locations are not chopped below the classifier's minimum
    /// window. Null when no activity is being tracked.
    /// </summary>
    [Key(23)]
    public TaskSession? CurrentSession { get; set; }
    /// <summary>
    /// Coins currently in the players purse, parsed from the sidebar scoreboard.
    /// 0 when not yet known (e.g. never seen in skyblock). -1 while outside skyblock.
    /// </summary>
    [Key(24)]
    public long Purse { get; set; }
    /// <summary>
    /// Bits the player currently has available, parsed from the sidebar scoreboard.
    /// 0 when not yet known.
    /// </summary>
    [Key(25)]
    public long Bits { get; set; }
    /// <summary>
    /// The island the player is currently on, parsed from the tab list's "Area: &lt;Island&gt;"
    /// line (see TabListUpdate). Unlike <see cref="CurrentLocation"/> (the scoreboard sub-zone,
    /// e.g. "Jungle"), this is the island itself (e.g. "Crystal Hollows"), and is only set while
    /// the tab list carries that line. Takes priority over the static zone-to-island map (see
    /// Tasks.SkyblockZones) for live classification since it can resolve zones the map leaves
    /// ambiguous (e.g. "Dragon's Lair").
    /// </summary>
    [Key(26)]
    public string? CurrentIsland { get; set; }
    /// <summary>
    /// When <see cref="CurrentIsland"/> was last set from the tab list (see TabListUpdate). The tab
    /// list is sent far less often than the scoreboard, so this can predate the player's current
    /// zone/location; classification must only trust <see cref="CurrentIsland"/> for a zone when
    /// this is at or after <see cref="LastLocationChange"/> - see TaskClassifier.Classify.
    /// </summary>
    [Key(27)]
    public DateTime CurrentIslandAt { get; set; }
    /// <summary>
    /// When <see cref="CurrentLocation"/> last actually CHANGED to a different zone (or was first
    /// seen) - unlike <see cref="LastLocationChange"/> (really "last profit flush", also bumped by
    /// the 5-minute same-zone flush - see CollectionListener.StoreLocationProfit), this only moves
    /// on a genuine zone change. Together with <see cref="CurrentLocationSeenAt"/> this bounds the
    /// window in which a tab-reported <see cref="CurrentIsland"/> reading can be trusted for the
    /// current zone - see TaskClassifier.Classify.
    /// </summary>
    [Key(28)]
    public DateTime CurrentLocationSince { get; set; }
    /// <summary>
    /// Time of the last scoreboard update that confirmed the player is still in
    /// <see cref="CurrentLocation"/> (bumped on every scoreboard tick while the zone is unchanged,
    /// not just on a flush). A tab reading is only trusted for the current zone's fragment when it
    /// arrived at/after <see cref="CurrentLocationSince"/> and at/before this - i.e. while the
    /// player was actually confirmed to be in that zone - see TaskClassifier.Classify.
    /// </summary>
    [Key(29)]
    public DateTime CurrentLocationSeenAt { get; set; }
    /// <summary>
    /// The Catacombs floor zone string (e.g. "The Catacombs (F7)") the player was last confirmed in,
    /// or null when they have not been in a dungeon floor recently. Set from every scoreboard tick
    /// while <see cref="CurrentLocation"/> is a Catacombs floor zone (Entrance excluded - it has no
    /// reward chests), and overridden from a Croesus reward-chest GUI title (which names the floor
    /// being claimed and can be for an older run) - see
    /// <see cref="Tasks.DungeonRewardAttribution"/>/<see cref="Services.CollectionListener"/>. Used
    /// to attribute a "Dungeon Hub" reward-claim period back to the run's own floor.
    /// </summary>
    [Key(30)]
    public string? LastDungeonFloor { get; set; }
    /// <summary>
    /// When <see cref="LastDungeonFloor"/> was last set - see <see cref="Tasks.DungeonRewardAttribution.ResolveLocation"/>,
    /// which only attributes a Dungeon Hub claim period to the floor within a short window of this.
    /// </summary>
    [Key(31)]
    public DateTime LastDungeonFloorAt { get; set; }
    /// <summary>
    /// The last Kuudra tier zone ("Kuudra's Hollow (T5)") the player was seen in, or null. Used by
    /// <see cref="Tasks.KuudraRewardAttribution"/> to attribute chest claims made at the Dungeon Hub /
    /// Forgotten Skull back to the tier. Keys 41/42: 40 is <see cref="LastTransferViewAt"/>.
    /// </summary>
    [Key(41)]
    public string? LastKuudraTier { get; set; }
    /// <summary>When <see cref="LastKuudraTier"/> was last seen.</summary>
    [Key(42)]
    public DateTime LastKuudraTierAt { get; set; }
    /// <summary>
    /// Seconds spent in Kuudra tier zones without anything collected (so no stored period) since the
    /// last claim - added to the claim period's start by <see cref="Tasks.KuudraRewardAttribution.UpdatePendingRun"/>.
    /// </summary>
    [Key(43)]
    public double KuudraPendingRunSeconds { get; set; }
    /// <summary>
    /// A dungeon reward chest GUI with a non-zero coin cost seen but not yet confirmed as bought (the
    /// click itself is invisible - only a purse drop on a later update confirms it) - see
    /// <see cref="Services.DungeonRewardListener"/>.
    /// </summary>
    [Key(32)]
    public PendingDungeonChestCharge? PendingDungeonChestCharge { get; set; }
    [MessagePackObject]
    public class PetState
    {
        [Key(0)] public string? Name { get; set; }
        [Key(1)] public string? Type { get; set; }
        [Key(2)] public string? Tier { get; set; }
        [Key(3)] public int Level { get; set; }
        [Key(4)] public double Exp { get; set; }
        [Key(5)] public bool IsActive { get; set; }
        [Key(6)] public string? HeldItem { get; set; }
        [Key(7)] public int CandyUsed { get; set; }
        [Key(8)] public string? ColorCode { get; set; }
        [Key(9)] public string? Tag { get; set; }
        [Key(10)] public string? Uuid { get; set; }
        [Key(11)] public double ProgressPercent { get; set; }
        [Key(12)] public int TargetLevel { get; set; }
        [Key(13)] public double CurrentExp { get; set; }
        [Key(14)] public double ExpForLevel { get; set; }
        [Key(15)] public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public PetState() { }
        public PetState(PetState other)
        {
            Name = other.Name;
            Type = other.Type;
            Tier = other.Tier;
            Level = other.Level;
            Exp = other.Exp;
            IsActive = other.IsActive;
            HeldItem = other.HeldItem;
            CandyUsed = other.CandyUsed;
            ColorCode = other.ColorCode;
            Tag = other.Tag;
            Uuid = other.Uuid;
            ProgressPercent = other.ProgressPercent;
            TargetLevel = other.TargetLevel;
            CurrentExp = other.CurrentExp;
            ExpForLevel = other.ExpForLevel;
            LastUpdated = other.LastUpdated;
        }
    }
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
        ActivePet = extractedInfo.ActivePet == null ? null : new ActivePet(extractedInfo.ActivePet);
        if (extractedInfo.Pets != null)
        {
            Pets = new List<PetState>();
            foreach (var pet in extractedInfo.Pets)
            {
                Pets.Add(new PetState(pet));
            }
        }
        else
        {
            Pets = null;
        }
        WeaponInHuntaxe = extractedInfo.WeaponInHuntaxe == null ? null : new Item(extractedInfo.WeaponInHuntaxe);
        HuntingToolkitItems = extractedInfo.HuntingToolkitItems == null ? null : [.. extractedInfo.HuntingToolkitItems];
        PlayerElectionVote = extractedInfo.PlayerElectionVote == null ? null : new PlayerElectionVote
        {
            VotedFor = extractedInfo.PlayerElectionVote.VotedFor,
            VoteCount = extractedInfo.PlayerElectionVote.VoteCount
        };
        MithrilPowder = extractedInfo.MithrilPowder;
        CurrentTask = extractedInfo.CurrentTask;
        CurrentTaskSince = extractedInfo.CurrentTaskSince;
        ClaimedTask = extractedInfo.ClaimedTask;
        ClaimedAt = extractedInfo.ClaimedAt;
        CurrentSession = extractedInfo.CurrentSession == null ? null : new TaskSession(extractedInfo.CurrentSession);
        Purse = extractedInfo.Purse;
        Bits = extractedInfo.Bits;
        CurrentIsland = extractedInfo.CurrentIsland;
        CurrentIslandAt = extractedInfo.CurrentIslandAt;
        CurrentLocationSince = extractedInfo.CurrentLocationSince;
        CurrentLocationSeenAt = extractedInfo.CurrentLocationSeenAt;
        LastDungeonFloor = extractedInfo.LastDungeonFloor;
        LastDungeonFloorAt = extractedInfo.LastDungeonFloorAt;
        LastKuudraTier = extractedInfo.LastKuudraTier;
        LastKuudraTierAt = extractedInfo.LastKuudraTierAt;
        KuudraPendingRunSeconds = extractedInfo.KuudraPendingRunSeconds;
        PendingDungeonChestCharge = extractedInfo.PendingDungeonChestCharge == null ? null : new PendingDungeonChestCharge
        {
            ChestType = extractedInfo.PendingDungeonChestCharge.ChestType,
            CostCoins = extractedInfo.PendingDungeonChestCharge.CostCoins,
            PurseAtOpen = extractedInfo.PendingDungeonChestCharge.PurseAtOpen,
            SeenAt = extractedInfo.PendingDungeonChestCharge.SeenAt,
            Floor = extractedInfo.PendingDungeonChestCharge.Floor,
            Essences = extractedInfo.PendingDungeonChestCharge.Essences == null ? null : new Dictionary<string, int>(extractedInfo.PendingDungeonChestCharge.Essences)
        };
        LastTransferViewAt = extractedInfo.LastTransferViewAt;
        LastCraftViewAt = extractedInfo.LastCraftViewAt;
        LastCraftIngredients = extractedInfo.LastCraftIngredients == null ? new() : new List<string>(extractedInfo.LastCraftIngredients);
        PeriodSplitPending = extractedInfo.PeriodSplitPending;
    }

    /// <summary>
    /// ReceivedAt of the last inventory update where the current or previous view was a transfer view
    /// (trade, sack, NPC shop, bazaar) - sack chat notifications shortly after it report a transfer or
    /// sale, not loot. See <see cref="Services.CollectionListener.IsTransferView"/>. Key 40 leaves
    /// room for other properties added after <see cref="PendingDungeonChestCharge"/>.
    /// </summary>
    [Key(40)]
    public DateTime LastTransferViewAt { get; set; }
    /// <summary>
    /// ReceivedAt of the last inventory update where the current or previous view was a recipe view
    /// (see <see cref="Services.CollectionListener.TryGetRecipe"/>) - sack chat notifications shortly
    /// after it that remove one of <see cref="LastCraftIngredients"/> are the craft's input, not a loss.
    /// </summary>
    [Key(44)]
    public DateTime LastCraftViewAt { get; set; }
    /// <summary>Ingredient tags of the recipe views behind <see cref="LastCraftViewAt"/> (union of current and previous view).</summary>
    [Key(45)]
    public List<string> LastCraftIngredients { get; set; } = new();
    /// <summary>
    /// An inventory change worth enough to end the period happened while a crafting view was open; the
    /// period ends with the first view after it instead, so the craft's input and output stay together.
    /// See <see cref="Services.CollectionListener.StartNewPeriodAfterLargeChange"/>.
    /// </summary>
    [Key(46)]
    public bool PeriodSplitPending { get; set; }
}

/// <summary>
/// A dungeon reward chest GUI with a non-zero coin cost, seen but not yet confirmed bought - see
/// <see cref="Services.DungeonRewardListener"/>.
/// </summary>
[MessagePackObject]
public class PendingDungeonChestCharge
{
    [Key(0)] public string? ChestType { get; set; }
    [Key(1)] public long CostCoins { get; set; }
    [Key(2)] public long PurseAtOpen { get; set; }
    [Key(3)] public DateTime SeenAt { get; set; }
    /// <summary>The floor (e.g. "F7") the chest belongs to, for the charge log line.</summary>
    [Key(4)] public string? Floor { get; set; }
    /// <summary>
    /// Essence in the chest's Contents (ESSENCE_WITHER=54, ...), credited when the charge is confirmed.
    /// Nullable/optional so states persisted before this field existed still deserialize.
    /// </summary>
    [Key(5)] public Dictionary<string, int>? Essences { get; set; }
}

/// <summary>
/// A contiguous run of activity attributed to a single task, accumulated across
/// location changes. Lives on <see cref="ExtractedInfo"/> so it survives across
/// state updates (single writer per player via Kafka partitioning).
/// </summary>
[MessagePackObject]
public class TaskSession
{
    /// <summary>Task the accumulated window currently classifies to, null while still ambiguous.</summary>
    [Key(0)] public string? DetectedTask { get; set; }
    /// <summary>Start of the session (start of its first fragment).</summary>
    [Key(1)] public DateTime StartTime { get; set; }
    /// <summary>Last time items were actually collected, used for idle/AFK finalization.</summary>
    [Key(2)] public DateTime LastItemTime { get; set; }
    [Key(3)] public string? Server { get; set; }
    /// <summary>Most recent location the session collected in.</summary>
    [Key(4)] public string? Location { get; set; }
    /// <summary>Items accumulated across every location the session spanned.</summary>
    [Key(5)] public Dictionary<string, int> Items { get; set; } = new();

    public TaskSession() { }
    public TaskSession(TaskSession other)
    {
        DetectedTask = other.DetectedTask;
        StartTime = other.StartTime;
        LastItemTime = other.LastItemTime;
        Server = other.Server;
        Location = other.Location;
        Items = other.Items == null ? new() : new(other.Items);
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
public class ActivePet
{
    [Key(0)]
    public string? Name { get; set; }
    [Key(1)]
    public string? ColorCode { get; set; }
    [Key(2)]
    public double? ProgressPercent { get; set; }
    [Key(3)]
    public int? TargetLevel { get; set; }
    [Key(4)]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public ActivePet()
    {
    }

    public ActivePet(ActivePet other)
    {
        Name = other.Name;
        ColorCode = other.ColorCode;
        ProgressPercent = other.ProgressPercent;
        TargetLevel = other.TargetLevel;
        LastUpdated = other.LastUpdated;
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
    public string ItemName = string.Empty;
}

[MessagePackObject]
public class ForgeItem
{
    [Key(0)]
    public string ItemName = string.Empty;
    [Key(1)]
    public DateTime ForgeEnd;
    [Key(2)]
    public string Tag = string.Empty;
}

[MessagePackObject]
public class PlayerElectionVote
{
    [Key(0)]
    public string VotedFor { get; set; } = string.Empty;
    [Key(1)]
    public int VoteCount { get; set; }
}
#nullable restore