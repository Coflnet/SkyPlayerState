using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Tasks.Gear;

namespace Coflnet.Sky.PlayerState.Models;

// Response shapes of GET /Task/gear-model/{taskName} (and the older /Task/gear-clusters/{taskName}), consumed by SkyApi
// through the generated PlayerState.Client. Never contains player keys, fingerprints or per-player rows.

/// <summary>Values of <see cref="GearModifierEffect.Basis"/> and <see cref="GearSetupEstimate.Basis"/>.</summary>
public static class GearSetupBasis
{
    /// <summary>Derived from task session observations of at least two distinct players.</summary>
    public const string Measured = "measured";
    /// <summary>A curated setup with a modelled estimate: shown only where nothing is measured, labelled "estimate".</summary>
    public const string Estimate = "estimate";
}

/// <summary>
/// The measured gear model of one task (see <see cref="Services.Gear.GearModelService"/>): the distribution of the gear
/// score over player-builds, the late game cohort (top 10 %), the rate curve over the score, the privacy safe piece
/// catalog, the recommended "maximum achievable" endgame build, the measured modifier effects and the curated estimates
/// as the fallback. Every number comes from at least <see cref="MinPlayers"/> distinct players; below 5 players numbers
/// are rounded to 2 significant digits and counts banded.
/// </summary>
public class TaskGearModel
{
    public string TaskName { get; set; } = "";
    /// <summary>Gear category of the task (the bound profile id, "other" for unbound tasks).</summary>
    public string Category { get; set; } = "";
    /// <summary>When the model was derived (hourly); null before the first derivation after a restart.</summary>
    public DateTime? ComputedAt { get; set; }
    /// <summary>How many weeks of observations the model is derived from.</summary>
    public int WindowWeeks { get; set; }
    /// <summary>Distinct players every published number, piece or modifier needs (k).</summary>
    public int MinPlayers { get; set; }
    /// <summary>Play time the distribution and the late game cohort need.</summary>
    public double MinHours { get; set; }
    /// <summary>How the gear score of a build is computed (for display; the weights are tuned in the data review).</summary>
    public GearScoreFormulaInfo ScoreFormula { get; set; } = new();
    /// <summary>The score distribution over the player-builds with known tool and armor; null below k players.</summary>
    public GearScoreSummary? Distribution { get; set; }
    /// <summary>The builds from the 90th score percentile (widened to k players, never below the 75th); null without.</summary>
    public GearLateGame? LateGame { get; set; }
    /// <summary>Coins/hour as a function of the score; null below 5 players or 3 hours.</summary>
    public GearRateCurve? Curve { get; set; }
    /// <summary>The pieces measured builds are composed from, by slot, best contribution first.</summary>
    public List<GearCatalogPiece> Pieces { get; set; } = new();
    /// <summary>The best catalog piece per slot (set bonuses respected) and its rate; null without a curve or a late game cohort.</summary>
    public GearRecommendedBuild? RecommendedEndgame { get; set; }
    /// <summary>Measured effects of modifier changes (same player, same task, before and after).</summary>
    public List<GearModifierEffect> ModifierEffects { get; set; } = new();
    /// <summary>
    /// The task's curated setups with modelled estimates (every setup has basis "estimate"), the fallback while nothing
    /// is measured. Null when no profile is researched for the task or the estimate failed.
    /// </summary>
    public TaskGearSetups? Estimates { get; set; }
}

/// <summary>
/// The score formula: <c>weighted-sum</c> (Σ weight × stat total), <c>log-damage-product</c> (combat) or <c>none</c> (an
/// unbound task that is not fought: no distribution, late game, curve, pieces or recommended build).
/// </summary>
public class GearScoreFormulaInfo
{
    public string Kind { get; set; } = "";
    /// <summary>Stat name to weight (for the damage product each listed stat is a factor).</summary>
    public Dictionary<string, double> Weights { get; set; } = new();
    /// <summary>Readable formula, e.g. "1 × Farming Fortune + 1 × Wheat Fortune".</summary>
    public string Expression { get; set; } = "";
    /// <summary>Special effects (set bonuses, pet perks, ...) the score of this task can include.</summary>
    public int EffectCount { get; set; }
}

