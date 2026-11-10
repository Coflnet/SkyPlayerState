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
    /// Crystal Nucleus data before this is ignored. Set to the first full day after the zone fix is deployed. Declared
    /// before <see cref="Cutoffs"/>: static fields initialize in text order.
    /// </summary>
    public static readonly DateTime CrystalNucleusCutoff = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, DateTime> Cutoffs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fossil Excavation", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
        // the old AFK check (rare pool in the bucket rate) dropped normal sessions and kept the lucky ones: 96% rare value
        { "Diana", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) },
        // every Crystal Hollows session with a Divan Fragment was labelled Crystal Nucleus (selection on outcome, 46M/h)
        { "Crystal Nucleus", CrystalNucleusCutoff }
    };

    public static bool IsBeforeReset(string task, DateTime at)
        => task != null && Cutoffs.TryGetValue(task, out var cutoff) && at < cutoff;
}
