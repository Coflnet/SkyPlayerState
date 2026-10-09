using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Classifies how a task is performed
/// </summary>
public enum TaskType
{
    /// <summary>Active tasks require continuous player attention (grinding mobs, mining, fishing)</summary>
    Active,
    /// <summary>Passive tasks only require setup then waiting (forging, kat, composter, traps)</summary>
    Passive,
    /// <summary>Limited tasks can only be done once per day or every few hours</summary>
    Limited
}

public class TaskResult
{
    public int ProfitPerHour { get; set; }
    public string Message { get; set; } = "No detailed instructions available.";
    public string Details { get; set; }
    public string OnClick { get; set; }
    public string PrimaryAction { get; set; }
    /// <summary>
    /// Indicates if the task is mostly passive, meaning it can be done in parallel to others (requiring mostly waiting)
    /// </summary>
    public bool MostlyPassive { get; set; }
    /// <summary>
    /// Classification: Active (requires grinding), Passive (setup + wait), Limited (daily/cooldown)
    /// </summary>
    public TaskType Type { get; set; } = TaskType.Active;
    public string Name { get; set; }
    /// <summary>
    /// When this result was calculated (for freshness checks by API consumers)
    /// </summary>
    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Detailed breakdown for API consumers (website, external services)
    /// </summary>
    public MethodBreakdown Breakdown { get; set; }
    /// <summary>
    /// Whether this task is currently accessible (false = time-locked, mayor-locked, or already completed today)
    /// </summary>
    public bool IsAccessible { get; set; } = true;
    /// <summary>
    /// Human-readable reason why the task is not accessible (null if accessible)
    /// </summary>
    public string InaccessibleReason { get; set; }
    /// <summary>
    /// For limited tasks: when this task can next be done (null if always available)
    /// </summary>
    public DateTime? NextAvailableAt { get; set; }
}

/// <summary>
/// Full breakdown of a money-making method for API consumers
/// </summary>
public class MethodBreakdown
{
    /// <summary>
    /// Detailed explanation of what to do (step-by-step)
    /// </summary>
    public string HowTo { get; set; }
    /// <summary>
    /// Item tags required to get started (can look up price to estimate startup cost)
    /// </summary>
    public List<RequiredItem> RequiredItems { get; set; } = [];
    /// <summary>
    /// Expected item drops with rates and value contribution
    /// </summary>
    public List<DropInfo> Drops { get; set; } = [];
    /// <summary>
    /// Items consumed by the method (e.g. gemstones fed into a Forge recipe). ContributionPerHour
    /// is negative (already subtracted from ProfitPerHour) so consumers can render it directly.
    /// Empty for methods with no ingredient cost.
    /// </summary>
    public List<DropInfo> Costs { get; set; } = [];
    /// <summary>
    /// Estimated actions per hour (kills, catches, mines, etc.)
    /// </summary>
    public double ActionsPerHour { get; set; }
    /// <summary>
    /// Name of the action unit (kills, catches, runs, mines)
    /// </summary>
    public string ActionUnit { get; set; } = "actions";
    /// <summary>
    /// Effects that can increase drop rates or speed
    /// </summary>
    public List<DropEffect> Effects { get; set; } = [];
    /// <summary>
    /// Whether the profit estimate comes from player data or formula
    /// </summary>
    public string Source { get; set; }
    /// <summary>
    /// Hours of player data used for calculation (0 if formula-based)
    /// </summary>
    public double TrackedHours { get; set; }
    /// <summary>
    /// Category for grouping (Fishing, Mining, Slayer, Dungeon, Farming, etc.)
    /// </summary>
    public string Category { get; set; }
    /// <summary>
    /// Bonus multiplier when cooperating with other players (1.0 = no bonus)
    /// </summary>
    public double CoopBonus { get; set; } = 1.0;
    /// <summary>
    /// Part of the task's coins/hour that comes from late rewards (the drops with Kind "late_reward"), already
    /// included in the total. Null when the task has no late reward.
    /// </summary>
    public double? LateRewardPerHour { get; set; }
    /// <summary>Display label of the late reward ("Contest reward", "Boss drop"), null when the task has none.</summary>
    public string LateRewardLabel { get; set; }
    /// <summary>
    /// Task type classification (Active, Passive, Limited)
    /// </summary>
    public TaskType Type { get; set; } = TaskType.Active;
    /// <summary>
    /// Wiki page explaining the method or its main item (e.g. Sludge Mining -&gt;
    /// https://hypixelskyblock.minecraft.wiki/w/Sludge_Juice). Null when no page has been
    /// curated/verified for this specific method yet - see MethodTask.WikiUrl.
    /// </summary>
    public string WikiUrl { get; set; }
    /// <summary>The exact zone to stand in to do this method (defaults to the first Locations entry).</summary>
    public string Where { get; set; }
    /// <summary>The SkyBlock island <see cref="Where"/> belongs to (see SkyblockZones.IslandOf), or null if unknown.</summary>
    public string Island { get; set; }
    /// <summary>Wiki page for <see cref="Island"/> (has the island's map), or null if unknown.</summary>
    public string WhereWikiUrl { get; set; }
    /// <summary>
    /// Warp command to get close: the task's own WarpCommand if it declares one, else the
    /// <see cref="Island"/>'s warp (see SkyblockZones.IslandInfo), else null.
    /// </summary>
    public string Warp { get; set; }
    /// <summary>
    /// Ordered, followable steps a total beginner can click through - see MethodTask.Steps.
    /// </summary>
    public List<TaskStep> Steps { get; set; } = [];
    /// <summary>
    /// Total <see cref="RequiredItem.EstimatedPrice"/> of the required items the player is known to be missing
    /// (<see cref="RequiredItem.Owned"/> == false), i.e. what it costs to get the gear for this method.
    /// 0 when all required items are owned. Null when it cannot be determined: no player state, no
    /// required items, or no item is known missing while some are undecidable (<c>Owned == null</c>).
    /// When some items are missing and others undecidable it is the sum of the known missing ones.
    /// </summary>
    public long? MissingItemsCost { get; set; }
    /// <summary>
    /// True when there are required items and all are known owned, false when at least one is known
    /// missing, null when unknown (no player state, undecidable items) or the method has no required items.
    /// </summary>
    public bool? GearOwned { get; set; }
    /// <summary>
    /// The task's requirements checked against the player's profile (see <see cref="TaskRequirement"/>), in the
    /// order they should be reached. Null when the task declares none, and also null on results produced
    /// without the requirement check.
    /// </summary>
    public List<TaskRequirement> Requirements { get; set; }
    /// <summary>
    /// Tri-state over the hard requirements only: true when all are met, false when at least one is known unmet
    /// (the task is then inaccessible), null when unknown or the task has none. Lets a client tell
    /// "locked by progression" from "missing gear" (<see cref="GearOwned"/>).
    /// </summary>
    public bool? RequirementsMet { get; set; }
}

