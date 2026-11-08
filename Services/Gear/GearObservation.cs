using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// One folded task session with the gear it was done with (row of <c>task_gear_observations</c>). The gear itself is in
/// <c>gear_fingerprints</c> under <see cref="GearFingerprint"/>.
/// </summary>
public class GearObservation
{
    public string TaskName { get; set; } = string.Empty;
    /// <summary>Monday (UTC) of the week of <see cref="FoldedAt"/>, the second half of the partition key.</summary>
    public DateTime WeekStart { get; set; }
    public DateTime FoldedAt { get; set; }
    /// <summary>Time uuid of <see cref="FoldedAt"/>, the clustering key (unique per row).</summary>
    public Guid ObservationId { get; set; }
    /// <summary>The player uuid in "N" format (like task_player_stats), see <see cref="GearPlayerKey"/>; never exposed by an endpoint.</summary>
    public string PlayerKey { get; set; } = string.Empty;
    /// <summary>Gear category the pieces were filtered with (farming, mining-gem, ..., other).</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>Last zone of the session (mining stats are zone dependent).</summary>
    public string? Location { get; set; }
    /// <summary>Folded seconds (unweighted).</summary>
    public double Seconds { get; set; }
    /// <summary>Confidence ramp weight of the fold (0.5 .. 1 here, lower is not observed).</summary>
    public double RampWeight { get; set; }
    public int StatBucket { get; set; }
    /// <summary>Common item value after winsorizing, not ramp weighted.</summary>
    public double CommonCoins { get; set; }
    /// <summary>Value of the rare drops split off into the EV pool, not ramp weighted.</summary>
    public double RareCoins { get; set; }
    /// <summary>Total item value of the period (common + rare, net of costs).</summary>
    public double ItemValue { get; set; }
    public string GearFingerprint { get; set; } = string.Empty;
    public string? ToolEvidence { get; set; }
    /// <summary>The session's stats: base stats plus the gear's lore stats, or tab sample means, see <see cref="StatsSource"/>.</summary>
    public Dictionary<string, double> Stats { get; set; } = new();
    /// <summary>base+gear, tab or none.</summary>
    public string StatsSource { get; set; } = GearObservationBuilder.StatsNone;
    /// <summary>Seconds between the stat source (the base's menu read, the last tab sample) and the session end, null without stats.</summary>
    public int? StatsAgeSeconds { get; set; }
    /// <summary>armor_stale, equipment_stale, pet_stale, tool_stale, tool_ambiguous, bait_stale, stats_without_tool (stats the held item affected are left out).</summary>
    public HashSet<string> Flags { get; set; } = new();
}

/// <summary>An observation plus the stored form of its gear.</summary>
public record GearObservationRecord(GearObservation Observation, ObservedGear Gear, string GearJson);

/// <summary>What the folder knows about a Folded outcome, see <see cref="Tasks.TaskPeriodFolder"/>.</summary>
public record GearFoldInput(Period Period, string Task, int StatBucket, double Seconds, double RampWeight,
    double CommonCoins, double RareCoins, double ItemValue, ExtractedInfo? Info);

/// <summary>
/// Turns a folded session into an observation (pure). Skips sessions that cannot say anything about a setup: ramp weight
/// below 0.5, under 10 minutes, gear changed mid session, no gear known. Parts of the gear seen too long before the
/// session are dropped and flagged: armor and tool older than 30 min before the start, equipment older than 8 days,
/// the pet older than 6 h (a matching tab Pet widget refreshes it), bait older than 30 min.
/// </summary>
public static class GearObservationBuilder
{
    /// <summary><see cref="ExtractedInfo.BaseStats"/> (same profile) plus the full session gear's lore stats.</summary>
    public const string StatsBaseGear = "base+gear";
    /// <summary>Means of the session's tab Stats widget samples (secondary source).</summary>
    public const string StatsTab = "tab";
    public const string StatsNone = "none";

    public const double MinRampWeight = 0.5;
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ArmorMaxAge = TimeSpan.FromMinutes(30);
    /// <summary>Equipment changes rarely and "Stats &amp; Equipment" is refreshed about weekly (/cofl tasks runs /stats after 7 days).</summary>
    public static readonly TimeSpan EquipmentMaxAge = TimeSpan.FromDays(8);
    public static readonly TimeSpan PetMaxAge = TimeSpan.FromHours(6);

    /// <summary>Null with the reason when the session is not observed.</summary>
    public static (GearObservationRecord? Record, string? SkipReason) Build(GearFoldInput input, string playerKey, DateTime foldedAt)
    {
        var skip = SkipReason(input);
        if (skip != null)
            return (null, skip);
        var category = GearCategories.ForTask(input.Task) ?? GearModifierTable.Other;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var gear = Narrow(input.Period.Gear!, category, input.Task, input.Period, flags);
        if (gear.Slots.Count == 0 && gear.Pet == null)
            return (null, "no_fresh_gear");
        var observed = GearFingerprint.Observed(gear);
        var json = GearFingerprint.ToJson(observed);
        var observation = NewObservation(input, playerKey, foldedAt, category, gear, flags);
        observation.GearFingerprint = GearFingerprint.Of(gear);
        AddStats(observation, input, category);
        return (new GearObservationRecord(observation, observed, json), null);
    }

    private static string? SkipReason(GearFoldInput input)
    {
        var period = input.Period;
        if (input.RampWeight < MinRampWeight)
            return "low_weight";
        if (input.Seconds < MinDuration.TotalSeconds)
            return "too_short";
        if (period.GearChanged)
            return "gear_changed";
        return period.Gear == null ? "no_gear" : null;
    }

