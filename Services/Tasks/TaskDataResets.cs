using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Per-task data cutoffs: aggregate rows and player stats from before the cutoff are ignored.
/// Fossil Excavation: the winsorizing sign flip (fixed 2026-10-25) corrupted its stored data.
/// An entry can be deleted after 2026-11-30.
/// </summary>
public static class TaskDataResets
{
    private static readonly Dictionary<string, DateTime> Cutoffs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fossil Excavation", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) }
    };

    public static bool IsBeforeReset(string task, DateTime at)
        => task != null && Cutoffs.TryGetValue(task, out var cutoff) && at < cutoff;
}
