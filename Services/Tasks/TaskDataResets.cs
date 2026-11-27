using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Per-task data cutoffs: aggregate rows and player stats from before the cutoff are ignored.
/// Fossil Excavation: the winsorizing sign flip (fixed 2026-10-25) corrupted its stored data.
/// An entry can be deleted 14 days (the row TTL) after its cutoff.
/// </summary>
public static class TaskDataResets
{
    /// <summary>
    /// Crystal Nucleus data before this is ignored: it was selected by its Divan Fragment loot, later it was only the
    /// bare nucleus zone visit; only run periods (<see cref="NucleusRunRouter"/>) count. Declared before
    /// <see cref="Cutoffs"/>: static fields initialize in text order.
    /// TODO: set at deploy time to the first full day (00:00 UTC) after the deploy that ships both the chat allowlist
    /// (SkyBackendForFrontend DefaultChatRegex with the nucleus lines) and this run attribution. The current value is a
    /// placeholder from hiding the task: left as is, zone-visit data from before that deploy would count.
    /// </summary>
    public static readonly DateTime CrystalNucleusCutoff = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Yog and Bal data before this holds a Divan's Alloy of 2026-11-10 (see <see cref="NucleusMiningTask.BundleOnlyLoot"/>).
    /// Declared before <see cref="Cutoffs"/> for the same reason.
    /// </summary>
    public static readonly DateTime YogAndBalCutoff = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, DateTime> Cutoffs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fossil Excavation", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
        // the old AFK check (rare pool in the bucket rate) dropped normal sessions and kept the lucky ones: 96% rare value
        { "Diana", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
        // every Crystal Hollows session with a Divan Fragment was labelled Crystal Nucleus (selection on outcome, 46M/h)
        { "Crystal Nucleus", CrystalNucleusCutoff },
        // a 1.7B Divan's Alloy claim (nucleus bundle loot, 2026-11-10) folded into its rare pool: 55.8M/h from 2.3 hours
        { "Yog and Bal", YogAndBalCutoff },
        // 26 bazaar bought Bobbin' Time books (1.7B), Behemoth pots booked as loot and a merged dungeon chest claim: 484M/h
        // from 3.9 hours, 2.84B of it an untraced rare pool (see TaskPeriodFolder.UndeclaredExpensive)
        // 11-29, not 11-28: pods of the old version may still write rows on the deploy day
        { "Helix Honey Lathering", new DateTime(2026, 11, 29, 0, 0, 0, DateTimeKind.Utc) }
    };

    public static bool IsBeforeReset(string task, DateTime at)
        => task != null && Cutoffs.TryGetValue(task, out var cutoff) && at < cutoff;
}