/// <summary>
/// The task's gear score distribution over player-builds with a known tool and at least 3 armor pieces; one vote per
/// player, split over their builds. Scores are always rounded to 2 significant digits.
/// </summary>
public class GearScoreSummary
{
    /// <summary>Exact from 5 players, else the lower bound of a 1-2-5 band.</summary>
    public int Players { get; set; }
    /// <summary>Exact from 5 players, else the lower bound of a 1-2-5 band.</summary>
    public int PlayerBuilds { get; set; }
    /// <summary>Play time, 2 significant digits.</summary>
    public double Hours { get; set; }
    public double ScoreP10 { get; set; }
    public double ScoreP25 { get; set; }
    public double ScoreP50 { get; set; }
    public double ScoreP75 { get; set; }
    public double ScoreP90 { get; set; }
    /// <summary>Share (0 .. 1, one decimal) of the builds whose equipment is unknown (counted as 0 in their score).</summary>
    public double UnknownEquipmentShare { get; set; }
    /// <summary>True below 5 players: the player and build counts and the hours are banded.</summary>
    public bool Rounded { get; set; }
}

/// <summary>
/// The late game cohort: the players with a build at or above <see cref="ScoreFrom"/> (2 significant digits); each
/// player's builds there (at least 10 minutes each) are pooled into one hours weighted rate.
/// </summary>
public class GearLateGame
{
    public double ScoreFrom { get; set; }
    /// <summary>True when the cohort had to be widened below the 90th percentile to reach k players.</summary>
    public bool Widened { get; set; }
    /// <summary>Exact from 5 players, else the lower bound of a 1-2-5 band (see <see cref="PlayersLabel"/>).</summary>
    public int Players { get; set; }
    /// <summary>"7", or "2+" below 5 players.</summary>
    public string PlayersLabel { get; set; } = "";
    /// <summary>Play time, 2 significant digits; below 5 players the lower bound of a 1-2-5 band.</summary>
    public double Hours { get; set; }
    /// <summary>Median over players of each player's pooled common coins/hour.</summary>
    public double CommonCoinsPerHour { get; set; }
    /// <summary>Rare drop value per hour, pooled (all rare coins / all hours).</summary>
    public double RareCoinsPerHour { get; set; }
    public double CoinsPerHour { get; set; }
    /// <summary>25th / 75th percentile of the players' common rates; null below 5 players.</summary>
    public double? CommonP25 { get; set; }
    public double? CommonP75 { get; set; }
    /// <summary>high (5+ players, coefficient of variation &lt;= 0.35), medium (5+ players) or low (below 5).</summary>
    public string Confidence { get; set; } = "";
    public bool Rounded { get; set; }
    /// <summary>Medians over players of the effective stats ("base+gear" observations), stats of k players only.</summary>
    public Dictionary<string, double> StatMedians { get; set; } = new();
}

/// <summary>
/// ln(common coins/hour) fit robustly (Theil–Sen) against the score, one vote per player (only pairs of different
/// players). Only published with a non-negative slope and finite values. Scores 2 significant digits.
/// </summary>
public class GearRateCurve
{
    public int Players { get; set; }
    /// <summary>Percent change of the rate for +10 % score (of the underlying stat value for the log scale, at the median score otherwise).</summary>
    public double SlopePer10Pct { get; set; }
    /// <summary>Pooled rare drop value per hour of the task, added to every point.</summary>
    public double RareCoinsPerHour { get; set; }
    /// <summary>The predicted rate at the distribution's score percentiles 10, 25, 50, 75 and 90.</summary>
    public List<GearCurvePoint> Points { get; set; } = new();
    /// <summary>Observed score range: the k-th lowest and k-th highest player score, 2 significant digits.</summary>
    public double ScoreMin { get; set; }
    public double ScoreMax { get; set; }
    /// <summary>theil-sen, or binned-theil-sen for many builds.</summary>
    public string Method { get; set; } = "";
}

public class GearCurvePoint
{
    public int ScorePercentile { get; set; }
    public double Score { get; set; }
    public double CommonCoinsPerHour { get; set; }
    public double CoinsPerHour { get; set; }
}

