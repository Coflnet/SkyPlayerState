using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Whether a task can be done right now. <see cref="Available"/> stays true when that could not be
/// determined (for example the mayor service is down): an uncertain task is never hidden, the note says why.
/// </summary>
/// <param name="Available">False only when the required event is known not to run</param>
/// <param name="Note">Why it is (or may not be) possible, null for a task that is plainly available</param>
/// <param name="NextAvailableAt">When it is possible again, null when it is possible now or that is unknown</param>
public record TaskAvailability(bool Available, string Note = null, DateTime? NextAvailableAt = null)
{
    public static readonly TaskAvailability Always = new(true);
}

/// <summary>The current mayor (lowercase name, as <see cref="CurrentMayorService"/> stores it) and the names and descriptions of their perks.</summary>
public record MayorSnapshot(string Name, IReadOnlyList<MayorPerk> Perks = null)
{
    /// <summary>Null when the mayor is unknown, perks unknown for a known name.</summary>
    public static MayorSnapshot FromName(string name) => name == null ? null : new MayorSnapshot(name);

    public bool HasPerk(string perkName) =>
        Perks?.Any(p => string.Equals(p.Name, perkName, StringComparison.OrdinalIgnoreCase)) ?? false;

    public bool HasPerkMentioning(string perkName, string text) =>
        Perks?.Any(p => string.Equals(p.Name, perkName, StringComparison.OrdinalIgnoreCase)
            && (p.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)) ?? false;

    /// <summary>The name with a capital first letter, for notes.</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Name : char.ToUpperInvariant(Name[0]) + Name[1..];
}

public record MayorPerk(string Name, string Description);

/// <summary>
/// A SkyBlock event or mayor perk a task can only be done during (see <see cref="MethodTask.RequiredEvent"/>).
/// </summary>
public abstract class SkyblockEvent
{
    /// <summary>Display name, e.g. "Spooky Festival".</summary>
    public abstract string Name { get; }

    /// <summary>Availability at <paramref name="utcNow"/>; <paramref name="mayor"/> is null when the mayor is unknown.</summary>
    public abstract TaskAvailability Check(DateTime utcNow, MayorSnapshot mayor);
}

/// <summary>The SkyBlock events tasks declare as <see cref="MethodTask.RequiredEvent"/>.</summary>
public static class SkyblockEvents
{
    /// <summary>
    /// Diana's Mythological Ritual perk. It is no minister perk (the election API marks it minister: false), so only a
    /// Diana term has it, or Jerry's Perkpocalypse while it rotates to Diana (not visible in the mayor data).
    /// </summary>
    public static readonly SkyblockEvent MythologicalRitual = new MythologicalRitualEvent();

    /// <summary>
    /// Spooky sea creatures bite while the Fear Mongerer is in the Hub, Autumn 26 to Late Autumn 3 (the Spooky Festival is
    /// Autumn 29-31). Foxy's Extra Event perk can schedule another one at a time the mayor data does not show.
    /// </summary>
    public static readonly CalendarEvent SpookyFestival = new("Spooky Festival",
        SkyblockCalendar.Day(SkyblockCalendar.Autumn, 26), SkyblockCalendar.Day(SkyblockCalendar.LateAutumn, 3),
        "the Spooky Festival, while the Fear Mongerer is in the Hub (Autumn 26 to Late Autumn 3)",
        extraEventKeyword: "Spooky");

    /// <summary>Jerry's Workshop opens for all of Late Winter and throughout real-world December.</summary>
    public static readonly CalendarEvent JerrysWorkshop = new("Jerry's Workshop",
        SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 1), SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 31),
        "the Season of Jerry, while Jerry's Workshop is open (all of Late Winter and real-world December)",
        realMonthOpen: 12);

    /// <summary>
    /// Marina's Fishing Festival perk (sharks), the first 3 days of each SkyBlock month. It is a minister perk too, which the
    /// mayor data does not show, so outside the mayor's perks only the calendar decides.
    /// </summary>
    public static readonly SkyblockEvent FishingFestival = new FishingFestivalEvent();
}

/// <summary>
/// The SkyBlock calendar: a year is 124 real hours of 12 months with 31 days of 20 minutes each,
/// year 1 started on 2019-06-11 17:55 UTC (same epoch as Coflnet.Sky.Core.Constants.SkyblockYear).
/// </summary>
public static class SkyblockCalendar
{
    public static readonly DateTime Epoch = new(2019, 6, 11, 17, 55, 0, DateTimeKind.Utc);
    public static readonly TimeSpan DayLength = TimeSpan.FromMinutes(20);
    public const int DaysPerMonth = 31;
    public const int DaysPerYear = 12 * DaysPerMonth;
    public static readonly TimeSpan YearLength = DayLength * DaysPerYear;

    // zero based month indices, Early Spring is 0
    public const int Months = 12;
    public const int Autumn = 7;
    public const int LateAutumn = 8;
    public const int LateWinter = 11;

    /// <summary>Zero based day of the year of a (zero based) month and its 1 based day.</summary>
    public static int Day(int month, int dayOfMonth) => month * DaysPerMonth + dayOfMonth - 1;

    /// <summary>Zero based day of the SkyBlock year at <paramref name="utc"/> (0 = Early Spring 1).</summary>
    public static int DayOfYear(DateTime utc)
    {
        var days = (long)Math.Floor((utc - Epoch) / DayLength);
        return (int)(((days % DaysPerYear) + DaysPerYear) % DaysPerYear);
    }

    /// <summary>The next real time at or after <paramref name="utc"/> on which day <paramref name="dayOfYear"/> starts.</summary>
    public static DateTime NextStartOf(int dayOfYear, DateTime utc)
    {
        var years = Math.Floor((utc - Epoch) / YearLength);
        var start = Epoch + YearLength * years + DayLength * dayOfYear;
        return start >= utc ? start : start + YearLength;
    }
}

