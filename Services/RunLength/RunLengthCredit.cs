using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Only completed runs feed <see cref="IRunLengthRecorder"/> (dungeon results header, Kuudra's "KUUDRA DOWN!"). The
/// completion line can arrive a moment after the scoreboard already moved on, so a stay that ended without one is
/// remembered on <see cref="ExtractedInfo"/> for <see cref="LateCompletionWindow"/> and credited by a late completion line.
/// </summary>
public static class RunLengthCredit
{
    /// <summary>How long after the zone change a completion line still credits the run that just ended.</summary>
    public static readonly TimeSpan LateCompletionWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when a completion line arriving at <paramref name="now"/> belongs to the run remembered on <paramref name="info"/>
    /// rather than to the current stay: the current stay only just started (a directly requeued run, see
    /// CollectionListener.IsNewInstanceInSameZone) and no run can be completed that fast.
    /// </summary>
    public static bool BelongsToPreviousRun(ExtractedInfo info, string keyPrefix, DateTime now)
        => info.UnrecordedRunKey != null && info.UnrecordedRunKey.StartsWith(keyPrefix, StringComparison.Ordinal)
            && now - info.CurrentLocationSince <= LateCompletionWindow;

    /// <summary>Remembers a stay that ended without a completion signal (overwrites an older one).</summary>
    public static void Remember(ExtractedInfo info, string key, TimeSpan length, DateTime endedAt)
    {
        info.UnrecordedRunKey = key;
        info.UnrecordedRunSeconds = length.TotalSeconds;
        info.UnrecordedRunEndedAt = endedAt;
    }

    /// <summary>
    /// Records the remembered stay when its key starts with <paramref name="keyPrefix"/> ("dungeon:", "kuudra:") and it ended
    /// at most <see cref="LateCompletionWindow"/> before <paramref name="now"/>. Returns whether a run was credited.
    /// The remembered stay is cleared either way when it matched the prefix.
    /// </summary>
    public static async Task<bool> TryCredit(UpdateArgs args, string keyPrefix, DateTime now, ILogger logger)
    {
        var info = args.currentState.ExtractedInfo;
        if (info.UnrecordedRunKey == null || !info.UnrecordedRunKey.StartsWith(keyPrefix, StringComparison.Ordinal))
            return false;
        var key = info.UnrecordedRunKey;
        var length = TimeSpan.FromSeconds(info.UnrecordedRunSeconds);
        var fresh = now - info.UnrecordedRunEndedAt <= LateCompletionWindow;
        info.UnrecordedRunKey = null;
        if (!fresh)
            return false;
        try
        {
            await args.GetService<IRunLengthRecorder>().Record(key, length,
                keyPrefix == "kuudra:" ? RunLengthBounds.Kuudra : RunLengthBounds.Dungeon);
            return true;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not record late run length {key}", key);
            return false;
        }
    }
}
