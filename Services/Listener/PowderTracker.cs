using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Books powder gains (increase of a powder amount between two readings) into the current period. Only gains over a
/// recent baseline count, clamped to the rate <see cref="MaxGainPerHour"/> (a HOTM reset refund books at most the cap);
/// a stale or legacy baseline, the alpha network and a profile change only move the baseline.
/// </summary>
public static class PowderTracker
{
    /// <summary>Gains over an older baseline are not attributable to the current period (same window as copper).</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(30);

    /// <summary>Shorter intervals count as this long, so one chest right after a reading is not an absurd rate.</summary>
    public static readonly TimeSpan MinRateInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Highest believable gain per hour, the same for every powder. The code holds no measured powder rates (powder
    /// is valued by fixed coin values only), so this is a generous guard against non grinding jumps: a HOTM reset
    /// refunds millions at once, fast powder grinding (Crystal Hollows chests, 2x powder events) stays far below.
    /// </summary>
    public const double MaxGainPerHour = 500_000;

    /// <summary>
    /// Tracks one reading of powder <paramref name="amounts"/> on <paramref name="profile"/> (null = unknown).
    /// On the alpha network nothing is booked and the baselines are forgotten, so the first main reading is a fresh one.
    /// </summary>
    public static void Track(UpdateArgs args, IReadOnlyDictionary<string, int> amounts, string? profile, ILogger logger)
    {
        if (amounts.Count == 0)
            return;
        var info = args.currentState.ExtractedInfo;
        if (info.OnAlphaNetwork)
        {
            PowderTab.ResetBaselines(info, null);
            return;
        }
        PowderTab.ClaimBaselines(info, profile);
        var now = args.msg.ReceivedAt;
        var age = now - info.PowderSeenAt;
        foreach (var (tag, current) in amounts)
            TrackOne(args, tag, current, age, logger);
        info.PowderSeenAt = now;
    }

    /// <summary>Whether a baseline <paramref name="age"/> old is recent enough to book a gain over it.</summary>
    public static bool IsFresh(TimeSpan age) => age >= TimeSpan.Zero && age <= MaxReadingAge;

    /// <summary>Most powder believable as grinding over <paramref name="age"/> (at least <see cref="MinRateInterval"/>).</summary>
    public static int MaxGain(TimeSpan age)
        => (int)(MaxGainPerHour * (age < MinRateInterval ? MinRateInterval : age).TotalHours);

    /// <summary>
    /// Books the gain over a fresh baseline, clamped to <see cref="MaxGain"/>: a big legitimate reward (a corpse, a chest)
    /// is undercounted rather than lost, a HOTM reset refund books at most the cap. A stale baseline books nothing.
    /// </summary>
    private static void TrackOne(UpdateArgs args, string tag, int current, TimeSpan age, ILogger logger)
    {
        if (current < 0)
            return;
        var info = args.currentState.ExtractedInfo;
        var previous = PowderTab.Get(info, tag);
        PowderTab.Set(info, tag, current);
        if (previous <= 0 || current <= previous)
            return;
        var gain = current - previous;
        if (!IsFresh(age))
        {
            logger.LogDebug("Powder gain of {gain} {tag} for {playerId} not booked (baseline age {age})",
                gain, tag, args.currentState.PlayerId, age);
            return;
        }
        Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, Clamp(args, tag, gain, age, logger));
    }

    private static int Clamp(UpdateArgs args, string tag, int gain, TimeSpan age, ILogger logger)
    {
        var max = MaxGain(age);
        if (gain <= max)
            return gain;
        logger.LogDebug("Powder gain of {gain} {tag} for {playerId} clamped to {max} (baseline age {age})",
            gain, tag, args.currentState.PlayerId, max, age);
        return max;
    }
}