/// <summary>
/// One step in a followable how-to-earn-coins guide. Rendered as a numbered list; clickable where
/// OnClick is set.
/// </summary>
public class TaskStep
{
    public int Number { get; set; }
    public string Text { get; set; }
    /// <summary>
    /// Same click semantics as <see cref="TaskResult.OnClick"/>: a string starting with "http" opens
    /// a URL in the browser, "suggest:" suggests a chat command, anything else runs as a command.
    /// Null means this step has no action (just read it).
    /// </summary>
    public string OnClick { get; set; }
}

public class RequiredItem
{
    public string ItemTag { get; set; }
    public string Name { get; set; }
    /// <summary>
    /// Why this item is needed (e.g. "Main weapon", "Armor set", "Tool")
    /// </summary>
    public string Reason { get; set; }
    /// <summary>
    /// Estimated price at time of calculation (0 if unknown)
    /// </summary>
    public long EstimatedPrice { get; set; }
    /// <summary>
    /// Whether the player's state shows this item (or an equivalent/better one, see GearOwnership).
    /// True = has it, false = state loaded and it was not found, null = unknown (no player state, or the
    /// entry cannot be decided from state, e.g. worn armor, consumables, example weapons).
    /// </summary>
    public bool? Owned { get; set; }
    /// <summary>
    /// Optional requirement class when the entry is an example of a category rather than one specific
    /// item (e.g. "HUNT_WEAPON": any weapon on the hunting weapon ladder satisfies it). Null for plain items.
    /// </summary>
    public string Category { get; set; }
}

public class DropInfo
{
    public string ItemTag { get; set; }
    public string Name { get; set; }
    public double RatePerHour { get; set; }
    public double PriceEach { get; set; }
    public double ContributionPerHour { get; set; }
    /// <summary>
    /// Null for an ordinary drop of the activity itself. "late_reward" for an item that arrives after the
    /// work that earned it (Galatea contest coupons, a slayer boss drop - see MethodTask.LateReward), so a
    /// client can show it as its own part of the task's total.
    /// </summary>
    public string Kind { get; set; }
    /// <summary>Short display label of <see cref="Kind"/> (e.g. "Contest reward", "Boss drop"); null when <see cref="Kind"/> is null.</summary>
    public string Label { get; set; }
}

public class DropEffect
{
    /// <summary>
    /// Name of the effect (e.g. "Luck VII", "Mining Speed boost", "Pet ability")
    /// </summary>
    public string Name { get; set; }
    /// <summary>
    /// Description of how it affects drops/speed
    /// </summary>
    public string Description { get; set; }
    /// <summary>
    /// Estimated multiplier on profit (1.0 = no change, 1.2 = 20% increase)
    /// </summary>
    public double EstimatedMultiplier { get; set; } = 1.0;
}
