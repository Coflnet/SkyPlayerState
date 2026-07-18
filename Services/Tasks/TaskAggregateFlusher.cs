using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Periodically flushes in-memory aggregate deltas to cassandra and refreshes
/// the merged read snapshot and live doer counts.
/// </summary>
public class TaskAggregateFlusher : BackgroundService
{
    private readonly TaskAggregateService aggregates;
    private readonly TaskActivityService activity;
    private readonly IConfiguration config;
    private readonly ILogger<TaskAggregateFlusher> logger;

    public TaskAggregateFlusher(TaskAggregateService aggregates, TaskActivityService activity,
        IConfiguration config, ILogger<TaskAggregateFlusher> logger)
    {
        this.aggregates = aggregates;
        this.activity = activity;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["TASKS:AGGREGATE"] == "false")
            return;
        // stagger startup so all pods do not hit cassandra at once
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
        var lastMerge = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await aggregates.Flush();
                // rebuild the read snapshot roughly once a minute. This is the only place the
                // merge runs; GetSnapshot() on the request/fold path just reads the last result.
                if (DateTime.UtcNow - lastMerge > TimeSpan.FromSeconds(60))
                {
                    await aggregates.RefreshSnapshot();
                    await activity.GetCounts(); // also samples the delta ring
                    lastMerge = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "task aggregate flush cycle failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { break; }
        }
        // final flush on shutdown so recent contributions are not lost
        try { await aggregates.Flush(); } catch (Exception e) { logger.LogError(e, "final aggregate flush failed"); }
    }
}
