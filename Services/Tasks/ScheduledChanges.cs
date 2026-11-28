using System;
using System.Threading;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Estimator changes that are deployed but only take effect from a start instant. Before it the code behaves as it did
/// before the change. Check with <see cref="IsOn"/> at the few decision points.
/// </summary>
public static class ScheduledChanges
{
    /// <summary>
    /// Switches MethodTask's formula cost top-up (observed spend only replaces its own part of the formula cost instead of
    /// the whole estimate, the withheld reference value follows it) and the Lunar Moth bait rate and cost bound. Waits for
    /// a week of cost data observed under the new AFK rule, which the new top-up should be judged against.
    /// </summary>
    public static readonly DateTime FormulaCostTopUpFrom = new(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Switches TaskEstimator's formula prior from the fixed pseudo-hours to the agreement weighted ones. Waits for the same
    /// week of data as <see cref="FormulaCostTopUpFrom"/>, so both are compared on data from the new AFK rule.
    /// </summary>
    public static readonly DateTime FormulaPriorAgreementFrom = new(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);

    private static readonly AsyncLocal<DateTime?> NowOverride = new();

    /// <summary>The instant checked against: the clock, or what a test set with <see cref="SetNowForTest"/>.</summary>
    public static DateTime Now => NowOverride.Value ?? TimeProvider.System.GetUtcNow().UtcDateTime;

    /// <summary>Whether the change starting at <paramref name="from"/> is in effect (the start instant itself is).</summary>
    public static bool IsOn(DateTime from) => Now >= from;

    /// <summary>Pins <see cref="Now"/> for the current async flow (test only, null restores the clock).</summary>
    public static void SetNowForTest(DateTime? now) => NowOverride.Value = now;
}
