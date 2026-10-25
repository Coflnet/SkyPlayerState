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
    private readonly CoinValueRegistry coinValues;

    /// <param name="coinValues">values the per-zone coins (powder overrides); without it only prices and pseudo tags count</param>
    public TaskSessionService(TaskClassifier classifier, CoinValueRegistry coinValues = null)
    {
        this.classifier = classifier;
        this.coinValues = coinValues;
    }

    /// <summary>
    /// Key a fragment's location is accounted under in the per zone "best spot" data: the canonical zone
    /// (pest count suffix etc. stripped). Garden plots ("Plot - N", free text names) collapse to "The Garden":
    /// a plot is part of the Garden and the individual plot names carry no information a player can act on.
    /// </summary>
    internal static string ZoneKey(string location)
    {
        var zone = SkyblockZones.Canonical(location);
        if (string.IsNullOrWhiteSpace(zone))
            return null;
        return zone == "Plot" ? "The Garden" : zone;
    }

    private double ValueOf(Dictionary<string, int> items, Dictionary<string, double> prices)
    {
        double total = 0;
        foreach (var (tag, count) in items)
        {
            var unit = coinValues != null ? coinValues.Value(tag, prices)
                : PseudoItems.TryGetCoinValue(tag, out var pseudo) ? pseudo
                : prices?.GetValueOrDefault(tag) ?? 0;
            total += unit * count;
        }
        return total;
    }

    /// <summary>Adds the fragment's duration and coin value (costs negative) to its zone on <paramref name="session"/>.</summary>
    private void AddZone(TaskSession session, Period fragment, Dictionary<string, double> prices)
    {
        var zone = ZoneKey(fragment.Location);
        if (zone == null)
            return;
        session.ZoneSeconds ??= new();
        session.ZoneValue ??= new();
        var seconds = Math.Max(0, (fragment.EndTime - fragment.StartTime).TotalSeconds);
        session.ZoneSeconds[zone] = session.ZoneSeconds.GetValueOrDefault(zone) + seconds;
        session.ZoneValue[zone] = session.ZoneValue.GetValueOrDefault(zone) + ValueOf(fragment.ItemsCollected, prices);
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
            info.CurrentSession = StartSession(fragment, claimedTask, prices, info.CurrentIsland, info.CurrentIslandAt,
                info.CurrentLocationSince, info.CurrentLocationSeenAt);
            return null;
        }

        // unseen gap or catch-up/real boundary: never merge across it
        if (HasGap(session, fragment) || CrossesUnknownBoundary(session, fragment))
        {
            var stale = BuildPeriod(session, playerUuid);
            info.CurrentSession = StartSession(fragment, claimedTask, prices, info.CurrentIsland, info.CurrentIslandAt,
                info.CurrentLocationSince, info.CurrentLocationSeenAt);
            return stale;
        }

        // tentatively merge and see whether the fragment shifts the classification
        var tentative = Merge(session.Items, fragment.ItemsCollected);
        var tentativePositive = PositiveOnly(tentative);
        var startTime = session.StartTime == default ? fragment.StartTime : session.StartTime;
        var minutes = (fragment.EndTime - startTime).TotalMinutes;
        var newTask = classifier.Classify(fragment.Location, tentativePositive, minutes, claimedTask, prices,
            info.CurrentIsland, info.CurrentIslandAt, info.CurrentLocationSince, info.CurrentLocationSeenAt)?.TaskName;

        var differentTask = session.DetectedTask != null && newTask != null && newTask != session.DetectedTask;
        if (differentTask || CarriesForeignPurchase(session, fragment) || LeftIsland(session, fragment, newTask))
        {
            // the fragment belongs to a different task -> flush the pre-merge session,
            // begin a fresh session from this fragment.
            var flush = BuildPeriod(session, playerUuid);
            info.CurrentSession = StartSession(fragment, claimedTask, prices, info.CurrentIsland, info.CurrentIslandAt,
                info.CurrentLocationSince, info.CurrentLocationSeenAt);
            return flush;
        }

        session.Items = Cap(tentative);
        session.StartTime = startTime;
        session.LastItemTime = fragment.EndTime;
        session.Location = fragment.Location;
        session.Server = fragment.Server;
        session.DetectedTask = newTask ?? session.DetectedTask;
        AddZone(session, fragment, prices);

        if ((now - session.StartTime).TotalMinutes >= MaxSessionMinutes)
        {
            info.CurrentSession = null;
            return BuildPeriod(session, playerUuid);
        }
        return null;
    }

    /// <summary>
    /// The fragment starts at least <see cref="IdleFlushMinutes"/> after the session's last item: the player was
    /// unseen (offline) in between, so no idle tick flushed the session. Merging would stretch it over the gap.
    /// </summary>
    private static bool HasGap(TaskSession session, Period fragment)
        => (fragment.StartTime - session.LastItemTime).TotalMinutes >= IdleFlushMinutes;

    /// <summary>
    /// Exactly one side is the Unknown location: that is the whole-inventory catch-up after a login (or a
    /// stale location). It must not be attributed to a real session, nor may real play be added to it.
    /// </summary>
    private static bool CrossesUnknownBoundary(TaskSession session, Period fragment)
        => IsUnknown(session.Location) != IsUnknown(fragment.Location);

    private static bool IsUnknown(string location)
        => string.Equals(location, Services.CollectionListener.UnknownLocation, StringComparison.Ordinal);

    /// <summary>
    /// A fragment with purchase evidence (BAZAAR_PURCHASE/AUCTION_PURCHASE) is a shopping trip: the bought
    /// item is not loot of the session's activity, so it only merges into a purchase sink.
    /// </summary>
    private bool CarriesForeignPurchase(TaskSession session, Period fragment)
        => fragment.ItemsCollected.Any(e => e.Value != 0 && PseudoItems.IsPurchase(e.Key))
            && !classifier.IsPurchaseSink(session.DetectedTask);

    /// <summary>
    /// A fragment from another island that the classifier cannot name (null classification) would otherwise
    /// be absorbed by whatever session is running. Only applies when both islands are known and differ.
    /// </summary>
    private static bool LeftIsland(TaskSession session, Period fragment, string newTask)
    {
        if (newTask != null)
            return false;
        var from = IslandOfZone(session.Location);
        var to = IslandOfZone(fragment.Location);
        return from != null && to != null && from != to;
    }

    /// <summary>
    /// <see cref="SkyblockZones.IslandOf"/> with the private island resolved: its scoreboard zone "Your Island"
    /// is deliberately ambiguous in the static map, but unlike the other ambiguous zones it only ever means the
    /// private island, so it is safe to treat as its own island here. Other ambiguous/unknown zones stay null (no split).
    /// </summary>
    internal static string IslandOfZone(string zone)
        => SkyblockZones.Canonical(zone) == "Your Island" ? "Private Island" : SkyblockZones.IslandOf(zone);

    private TaskSession StartSession(Period fragment, string claimedTask, Dictionary<string, double> prices,
        string currentIsland, DateTime currentIslandAt, DateTime currentLocationSince, DateTime currentLocationSeenAt)
    {
        var items = Cap(Merge(new Dictionary<string, int>(), fragment.ItemsCollected));
        // the classifier only ever sees gains: costs (negative counts) ride along for the folder
        var positiveItems = PositiveOnly(items);
        var minutes = (fragment.EndTime - fragment.StartTime).TotalMinutes;
        var started = new TaskSession
        {
            StartTime = fragment.StartTime,
            LastItemTime = fragment.EndTime,
            Location = fragment.Location,
            Server = fragment.Server,
            Items = items,
            DetectedTask = classifier.Classify(fragment.Location, positiveItems, minutes, claimedTask, prices,
                currentIsland, currentIslandAt, currentLocationSince, currentLocationSeenAt)?.TaskName
        };
        AddZone(started, fragment, prices);
        return started;
    }

    private static Period BuildPeriod(TaskSession session, string playerUuid) => new()
    {
        PlayerUuid = playerUuid,
        Server = session.Server,
        Location = session.Location,
        StartTime = session.StartTime,
        EndTime = session.LastItemTime,
        ItemsCollected = new Dictionary<string, int>(session.Items),
        DetectedTask = session.DetectedTask,
        ZoneSeconds = session.ZoneSeconds == null ? new() : new(session.ZoneSeconds),
        ZoneValue = session.ZoneValue == null ? new() : new(session.ZoneValue)
    };

    /// <summary>
    /// Net-sums the fragment into a copy of the session items. Negative counts (real costs such as
    /// DUNGEON_CHEST_COST, KISMET_FEATHER, keys) are kept: <see cref="TaskPeriodFolder"/> decides per task
    /// which of them count. Tags whose net becomes 0 are dropped.
    /// </summary>
    private static Dictionary<string, int> Merge(Dictionary<string, int> into, Dictionary<string, int> from)
    {
        var result = new Dictionary<string, int>(into);
        foreach (var (tag, count) in from)
        {
            if (count == 0)
                continue;
            ItemCountMath.Add(result, tag, count);
            if (result[tag] == 0)
                result.Remove(tag);
        }
        return result;
    }

    private static Dictionary<string, int> PositiveOnly(Dictionary<string, int> items)
        => items.Any(e => e.Value <= 0) ? items.Where(e => e.Value > 0).ToDictionary(e => e.Key, e => e.Value) : items;

    /// <summary>Keeps the entries with the largest absolute count, so huge negative costs are not cut.</summary>
    private static Dictionary<string, int> Cap(Dictionary<string, int> items)
    {
        if (items.Count <= MaxDistinctItems)
            return items;
        return items.OrderByDescending(e => Math.Abs((long)e.Value)).Take(MaxDistinctItems)
            .ToDictionary(e => e.Key, e => e.Value);
    }
}
