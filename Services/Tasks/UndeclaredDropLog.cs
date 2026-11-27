using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Logs the expensive gains <see cref="TaskPeriodFolder"/> kept out of a task because the task does not declare them as a
/// drop, so missing legit drops can be found and declared. One Information line per (task, item) and
/// <see cref="Interval"/>: the first sighting logs at once, later ones are summed into the next line.
/// </summary>
public class UndeclaredDropLog
{
    /// <summary>At most one line per task and item in this time.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly Prometheus.Counter ExcludedValue = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_undeclared_drop_value_total",
        "Coin value of expensive gains excluded from a task's fold because the task does not declare them (they go to its undeclared pool); promoted: enough players found the tag for it to count as a rare drop",
        ["task", "promoted"]);

    private readonly ILogger logger;
    private readonly Func<DateTime> now;
    private readonly ConcurrentDictionary<(string Task, string Tag), Pending> pending = new();

    private class Pending
    {
        public DateTime LoggedAt = DateTime.MinValue;
        public int Periods;
        public long Count;
        public double Value;
    }

    private void LogLine(string task, string tag, int count, double value, string playerUuid, bool promoted, Pending entry)
    {
        var what = promoted ? "Counted promoted undeclared drop for" : "Excluded undeclared expensive drop from";
        logger.LogInformation(
            "{what} {task}: {count}x {tag} worth {value:N0} coins (player {player}); {periods} periods, {totalCount} items, {totalValue:N0} coins since the last line",
            what, task, count, tag, value, playerUuid, entry.Periods, entry.Count, entry.Value);
    }

    public UndeclaredDropLog(ILogger logger, Func<DateTime> now = null)
    {
        this.logger = logger;
        this.now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Records one excluded gain, logging it (with what was summed since the last line) when the interval allows.
    /// <paramref name="promoted"/>: the tag has the independent evidence of <see cref="UndeclaredPool.PromotionPlayers"/> players.
    /// </summary>
    public void Record(string task, string tag, int count, double value, string playerUuid, bool promoted = false)
    {
        if (double.IsFinite(value) && value > 0)
            ExcludedValue.WithLabels(task, promoted ? "true" : "false").Inc(value);
        var entry = pending.GetOrAdd((task, tag), _ => new Pending());
        lock (entry)
        {
            entry.Periods++;
            entry.Count += count;
            entry.Value += value;
            var at = now();
            if (at - entry.LoggedAt < Interval)
                return;
            LogLine(task, tag, count, value, playerUuid, promoted, entry);
            entry.LoggedAt = at;
            entry.Periods = 0;
            entry.Count = 0;
            entry.Value = 0;
        }
    }
}