/// <summary>A piece (or the pet) of the catalog with what SkyApi needs to price it and to rank it.</summary>
public class GearCatalogPiece
{
    /// <summary>Stable within a derivation: "slot:TAG:hash" or "pet:TYPE:TIER:LEVEL:HELD"; referenced by <see cref="GearRecommendedBuild.Pieces"/>.</summary>
    public string Id { get; set; } = "";
    /// <summary>tool, helmet .. boots, necklace .. gloves or pet.</summary>
    public string Slot { get; set; } = "";
    /// <summary>The item spec (tag and only the modifiers k players share, lore stats as medians); null for the pet.</summary>
    public GearPiece? Item { get; set; }
    /// <summary>The pet spec (type; the tier and level at least 2 players reach; the held item when shared); null for items.</summary>
    public GearPet? Pet { get; set; }
    /// <summary>
    /// The piece's score alone plus its share of the full set bonuses it belongs to; for the pet, the score it adds to the
    /// task's most common published tool and armor (so perks that need them count).
    /// </summary>
    public double ScoreContribution { get; set; }
    /// <summary>Median score percentile (0 .. 100) of the builds using the piece; null below k players with known tool and armor.</summary>
    public double? UsedAtScorePercentile { get; set; }
    /// <summary>Special effects the piece triggers alone, and its share of set bonuses.</summary>
    public List<GearPieceEffect> Effects { get; set; } = new();
    /// <summary>Exact from 5 players, else the lower bound of a 1-2-5 band (see <see cref="PlayersLabel"/>).</summary>
    public int Players { get; set; }
    public string PlayersLabel { get; set; } = "";
    public bool Rounded { get; set; }
}

/// <summary>An effect of the effects table on a piece (approximate stat equivalent).</summary>
public class GearPieceEffect
{
    public string Id { get; set; } = "";
    /// <summary>set, pet, equipment or enchant.</summary>
    public string Kind { get; set; } = "";
    /// <summary>add or multiply.</summary>
    public string Mode { get; set; } = "";
    public List<string> Stats { get; set; } = new();
    /// <summary>The added value (or the fraction for multiply); for a set share the per-piece share of the full set bonus.</summary>
    public double Value { get; set; }
    /// <summary>True when <see cref="Value"/> is a share of a set bonus that needs the other pieces.</summary>
    public bool SetShare { get; set; }
    public string Confidence { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>The recommended "maximum achievable" endgame build.</summary>
public class GearRecommendedBuild
{
    /// <summary>Slot to the <see cref="GearCatalogPiece.Id"/> of the chosen piece.</summary>
    public Dictionary<string, string> Pieces { get; set; } = new();
    /// <summary>2 significant digits.</summary>
    public double Score { get; set; }
    /// <summary>Where the score lies in the distribution (0 .. 100); null without a distribution.</summary>
    public double? ScorePercentile { get; set; }
    public double CoinsPerHour { get; set; }
    /// <summary>The score is above the observed range: the rate is read from the curve at most 20 % beyond it.</summary>
    public bool Extrapolated { get; set; }
    /// <summary>
    /// The curve's rate was above the cap and was capped (2 significant digits): the late game cohort's P75 per-player
    /// rate from 5 players, else the cohort's published rate.
    /// </summary>
    public bool Capped { get; set; }
    /// <summary>curve (rate from the curve at the score) or cohort (no curve: the late game cohort's rate).</summary>
    public string Basis { get; set; } = "";
    /// <summary>Ids of the special effects the build triggers (set bonuses, pet perks, ...).</summary>
    public List<string> Effects { get; set; } = new();
}

/// <summary>
/// The measured effect of changing part of a setup: players who played the task with both setups for at least an hour
/// each, the ratio of their common rates after / before (median over players).
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

/// <summary>One part of a setup that changed, e.g. tool.enchants.harvesting 5 -&gt; 6.</summary>
public class GearKeyChange
{
    /// <summary>tool, armor, pet, equipment, tool.reforge, tool.enchants.&lt;name&gt;, &lt;slot&gt;.enchants.&lt;name&gt;, tool.parts.&lt;key&gt;, tool.applied.&lt;key&gt;.</summary>
    public string Component { get; set; } = "";
    /// <summary>Null when the part was absent.</summary>
    public string? From { get; set; }
    /// <summary>Null when the part was removed.</summary>
    public string? To { get; set; }
}
