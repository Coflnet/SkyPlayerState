using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Turns a Crystal Nucleus run into one Crystal Nucleus period. The hunt crosses the whole Crystal Hollows (Jungle Temple,
/// Goblin Queen's Den, Mines of Divan, Lost Precursor City, Khazad-dûm), so its sessions classify as other tasks, and the
/// claim visit is too short to classify at all. While a run is active (<see cref="NucleusRunTracker"/>, from chat) every
/// Crystal Hollows session that <see cref="TaskSessionService"/> flushes is held on <see cref="NucleusRunState.Held"/>
/// instead of being folded. Once the loot bundle is in (see <see cref="ShouldClose"/>) the held sessions and the open
/// one are merged into one period: the hunt's loot plus the bundle, its time the sum of the session times. An abandoned
/// run (gap, profile switch) folds its held sessions as their own tasks, as if nothing had been held. Without run
/// evidence nothing is held, so this is a plain pass-through to <see cref="TaskSessionService.Accumulate"/>.
/// </summary>
public class NucleusRunRouter
{
    /// <summary>
    /// Most sessions held for one run. A run takes 6-20 minutes and sessions last up to 30, so a dozen covers even a
    /// slow run with many task changes; the oldest beyond it is folded as its own task.
    /// </summary>
    public const int MaxHeldSessions = 12;
    /// <summary>Most item entries over all held sessions (a session keeps up to 40), bounds the persisted state.</summary>
    public const int MaxHeldItemEntries = 300;
    /// <summary>A claim whose run was not closed by a later fragment within this time is closed anyway.</summary>
    public static readonly TimeSpan MaxClaimPending = TimeSpan.FromMinutes(30);
    /// <summary>
    /// Fragments starting this soon after the claim was processed still belong to the run: the bundle is picked up at the
    /// Nucleus Vault seconds after the claim line, but a same-zone flush can land in between.
    /// </summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Held sessions that ended this long before the run's start (or, without a known start, before the claim) are not
    /// part of the run: a session is at most this long, so the session open at the run's start is still included.
    /// </summary>
    public static readonly TimeSpan RunWindowLead = TimeSpan.FromMinutes(TaskSessionService.MaxSessionMinutes);

    private const string Hollows = "Crystal Hollows";
    private const string NucleusZone = "Crystal Nucleus";
    private readonly TaskSessionService sessions;
    private readonly ILogger logger;

    public NucleusRunRouter(TaskSessionService sessions, ILogger? logger = null)
    {
        this.sessions = sessions;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// <see cref="TaskSessionService.Accumulate"/> with the run handling around it. Returns the periods to fold now, in
    /// order: a closed run or the released sessions of an abandoned one, then the session flush (unless it was held).
    /// </summary>
    public List<Period> Accumulate(ExtractedInfo info, string playerUuid, Period fragment,
        string claimedTask, Dictionary<string, double> prices, DateTime now)
    {
        var run = info.NucleusRun;
        List<Period> result = run == null ? [] : Settle(info, run, playerUuid, fragment, now);
        var flush = TryAccumulate(info, playerUuid, fragment, claimedTask, prices, now);
        if (run != null)
            NoteBundle(run, fragment);
        if (flush == null)
            return result;
        if (run != null && ShouldHold(info, run, flush, now))
            result.AddRange(Hold(run, flush));
        else
            result.Add(flush);
        return result;
    }

    /// <summary>A failing session merge loses only this fragment, never the periods a run close or release already took out of the state.</summary>
    private Period? TryAccumulate(ExtractedInfo info, string playerUuid, Period fragment,
        string claimedTask, Dictionary<string, double> prices, DateTime now)
    {
        try
        {
            return sessions.Accumulate(info, playerUuid, fragment, claimedTask, prices, now);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to accumulate the session of {player} at {location}", playerUuid, fragment.Location);
            return null;
        }
    }

    /// <summary>Closes a claimed run, releases an abandoned one, or does nothing.</summary>
    private static List<Period> Settle(ExtractedInfo info, NucleusRunState run, string playerUuid, Period fragment, DateTime now)
    {
        if (ShouldClose(run, fragment, now))
            return Close(info, run, playerUuid);
        if (IsAbandoned(info, run, now))
            return ToPeriods(TakeHeld(run), playerUuid);
        return [];
    }

    /// <summary>
    /// A claimed run closes before <paramref name="fragment"/> once the bundle loot was seen, when the fragment starts
    /// <see cref="ClaimGrace"/> after the claim was processed, or <see cref="MaxClaimPending"/> after it. Compared on one
    /// clock: fragments are stamped with the processing time, so is <see cref="NucleusRunState.ClaimProcessedAt"/>.
    /// </summary>
    internal static bool ShouldClose(NucleusRunState run, Period fragment, DateTime now)
    {
        if (!run.ClaimPending)
            return false;
        return run.BundleSeen || fragment.StartTime >= run.ClaimProcessedAt + ClaimGrace
            || now - run.ClaimProcessedAt > MaxClaimPending;
    }

    /// <summary>
    /// A fragment ending after the claim with loot at the nucleus brought the bundle (picked up at the Nucleus Vault); the
    /// next fragment closes the run. Placing crystals only removes items, so the placing visit is no bundle.
    /// </summary>
    private static void NoteBundle(NucleusRunState run, Period fragment)
    {
        if (!run.ClaimPending || fragment.EndTime < run.ClaimProcessedAt)
            return;
        if (SkyblockZones.Canonical(fragment.Location) != NucleusZone)
            return;
        run.BundleSeen |= fragment.ItemsCollected?.Any(e => e.Value > 0 && !PseudoItems.IsPseudo(e.Key)) == true;
    }

    private static bool IsAbandoned(ExtractedInfo info, NucleusRunState run, DateTime now)
        => !run.ClaimPending && run.Held is { Count: > 0 } && !NucleusRunTracker.IsActive(run, info.CurrentProfileName, now);

    /// <summary>Only Crystal Hollows sessions of an active run (or of a claim not closed yet) are held.</summary>
    private static bool ShouldHold(ExtractedInfo info, NucleusRunState run, Period flush, DateTime now)
        => IsHollows(flush.Location)
            && (run.ClaimPending || NucleusRunTracker.IsActive(run, info.CurrentProfileName, now));

    internal static bool IsHollows(string? location) => location != null && TaskSessionService.IslandOfZone(location) == Hollows;

    /// <summary>
    /// Merges the run's held sessions and the open Crystal Hollows session into one Crystal Nucleus period. Held sessions
    /// from before the run's window are folded as their own tasks.
    /// </summary>
    private static List<Period> Close(ExtractedInfo info, NucleusRunState run, string playerUuid)
    {
        var runs = Math.Max(1, run.PendingClaims);
        run.ClaimPending = false;
        run.BundleSeen = false;
        run.PendingClaims = 0;
        var windowStart = (run.ClaimedRunStart != default ? run.ClaimedRunStart : run.ClaimAt) - RunWindowLead;
        var held = TakeHeld(run);
        var result = ToPeriods(held.Where(s => s.LastItemTime < windowStart), playerUuid);
        var parts = held.Where(s => s.LastItemTime >= windowStart).ToList();
        if (TakeOpenHollowsSession(info) is { } open)
            parts.Add(open);
        if (parts.Count > 0)
            result.Add(MergeRun(parts, playerUuid, runs));
        return result;
    }

    private static TaskSession? TakeOpenHollowsSession(ExtractedInfo info)
    {
        var open = info.CurrentSession;
        if (open == null || !IsHollows(open.Location))
            return null;
        info.CurrentSession = null;
        return open;
    }

    /// <summary>
    /// Holds a flushed session; returns the oldest held ones beyond the caps, to be folded as their own tasks. Only the
    /// newest held session keeps its gear snapshot (the merged run uses the last gear); the others keep its fingerprint.
    /// </summary>
    private static List<Period> Hold(NucleusRunState run, Period flush)
    {
        run.Held ??= [];
        foreach (var older in run.Held)
            older.Gear = null;
        run.Held.Add(ToSession(flush));
        var overflow = new List<Period>();
        while (run.Held.Count > MaxHeldSessions || run.Held.Sum(s => s.Items?.Count ?? 0) > MaxHeldItemEntries)
        {
            overflow.Add(TaskSessionService.BuildPeriod(run.Held[0], flush.PlayerUuid));
            run.Held.RemoveAt(0);
        }
        return overflow;
    }

    private static List<TaskSession> TakeHeld(NucleusRunState run)
    {
        var held = run.Held ?? [];
        run.Held = null;
        return held;
    }

    private static List<Period> ToPeriods(IEnumerable<TaskSession> held, string playerUuid)
        => held.Select(s => TaskSessionService.BuildPeriod(s, playerUuid)).ToList();

    private static TaskSession ToSession(Period period) => new()
    {
        DetectedTask = period.DetectedTask,
        StartTime = period.StartTime,
        LastItemTime = period.EndTime,
        Server = period.Server,
        Location = period.Location,
        Items = new(period.ItemsCollected ?? []),
        ZoneSeconds = period.ZoneSeconds == null ? new() : new(period.ZoneSeconds),
        ZoneValue = period.ZoneValue == null ? new() : new(period.ZoneValue),
        Gear = period.Gear == null ? null : new GearSnapshot(period.Gear),
        GearFingerprint = Fingerprint(period.Gear),
        GearChanged = period.GearChanged,
        StatSums = period.StatSums == null ? new() : new(period.StatSums),
        StatSamples = period.StatSamples == null ? new() : new(period.StatSamples),
        LastStatSampleAt = period.LastStatSampleAt
    };

    /// <summary>Gear fingerprint for <see cref="GearDiffers"/>, null without gear or when it cannot be computed.</summary>
    private static string? Fingerprint(GearSnapshot? gear)
    {
        if (gear == null)
            return null;
        try
        {
            return Services.Gear.GearFingerprint.Of(gear);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One Crystal Nucleus period from the run's sessions: all their items (the hunt's loot and the bundle) plus one
    /// <see cref="PseudoItems.NUCLEUS_RUN"/> per claimed run, ending with the last session. Its duration is the sum of the session times,
    /// not the wall clock span, so time spent elsewhere in between (and folded there) is not counted twice.
    /// </summary>
    internal static Period MergeRun(IReadOnlyList<TaskSession> parts, string playerUuid, int runs = 1)
    {
        var last = parts.MaxBy(p => p.LastItemTime)!;
        var seconds = parts.Sum(p => Math.Max(0, (p.LastItemTime - p.StartTime).TotalSeconds));
        var items = SumCounts(parts.Select(p => p.Items));
        ItemCountMath.Add(items, PseudoItems.NUCLEUS_RUN, runs);
        return new Period
        {
            PlayerUuid = playerUuid,
            Server = last.Server!,
            Location = last.Location!,
            StartTime = last.LastItemTime - TimeSpan.FromSeconds(seconds),
            EndTime = last.LastItemTime,
            ItemsCollected = items,
            DetectedTask = NucleusMiningTask.TaskName,
            ZoneSeconds = Sum(parts.Select(p => p.ZoneSeconds)),
            ZoneValue = Sum(parts.Select(p => p.ZoneValue)),
            Gear = last.Gear == null ? null : new GearSnapshot(last.Gear),
            GearChanged = GearDiffers(parts),
            StatSums = Sum(parts.Select(p => p.StatSums)),
            StatSamples = SumCounts(parts.Select(p => p.StatSamples)),
            LastStatSampleAt = parts.Max(p => p.LastStatSampleAt)
        };
    }

    private static Dictionary<string, int> SumCounts(IEnumerable<Dictionary<string, int>> all)
    {
        var sum = new Dictionary<string, int>();
        foreach (var (tag, count) in all.Where(d => d != null).SelectMany(d => d))
            ItemCountMath.Add(sum, tag, count);
        foreach (var zero in sum.Where(e => e.Value == 0).Select(e => e.Key).ToList())
            sum.Remove(zero);
        return sum;
    }

    private static Dictionary<string, double> Sum(IEnumerable<Dictionary<string, double>> all)
    {
        var sum = new Dictionary<string, double>();
        foreach (var (key, value) in all.Where(d => d != null).SelectMany(d => d))
            sum[key] = sum.GetValueOrDefault(key) + value;
        return sum;
    }

    /// <summary>
    /// A run is gear observation material only when one gear was worn throughout (rarely: the tool changes per zone).
    /// Compared by fingerprint: only the newest held session still has its snapshot.
    /// </summary>
    private static bool GearDiffers(IReadOnlyList<TaskSession> parts)
    {
        if (parts.Any(p => p.GearChanged || p.GearFingerprint == null))
            return true;
        return parts.Select(p => p.GearFingerprint).Distinct().Count() > 1;
    }
}