/// <summary>An event on fixed days of every SkyBlock year (inclusive day range), optionally also open for a real month.</summary>
public class CalendarEvent : SkyblockEvent
{
    private readonly int firstDay;
    private readonly int lastDay;
    private readonly string window;
    private readonly int? realMonthOpen;
    private readonly string extraEventKeyword;

    public CalendarEvent(string name, int firstDay, int lastDay, string window,
        int? realMonthOpen = null, string extraEventKeyword = null)
    {
        Name = name;
        this.firstDay = firstDay;
        this.lastDay = lastDay;
        this.window = window;
        this.realMonthOpen = realMonthOpen;
        this.extraEventKeyword = extraEventKeyword;
    }

    public override string Name { get; }

    /// <summary>True while the event runs (ignores extra events a mayor perk schedules).</summary>
    public bool IsRunningAt(DateTime utc)
    {
        if (realMonthOpen == utc.Month)
            return true;
        var day = SkyblockCalendar.DayOfYear(utc);
        return day >= firstDay && day <= lastDay;
    }

    public override TaskAvailability Check(DateTime utcNow, MayorSnapshot mayor)
    {
        if (IsRunningAt(utcNow))
            return TaskAvailability.Always;
        if (MayorMaySchedule(mayor))
            return new TaskAvailability(true,
                $"Only possible during {window}. Foxy's Extra Event perk schedules an extra {Name} this year, check the in-game calendar.");
        // no relative time in the note: it is cached on the web, clients render NextAvailableAt themselves
        return new TaskAvailability(false, $"Only possible during {window}.", NextStart(utcNow));
    }

    private bool MayorMaySchedule(MayorSnapshot mayor)
    {
        if (extraEventKeyword == null || mayor?.Name != "foxy")
            return false;
        // without perk data a Foxy term may have the extra event, don't hide on uncertainty
        return mayor.Perks == null || mayor.HasPerkMentioning("Extra Event", extraEventKeyword);
    }

    private DateTime NextStart(DateTime utc)
    {
        var next = SkyblockCalendar.NextStartOf(firstDay, utc);
        if (realMonthOpen is not int month)
            return next;
        var realStart = new DateTime(utc.Year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (realStart < utc)
            realStart = realStart.AddYears(1);
        return realStart < next ? realStart : next;
    }
}

/// <summary>Diana's Mythological Ritual perk, see <see cref="SkyblockEvents.MythologicalRitual"/>.</summary>
public class MythologicalRitualEvent : SkyblockEvent
{
    public const string PerkName = "Mythological Ritual";

    public override string Name => PerkName;

    public override TaskAvailability Check(DateTime utcNow, MayorSnapshot mayor)
    {
        if (mayor?.Name == null)
            return new TaskAvailability(true,
                "Needs Mayor Diana's Mythological Ritual perk. The current mayor could not be checked.");
        if (mayor.Name == "diana" || mayor.HasPerk(PerkName))
            return TaskAvailability.Always;
        if (mayor.Name == "jerry")
            return new TaskAvailability(true,
                "Needs Mayor Diana's Mythological Ritual perk: only possible while Jerry's Perkpocalypse grants Diana's perks.");
        return new TaskAvailability(false,
            $"Needs Mayor Diana's Mythological Ritual perk (current mayor: {mayor.DisplayName}). Possible again once Diana wins an election.");
    }
}

/// <summary>Marina's Fishing Festival perk, see <see cref="SkyblockEvents.FishingFestival"/>.</summary>
public class FishingFestivalEvent : SkyblockEvent
{
    public const string PerkName = "Fishing Festival";
    public const int FestivalDays = 3;
    private const string Window = "the Fishing Festival, the first 3 days of each SkyBlock month while Marina's Fishing Festival perk is active (as mayor or minister)";

    public override string Name => PerkName;

    public override TaskAvailability Check(DateTime utcNow, MayorSnapshot mayor)
    {
        var perkActive = mayor != null && (mayor.HasPerk(PerkName) || mayor.Name == "marina" && mayor.Perks == null);
        if (IsFestivalDay(utcNow))
            return perkActive ? TaskAvailability.Always : new TaskAvailability(true, mayor?.Name == null
                ? $"Only possible during {Window}. The current mayor could not be checked."
                : $"Only possible during {Window}. The current mayor does not have the perk, it only runs if Marina is the minister (not checked).");
        if (mayor?.Name == null)
            return new TaskAvailability(true, $"Only possible during {Window}. The current mayor could not be checked.");
        if (FoxyMaySchedule(mayor))
            return new TaskAvailability(true,
                $"Only possible during {Window}. Foxy's Extra Event perk schedules an extra Fishing Festival this year, check the in-game calendar.");
        // without the perk on the mayor the next month only has a festival if the minister grants it
        return new TaskAvailability(false, $"Only possible during {Window}.", perkActive ? NextMonthStart(utcNow) : null);
    }

    public static bool IsFestivalDay(DateTime utc) => SkyblockCalendar.DayOfYear(utc) % SkyblockCalendar.DaysPerMonth < FestivalDays;

    private static bool FoxyMaySchedule(MayorSnapshot mayor) => mayor.Name == "foxy"
        && (mayor.Perks == null || mayor.HasPerkMentioning("Extra Event", PerkName));

    private static DateTime NextMonthStart(DateTime utc)
    {
        var month = SkyblockCalendar.DayOfYear(utc) / SkyblockCalendar.DaysPerMonth;
        return SkyblockCalendar.NextStartOf((month + 1) % SkyblockCalendar.Months * SkyblockCalendar.DaysPerMonth, utc);
    }
}
