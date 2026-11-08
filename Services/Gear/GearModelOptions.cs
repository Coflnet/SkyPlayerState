namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Thresholds of the gear model derivation (<see cref="GearModelService.Compute"/>).</summary>
public sealed record GearModelOptions
{
    /// <summary>Distinct players a published number, piece, modifier or modifier effect needs (k).</summary>
    public int MinPlayers { get; init; } = 2;
    /// <summary>Play time the score distribution and the late game cohort need, summed over their builds.</summary>
    public double MinHours { get; init; } = 1;
    /// <summary>Play time a player needs on each side of a modifier change.</summary>
    public double MinHoursPerSide { get; init; } = 1;
    /// <summary>The late game cohort starts at this score percentile ...</summary>
    public double LateGamePercentile { get; init; } = 0.9;
    /// <summary>... and is widened down to at most this one to reach <see cref="MinPlayers"/>.</summary>
    public double LateGameFloorPercentile { get; init; } = 0.75;
    /// <summary>Distinct players and play time the rate curve needs.</summary>
    public int CurveMinPlayers { get; init; } = 5;
    public double CurveMinHours { get; init; } = 3;
    /// <summary>Hours weight of one player-build in the curve fit is capped here, so one long grinder cannot dominate the slope.</summary>
    public double CurveMaxWeightHours { get; init; } = 10;
    /// <summary>Above this many player-builds the curve is fit on <see cref="CurveBins"/> score bins (median per bin).</summary>
    public int CurveMaxPairwisePoints { get; init; } = 300;
    public int CurveBins { get; init; } = 50;
    /// <summary>The recommended build's rate is never read from the curve further than this fraction beyond the observed score range.</summary>
    public double MaxExtrapolation { get; init; } = 0.2;

    public static readonly GearModelOptions Default = new();
}
