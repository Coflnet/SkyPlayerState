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
    /// <summary>... and is widened down to at most this one to reach <see cref="LateGameMinPlayers"/> and <see cref="LateGameMinHours"/>.</summary>
    public double LateGameFloorPercentile { get; init; } = 0.75;
    /// <summary>
    /// Distinct players the late game cohort needs. Stricter than <see cref="MinPlayers"/>: cohorts of 2 players over 1 h
    /// swung by 4x between two hourly derivations (Wheat 18 -> 4.4M/h, live check 2026-11-09).
    /// </summary>
    public int LateGameMinPlayers { get; init; } = 3;
    /// <summary>Play time the late game cohort needs, summed over its counting builds.</summary>
    public double LateGameMinHours { get; init; } = 2;
    /// <summary>
    /// Shrinkage of the late game rate toward its prior (<see cref="GearLateGamePrior"/>): the cohort's weight is
    /// hours / (hours + this), so a cohort of 3 h counts half, one of 27 h 90 %; 0 turns the shrinkage off. TUNABLE.
    /// </summary>
    public double LateGamePriorHours { get; init; } = 3;
    /// <summary>Whether the late game rate is never published below the all-player prior (<see cref="GearLateGamePrior.Floor"/>).</summary>
    public bool LateGameFloorAtPrior { get; init; } = true;
    /// <summary>
    /// Distinct players and play time the rate curve needs. 8 players: curves of 5 to 6 players predicted absurd rates at
    /// the P90 (Sven 172.7M/h with 6 players, live check 2026-11-09).
    /// </summary>
    public int CurveMinPlayers { get; init; } = 8;
    public double CurveMinHours { get; init; } = 3;
    /// <summary>Hours weight of one player-build in the curve fit is capped here, so one long grinder cannot dominate the slope.</summary>
    public double CurveMaxWeightHours { get; init; } = 10;
    /// <summary>Above this many player-builds the curve is fit on <see cref="CurveBins"/> score bins (median per bin).</summary>
    public int CurveMaxPairwisePoints { get; init; } = 300;
    public int CurveBins { get; init; } = 50;
    /// <summary>Whether curve predictions are capped (<see cref="GearCurveModel.CommonCap"/>); false turns the cap off by config.</summary>
    public bool CurveCapEnabled { get; init; } = true;
    /// <summary>
    /// A curve prediction (common coins per hour) is capped at this multiple of the median player's common rate, and at
    /// the highest common rate <see cref="CurveCapPlayers"/> players reach (<see cref="GearCurveModel.CommonCap"/>). TUNABLE.
    /// </summary>
    public double CurveMaxMedianMultiple { get; init; } = 3;
    /// <summary>
    /// The cap's top rate is the one this many players reach (3, not k = 2: where the cap binds, the published rate would
    /// otherwise be the second best player's exact rate); the cap is also rounded to 2 significant digits.
    /// </summary>
    public int CurveCapPlayers { get; init; } = 3;
    /// <summary>The recommended build's rate is never read from the curve further than this fraction beyond the observed score range.</summary>
    public double MaxExtrapolation { get; init; } = 0.2;

    public static readonly GearModelOptions Default = new();
}