    private static GearObservation NewObservation(GearFoldInput input, string playerKey, DateTime foldedAt,
        string category, GearSnapshot gear, HashSet<string> flags) => new()
    {
        TaskName = input.Task,
        WeekStart = WeekStart(foldedAt),
        FoldedAt = foldedAt,
        ObservationId = global::Cassandra.TimeUuid.NewId(new DateTimeOffset(DateTime.SpecifyKind(foldedAt, DateTimeKind.Utc))).ToGuid(),
        PlayerKey = playerKey,
        Category = category,
        Location = input.Period.Location,
        Seconds = input.Seconds,
        RampWeight = input.RampWeight,
        StatBucket = input.StatBucket,
        CommonCoins = input.CommonCoins,
        RareCoins = input.RareCoins,
        ItemValue = input.ItemValue,
        ToolEvidence = gear.ToolEvidence,
        Flags = flags
    };

    /// <summary>Monday 00:00 UTC of the week containing <paramref name="time"/>.</summary>
    public static DateTime WeekStart(DateTime time)
    {
        var day = DateTime.SpecifyKind(time.Date, DateTimeKind.Utc);
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-sinceMonday);
    }

    /// <summary>The session gear filtered to the task's category, without parts seen too long before the session.</summary>
    internal static GearSnapshot Narrow(GearSnapshot source, string category, string task, Period period, HashSet<string> flags)
    {
        var gear = new GearSnapshot(source);
        gear.Slots = gear.Slots.ToDictionary(s => s.Key, s => GearNormalizer.Filter(s.Value, category, task));
        gear.Pet = gear.Pet == null ? null : GearNormalizer.FilterPet(gear.Pet, category, task);
        DropIfOlder(gear, GearSnapshot.ArmorSlots, gear.ArmorAt, period.StartTime - ArmorMaxAge, "armor_stale", flags);
        DropIfOlder(gear, GearSnapshot.EquipmentSlots, gear.EquipmentAt, period.EndTime - EquipmentMaxAge, "equipment_stale", flags);
        DropIfOlder(gear, [GearSnapshot.Tool], gear.ToolAt, period.StartTime - ArmorMaxAge, "tool_stale", flags);
        if (!GearToolEvidence.IsKnown(gear.ToolEvidence))
        {
            gear.Slots.Remove(GearSnapshot.Tool);
            flags.Add("tool_ambiguous");
        }
        NarrowPet(gear, period, flags);
        NarrowBait(gear, category, period, flags);
        return gear;
    }

    private static void DropIfOlder(GearSnapshot gear, string[] slots, DateTime seenAt, DateTime oldest, string flag, HashSet<string> flags)
    {
        if (seenAt >= oldest || !slots.Any(gear.Slots.ContainsKey))
            return;
        foreach (var slot in slots)
            gear.Slots.Remove(slot);
        flags.Add(flag);
    }

    private static void NarrowPet(GearSnapshot gear, Period period, HashSet<string> flags)
    {
        if (gear.Pet == null || gear.PetAt >= period.EndTime - PetMaxAge)
            return;
        gear.Pet = null;
        flags.Add("pet_stale");
    }

    private static void NarrowBait(GearSnapshot gear, string category, Period period, HashSet<string> flags)
    {
        if (!GearModifierTable.For(category).Bait)
        {
            gear.Bait = new();
            return;
        }
        if (gear.Bait.Count > 0 && gear.BaitAt < period.StartTime - ArmorMaxAge)
        {
            gear.Bait = new();
            flags.Add("bait_stale");
        }
    }

    /// <summary>
    /// The base stats of the last "Stats &amp; Equipment" read plus the observed gear's lore stats ("base+gear", see
    /// <see cref="GearBaseStats"/>), else the means of the session's tab samples ("tab"), else none.
    /// </summary>
    private static void AddStats(GearObservation observation, GearFoldInput input, string category)
    {
        var period = input.Period;
        if (UsableBase(input.Info) is { } reading)
        {
            // the full session gear, not the narrowed one: the base had all shown parts subtracted, staleness only shapes the key
            var effective = GearBaseStats.Effective(reading, period.Gear!, category, input.Task, out var withoutTool);
            if (withoutTool.Count > 0)
                observation.Flags.Add("stats_without_tool");
            SetStats(observation, effective, StatsBaseGear, period.EndTime - reading.At);
            return;
        }
        var means = MeanStats(period);
        if (means.Count > 0)
            SetStats(observation, means, StatsTab, period.EndTime - period.LastStatSampleAt);
    }

    /// <summary>The base stats when they were read on the player's current profile.</summary>
    private static BaseStatsReading? UsableBase(ExtractedInfo? info)
    {
        var reading = info?.BaseStats;
        if (reading == null || reading.Stats.Count == 0 || reading.ProfileId != info!.CurrentProfileId)
            return null;
        return reading;
    }

    private static Dictionary<string, double> MeanStats(Period period)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, sum) in period.StatSums ?? new())
            if (period.StatSamples?.GetValueOrDefault(name) is > 0 and var count)
                result[name] = sum / count;
        return result;
    }

    private static void SetStats(GearObservation observation, Dictionary<string, double> stats, string source, TimeSpan age)
    {
        if (stats.Count == 0)
            return;
        observation.Stats = stats;
        observation.StatsSource = source;
        observation.StatsAgeSeconds = (int)Math.Max(0, Math.Round(age.TotalSeconds));
    }
}
