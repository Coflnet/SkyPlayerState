using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Accumulates location fragments into a task <see cref="TaskSession"/> that spans
/// location changes, so tasks performed across multiple areas (Diana, crystal
/// hollows, mob hunting, ...) are folded as one session instead of being chopped
/// into sub-window fragments that individually fail the classifier's minimum
/// signal gate and understate the task's value.
///
/// A session is flushed (returned for folding) when a fragment clearly shifts the
/// classification to a different task, when the player goes idle (no items for
/// <see cref="IdleFlushMinutes"/>), or when it reaches <see cref="MaxSessionMinutes"/>.
/// The idle path is what filters out AFK players; the minimum-window gate in the
/// classifier/folder is what filters drive-by non-doers - neither should penalise
/// someone legitimately moving between a task's locations.
/// </summary>
public class TaskSessionService
{
    /// <summary>A session is cut and re-started once it reaches this length, bounding fold size and getting data in sooner.</summary>
    public const double MaxSessionMinutes = 30;
    /// <summary>No items collected for this long finalizes the session (matches the 5 min idle flush cadence).</summary>
    public const double IdleFlushMinutes = 5;
    /// <summary>Cap on distinct item tags kept in a session to bound the state blob.</summary>
    public const int MaxDistinctItems = 40;

    private readonly TaskClassifier classifier;

    public TaskSessionService(TaskClassifier classifier)
    {
        this.classifier = classifier;
    }

    /// <summary>
    /// Merge a just-flushed location fragment (may be empty on an idle tick) into the
    /// running session on <paramref name="info"/>. Returns a synthetic period spanning
    /// the accumulated session when a session boundary is crossed, otherwise null.
    /// </summary>
    /// <param name="info">the player state carrying <see cref="ExtractedInfo.CurrentSession"/></param>
    /// <param name="playerUuid">uuid stamped on flushed periods</param>
    /// <param name="fragment">the location fragment that was just flushed; empty items = idle tick</param>
    /// <param name="claimedTask">manually claimed task, biases classification tie breaks</param>
    /// <param name="prices">coin values for classification tie breaking, may be null</param>
    /// <param name="now">current time</param>
    public Period Accumulate(ExtractedInfo info, string playerUuid, Period fragment,
        string claimedTask, Dictionary<string, double> prices, DateTime now)
    {
        var session = info.CurrentSession;
        var hasItems = fragment.ItemsCollected != null && fragment.ItemsCollected.Count > 0
            && fragment.ItemsCollected.Values.Any(v => v > 0);

        // idle tick: nothing collected this fragment
        if (!hasItems)
        {
            if (session != null && (now - session.LastItemTime).TotalMinutes >= IdleFlushMinutes)
            {
                info.CurrentSession = null;
                return BuildPeriod(session, playerUuid);
            }
            return null;
        }

        if (session == null)
        {
            info.CurrentSession = StartSession(fragment, claimedTask, prices);
            return null;
        }

        // tentatively merge and see whether the fragment shifts the classification
        var tentative = Merge(session.Items, fragment.ItemsCollected);
        var startTime = session.StartTime == default ? fragment.StartTime : session.StartTime;
        var minutes = (fragment.EndTime - startTime).TotalMinutes;
        var newTask = classifier.Classify(fragment.Location, tentative, minutes, claimedTask, prices)?.TaskName;

        if (session.DetectedTask != null && newTask != null && newTask != session.DetectedTask)
        {
            // the fragment belongs to a different task -> flush the pre-merge session,
            // begin a fresh session from this fragment.
            var flush = BuildPeriod(session, playerUuid);
            info.CurrentSession = StartSession(fragment, claimedTask, prices);
            return flush;
        }

        session.Items = Cap(tentative);
        session.StartTime = startTime;
        session.LastItemTime = fragment.EndTime;
        session.Location = fragment.Location;
        session.Server = fragment.Server;
        session.DetectedTask = newTask ?? session.DetectedTask;

        if ((now - session.StartTime).TotalMinutes >= MaxSessionMinutes)
        {
            info.CurrentSession = null;
            return BuildPeriod(session, playerUuid);
        }
        return null;
    }

    private TaskSession StartSession(Period fragment, string claimedTask, Dictionary<string, double> prices)
    {
        var items = Cap(Merge(new Dictionary<string, int>(), fragment.ItemsCollected));
        var minutes = (fragment.EndTime - fragment.StartTime).TotalMinutes;
        return new TaskSession
        {
            StartTime = fragment.StartTime,
            LastItemTime = fragment.EndTime,
            Location = fragment.Location,
            Server = fragment.Server,
            Items = items,
            DetectedTask = classifier.Classify(fragment.Location, items, minutes, claimedTask, prices)?.TaskName
        };
    }

    private static Period BuildPeriod(TaskSession session, string playerUuid) => new()
    {
        PlayerUuid = playerUuid,
        Server = session.Server,
        Location = session.Location,
        StartTime = session.StartTime,
        EndTime = session.LastItemTime,
        ItemsCollected = new Dictionary<string, int>(session.Items),
        DetectedTask = session.DetectedTask
    };

    private static Dictionary<string, int> Merge(Dictionary<string, int> into, Dictionary<string, int> from)
    {
        var result = new Dictionary<string, int>(into);
        foreach (var (tag, count) in from)
        {
            if (count <= 0)
                continue;
            result[tag] = result.GetValueOrDefault(tag) + count;
        }
        return result;
    }

    private static Dictionary<string, int> Cap(Dictionary<string, int> items)
    {
        if (items.Count <= MaxDistinctItems)
            return items;
        return items.OrderByDescending(e => e.Value).Take(MaxDistinctItems)
            .ToDictionary(e => e.Key, e => e.Value);
    }
}
