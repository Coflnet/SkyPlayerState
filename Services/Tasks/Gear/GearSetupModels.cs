using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

// Response shapes of GET /Task/gear-setups, consumed by SkyApi through the generated PlayerState.Client.

/// <summary>The gear setups of one task with their estimated coins per hour.</summary>
public class TaskGearSetups
{
    public string TaskName { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    /// <summary>Setup id the community rate is assumed to be earned with.</summary>
    public string Anchor { get; set; } = "";
    /// <summary>ISO date the profile was researched, null if not stated.</summary>
    public string? ResearchedAt { get; set; }
    /// <summary>The task's community coins/hour (<see cref="TaskEstimator.GetCommunityEstimates"/>), 0 when unknown.</summary>
    public double CommunityCoinsPerHour { get; set; }
    /// <summary>Tier the community rate came from: stat_bucket, global or formula (none when there is no estimate).</summary>
    public string Source { get; set; } = "";
    public int Contributors { get; set; }
    /// <summary>Estimated coins/hour of the baseline setup, null when unknown.</summary>
    public double? BaselineCoinsPerHour { get; set; }
    /// <summary>The setup everything is compared against (DeltaCoinsPerHour 0). Its items are what a player is assumed to own.</summary>
    public GearSetupEstimate Baseline { get; set; } = new();
    /// <summary>budget, mid and endgame, in that order.</summary>
    public List<GearSetupEstimate> Setups { get; set; } = new();
}

/// <summary>One setup with its estimated rate and how far to trust it.</summary>
public class GearSetupEstimate
{
    /// <summary>Always "estimate": a modelled rate, see <see cref="Models.GearSetupBasis"/>; measured setups are clusters.</summary>
    public string Basis { get; set; } = Models.GearSetupBasis.Estimate;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>baseline, budget, mid or endgame.</summary>
    public string Tier { get; set; } = "";
    public List<GearSetupItem> Items { get; set; } = new();
    public Dictionary<string, double> Stats { get; set; } = new();
    /// <summary>baseline, upgrade, or (tierUnlock) reached: gear for this tier or a lower one, shown without numbers.</summary>
    public string Status { get; set; } = "";
    /// <summary>
    /// CoinsPerHour divided by the task's community rate. Scaled models clamp it to [0.2, 5]; for tierUnlock it is
    /// the ratio of two community rates and not clamped. Null when no rate can be given (and for reached setups).
    /// </summary>
    public double? Multiplier { get; set; }
    /// <summary>Estimated coins/hour with this setup, null when no community rate exists.</summary>
    public double? CoinsPerHour { get; set; }
    /// <summary>CoinsPerHour minus the baseline's; null when either is unknown and for reached setups.</summary>
    public double? DeltaCoinsPerHour { get; set; }
    /// <summary>high, medium or low.</summary>
    public string Confidence { get; set; } = "";
    public List<string> Assumptions { get; set; } = new();
    /// <summary>https links: the profile's sources plus this setup's own.</summary>
    public List<string> Sources { get; set; } = new();
    /// <summary>The tier task this setup is gear for (tierUnlock profiles), else null.</summary>
    public string? UnlocksTask { get; set; }
}

/// <summary>One item of a setup.</summary>
public class GearSetupItem
{
    public string Slot { get; set; } = "";
    public string ItemTag { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>Clean price of the tag (<see cref="TaskPriceService.GetPrices"/>), null when unknown or when PriceFilters apply.</summary>
    public double? EstimatedPrice { get; set; }
    /// <summary>Auction filters for variant priced items (pet level, rarity, ...), null for plain tags.</summary>
    public Dictionary<string, string>? PriceFilters { get; set; }
    /// <summary>False for items that cannot be bought (soulbound); they are listed but must not count as cost.</summary>
    public bool Tradeable { get; set; } = true;
}
