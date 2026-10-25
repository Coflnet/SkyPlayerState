using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// A single task's estimated coins per hour for one player, with the inputs that produced it.
/// Produced server side by <see cref="TaskEstimator"/> (personal/stat-bucket/global blending,
/// saturation), consumed in-process by <see cref="MethodTask.ComputeFromServerEstimate"/> to render
/// the final <see cref="TaskResult"/>, and exposed to SkyModCommands as-is via the generated
/// PlayerState.Client (this shape must stay in sync with the OpenAPI schema).
/// </summary>
public class TaskEstimate
{
    public string TaskName { get; set; }
    public string Category { get; set; }
    public string Source { get; set; }         // personal | stat_bucket | global | formula
    public byte StatBucket { get; set; }
    // Both already net of ingredient cost for recipe tasks (MethodTask.FormulaCosts, e.g. the 16
    // Fine gems a Gemstone Mixture consumes) - see TaskEstimator.RateFromAggregate/
    // RateFromPlayerStat/FormulaRate, all of which subtract MethodTask.ScaledFormulaCost before
    // this reaches either field. MethodTask.ComputeFromServerEstimate must not subtract it again.
    public double CoinsPerHour { get; set; }   // after saturation penalty
    public double RawCoinsPerHour { get; set; }// before saturation penalty
    public double PersonalTrackedMinutes { get; set; }
    public double CommunityTrackedHours { get; set; }
    public int Contributors { get; set; }
    public int CurrentDoers { get; set; }
    public int DoersChange20m { get; set; }
    public List<TaskDropRate> Drops { get; set; } = new();
    public string TraceId { get; set; }
    /// <summary>Wiki page explaining the method/main item, or null if not curated - see MethodTask.WikiUrl.</summary>
    public string WikiUrl { get; set; }
    /// <summary>The exact zone to stand in to do this method - see MethodTask.Where.</summary>
    public string Where { get; set; }
    /// <summary>The SkyBlock island Where belongs to, or null if unknown - see SkyblockZones.IslandOf.</summary>
    public string Island { get; set; }
    /// <summary>Wiki page for Island (has the island's map), or null if unknown.</summary>
    public string WhereWikiUrl { get; set; }
    /// <summary>Warp command to get close: the task's own, else the island's, else null.</summary>
    public string Warp { get; set; }
    /// <summary>Ordered, followable steps a total beginner can click through - see MethodTask.Steps.</summary>
    public List<TaskStep> Steps { get; set; } = new();
    /// <summary>Up to 3 best zones by coins/hour (same thresholds as <see cref="BestZone"/>), best first, empty when none qualifies.</summary>
    public List<BestZoneEntry> BestZones { get; set; } = new();
    /// <summary>Zone where the highest coins/hour was observed across several players, null when no zone qualifies - see TaskEstimator.PickBestZone.</summary>
    public string BestZone { get; set; }
    /// <summary>Observed coins per hour in <see cref="BestZone"/>, 0 when none.</summary>
    public double BestZoneCoinsPerHour { get; set; }
    /// <summary>Distinct players the <see cref="BestZone"/> rate was observed from, 0 when none.</summary>
    public int BestZonePlayers { get; set; }
}

public class TaskDropRate
{
    public string ItemTag { get; set; }
    public double RatePerHour { get; set; }
    public double PriceEach { get; set; }
    public double ContributionPerHour { get; set; }
}

/// <summary>A zone with the coins/hour observed there and the distinct players it came from.</summary>
public class BestZoneEntry
{
    public string Zone { get; set; }
    public double CoinsPerHour { get; set; }
    public int Players { get; set; }
}
