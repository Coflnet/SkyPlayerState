using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Tasks.Gear;

namespace Coflnet.Sky.PlayerState.Models;

// Response shapes of GET /Task/gear-clusters/{taskName}, consumed by SkyApi through the generated PlayerState.Client.

/// <summary>Values of <see cref="GearCluster.Basis"/> and <see cref="GearSetupEstimate.Basis"/>.</summary>
public static class GearSetupBasis
{
    /// <summary>Derived from task session observations of at least two distinct players.</summary>
    public const string Measured = "measured";
    /// <summary>A curated setup with a modelled estimate: shown only where nothing is measured, labelled "estimate".</summary>
    public const string Estimate = "estimate";
}

/// <summary>
/// Measured gear setups of one task (see <see cref="Services.Gear.GearClusterService"/>) plus the curated setups with
/// their modelled estimates as the fallback. Never contains player keys, fingerprints or single observations.
/// </summary>
public class TaskGearClusters
{
    public string TaskName { get; set; } = "";
    /// <summary>Gear category of the task (the bound profile id, "other" for unbound tasks).</summary>
    public string Category { get; set; } = "";
    /// <summary>When the clusters were derived (hourly); null before the first derivation after a restart.</summary>
    public DateTime? ComputedAt { get; set; }
    /// <summary>How many weeks of observations the clusters are derived from.</summary>
    public int WindowWeeks { get; set; }
    /// <summary>Distinct players a cluster or modifier effect needs to be published.</summary>
    public int MinPlayers { get; set; }
    /// <summary>Play time a cluster needs, summed over its players.</summary>
    public double MinHours { get; set; }
    /// <summary>Measured setups, best coins/hour first, at most 12. Empty while nothing is measured.</summary>
    public List<GearCluster> Clusters { get; set; } = new();
    /// <summary>Measured effects of modifier changes (same player, same task, before and after).</summary>
    public List<GearModifierEffect> ModifierEffects { get; set; } = new();
    /// <summary>
    /// The task's curated setups with modelled estimates (every setup has basis "estimate"), the fallback for tiers
    /// without a measured cluster. Null when no profile is researched for the task or the estimate failed.
    /// </summary>
    public TaskGearSetups? Estimates { get; set; }
}

/// <summary>One measured setup: the observations sharing a setup key, summarized over players.</summary>
public class GearCluster
{
    /// <summary>Always "measured".</summary>
    public string Basis { get; set; } = GearSetupBasis.Measured;
    /// <summary>Readable setup key ("L2|armor=...|pet=...|tool=..."), stable between derivations.</summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// 1 = tool + armor, 2 = plus the pet, 3 = plus the rate relevant modifiers and the equipment. Levels are nested: a
    /// cluster holds every observation under its key, so an L1 cluster stands for everyone with that tool and armor,
    /// including the players of its finer clusters (children are subsets, see <see cref="ParentKey"/>).
    /// </summary>
    public int Level { get; set; }
    /// <summary>Distinct players (at least 2; show exactly 2 as "2+").</summary>
    public int Players { get; set; }
    /// <summary>Exact from 5 players; below that the lower bound of a 1-2-5 band (see <see cref="ObservationsLabel"/>).</summary>
    public int Observations { get; set; }
    /// <summary>Observations for display: the exact count from 5 players, else the band ("5+").</summary>
    public string ObservationsLabel { get; set; } = "";
    /// <summary>Play time of all observations, rounded to 2 significant digits.</summary>
    public double Hours { get; set; }
    /// <summary>The key of the enclosing cluster one level up (this cluster's observations are a subset of it), null for level 1.</summary>
    public string? ParentKey { get; set; }
    /// <summary>True when a finer published cluster has this cluster as its parent: its numbers include those players.</summary>
    public bool IncludesChildren { get; set; }
    /// <summary>Median over players of each player's hours weighted common coins/hour.</summary>
    public double CommonCoinsPerHour { get; set; }
    /// <summary>Rare drop value per hour, pooled (all rare coins / all hours).</summary>
    public double RareCoinsPerHour { get; set; }
    /// <summary>CommonCoinsPerHour + RareCoinsPerHour (of the published, possibly rounded, parts).</summary>
    public double CoinsPerHour { get; set; }
    /// <summary>25th percentile of the players' common rates; null below 5 players.</summary>
    public double? CommonP25 { get; set; }
    /// <summary>75th percentile of the players' common rates; null below 5 players.</summary>
    public double? CommonP75 { get; set; }
    /// <summary>high (5+ players, coefficient of variation &lt;= 0.35), medium (3+ players) or low (2 players).</summary>
    public string Confidence { get; set; } = "";
    /// <summary>True below 5 players: rates and stats rounded to 2 significant digits, observations banded, no percentiles.</summary>
    public bool Rounded { get; set; }
    /// <summary>
    /// The setup's items by slot (helmet .. boots, necklace .. gloves, tool): the tag and only the modifiers at least 2
    /// distinct players share, lore stats as medians over players.
    /// </summary>
    public Dictionary<string, GearPiece> Slots { get; set; } = new();
    /// <summary>The pet type at least 2 players share; tier, held item and level likewise (level 0 = not shared).</summary>
    public GearPet? Pet { get; set; }
    /// <summary>Medians over players of the effective stats (base stats plus the gear, "base+gear" observations only).</summary>
    public Dictionary<string, double> StatMedians { get; set; } = new();
}

/// <summary>
/// The measured effect of changing part of a setup: players who played the task with both setup keys for at least an
/// hour each, the ratio of their common rates after / before (median over players).
/// </summary>
public class GearModifierEffect
{
    public string Basis { get; set; } = GearSetupBasis.Measured;
    /// <summary>What differs between the two setups.</summary>
    public List<GearKeyChange> Changes { get; set; } = new();
    public int Players { get; set; }
    /// <summary>Before/after pairs over all players (a player can switch more than once); a 1-2-5 band lower bound when rounded.</summary>
    public int Pairs { get; set; }
    /// <summary>Common rate with the To values divided by the rate with the From values.</summary>
    public double Ratio { get; set; }
    /// <summary>(Ratio - 1) * 100.</summary>
    public double PercentChange { get; set; }
    /// <summary>True below 5 players: the percent change is rounded to 2 significant digits and the pairs are banded.</summary>
    public bool Rounded { get; set; }
}

/// <summary>One part of a setup key that changed, e.g. tool.enchants.harvesting 5 -&gt; 6.</summary>
public class GearKeyChange
{
    /// <summary>tool, armor, pet, equipment, tool.reforge, tool.enchants.&lt;name&gt;, &lt;slot&gt;.enchants.&lt;name&gt;, tool.parts.&lt;key&gt;, tool.applied.&lt;key&gt;, tool.counters.&lt;key&gt;.</summary>
    public string Component { get; set; } = "";
    /// <summary>Null when the part was absent.</summary>
    public string? From { get; set; }
    /// <summary>Null when the part was removed.</summary>
    public string? To { get; set; }
}
