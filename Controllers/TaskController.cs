using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Serves ranked, stat aware, saturation adjusted task estimates and live task metrics.
/// </summary>
[ApiController]
[Route("[controller]")]
public class TaskController : ControllerBase
{
    private readonly IPersistenceService persistence;
    private readonly TaskEstimator estimator;
    private readonly TaskActivityService activity;
    private readonly TaskPriceService prices;
    private readonly TaskAggregateService aggregates;
    private readonly TaskExecutionService execution;
    private readonly TaskRegistry registry;
    private readonly ILogger<TaskController> logger;

    public TaskController(IPersistenceService persistence, TaskEstimator estimator,
        TaskActivityService activity, TaskPriceService prices, TaskAggregateService aggregates,
        TaskExecutionService execution, TaskRegistry registry, ILogger<TaskController> logger)
    {
        this.persistence = persistence;
        this.estimator = estimator;
        this.activity = activity;
        this.prices = prices;
        this.aggregates = aggregates;
        this.execution = execution;
        this.registry = registry;
        this.logger = logger;
    }

    /// <summary>
    /// Ranked task estimates for one player, best coins per hour first.
    /// </summary>
    /// <param name="playerId">The player's Minecraft name - player state (<see cref="LoadState"/>)
    /// is keyed by name, not uuid. Calling this with a uuid instead degrades gracefully to
    /// community-only estimates (<see cref="TaskEstimator"/> treats an unresolved/empty state the
    /// same as no personal data), it does not throw.</param>
    /// <param name="cancellationToken"></param>
    [HttpGet("{playerId}")]
    public async Task<List<TaskEstimate>> GetEstimates(string playerId, CancellationToken cancellationToken)
    {
        var stateTask = LoadState(playerId, cancellationToken);
        var pricesTask = prices.GetPrices(cancellationToken);
        var state = await stateTask;
        var priceLookup = await pricesTask;
        var estimates = await estimator.EstimateAll(state, priceLookup, cancellationToken);
        return estimates.OrderByDescending(e => e.CoinsPerHour).ToList();
    }

    private async Task<StateObject> LoadState(string playerId, CancellationToken cancellationToken)
    {
        try
        {
            return await persistence.GetStateObject(playerId)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (TimeoutException e)
        {
            logger.LogWarning(e, "player state timed out for {player}; using community estimates", playerId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "player state unavailable for {player}; using community estimates", playerId);
        }
        return null;
    }

    /// <summary>
    /// Executes every registered task for one player and returns the full results (message,
    /// accessibility, MethodBreakdown with steps/wiki/where/drops/costs/effects, ...), sorted
    /// accessible-first then by profit/hour. This is what SkyModCommands' /cofl task and API
    /// consumers render directly - unlike <see cref="GetEstimates"/>, which only covers the
    /// stat-aware community estimate tier for MethodTask.
    /// </summary>
    /// <param name="playerId">The player's Minecraft name (not uuid) - see
    /// <see cref="TaskExecutionService.LoadStateAndHistory"/>, which resolves the uuid used for the
    /// (uuid-keyed) profit history from the loaded state instead of requiring the caller to know it.
    /// Calling this with a uuid still works (history/PlayerUuid use it directly) but returns no
    /// personal state (no skills/HOTM/purse/claimed task).</param>
    /// <param name="cancellationToken"></param>
    [HttpGet("{playerId}/results")]
    public async Task<List<TaskResult>> GetResults(string playerId, CancellationToken cancellationToken)
    {
        return await execution.ExecuteAll(playerId, cancellationToken);
    }

    /// <summary>
    /// Executes a single registered task for one player and returns its full result, including the
    /// MethodBreakdown (steps, wiki, where, island, warp, drops, costs, effects). 404s when no task
    /// is registered under <paramref name="taskName"/> (see <see cref="TaskRegistry.GetByName"/> -
    /// matches by MethodTask.MethodName or the class-derived <see cref="ProfitTask.Name"/>).
    /// </summary>
    /// <param name="playerId">The player's Minecraft name (not uuid) - see <see cref="GetResults"/>.</param>
    /// <param name="taskName"></param>
    /// <param name="cancellationToken"></param>
    [HttpGet("{playerId}/results/{taskName}")]
    public async Task<TaskResult> GetResult(string playerId, string taskName, CancellationToken cancellationToken)
    {
        var result = await execution.ExecuteOne(playerId, taskName, cancellationToken);
        if (result == null)
            throw new CoflnetException("task_not_found", $"No task named '{taskName}' is registered.");
        return result;
    }

    /// <summary>
    /// Metadata (name, description) for every registered money-making method, no player data
    /// needed. SkyModCommands forwards its own <c>/api/task/methods</c> to this.
    /// </summary>
    [HttpGet("methods")]
    [ResponseCache(Duration = 300)]
    public List<MethodMetadata> GetMethods()
    {
        return registry.Tasks.OfType<MethodTask>()
            .Select(t => new MethodMetadata { Name = t.Name, Description = t.Description })
            .ToList();
    }

    /// <summary>
    /// Live metrics per task: current doers, 20 minute change, total tracked hours.
    /// </summary>
    [HttpGet("metrics")]
    [ResponseCache(Duration = 60)]
    public async Task<List<TaskMetrics>> GetMetrics()
    {
        var counts = await activity.GetCounts();
        var deltas = await activity.GetChange20m();
        var trackedHours = GetTrackedHours(aggregates.GetSnapshot());
        return counts.Select(c => new TaskMetrics
        {
            TaskName = c.Key,
            CurrentDoers = c.Value,
            Change20m = deltas.GetValueOrDefault(c.Key),
            TotalTrackedHours = trackedHours.GetValueOrDefault(c.Key)
        }).OrderByDescending(m => m.CurrentDoers).ToList();
    }

    internal static Dictionary<string, double> GetTrackedHours(
        Dictionary<(string task, byte bucket), BucketAggregate> snapshot) =>
        snapshot.GroupBy(e => e.Key.task, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Value.WSeconds) / 3600,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The task the player is currently detected or claimed to be doing.
    /// </summary>
    [HttpGet("{playerId}/current")]
    public async Task<CurrentTask> GetCurrent(string playerId)
    {
        var state = await persistence.GetStateObject(playerId);
        var info = state?.ExtractedInfo;
        if (info == null)
            return null;
        if (info.ClaimedTask != null && DateTime.UtcNow - info.ClaimedAt < TimeSpan.FromMinutes(30))
            return new CurrentTask { TaskName = info.ClaimedTask, Since = info.ClaimedAt, Source = "claimed" };
        if (info.CurrentTask != null)
            return new CurrentTask { TaskName = info.CurrentTask, Since = info.CurrentTaskSince, Source = "auto" };
        return null;
    }

    /// <summary>
    /// Players currently doing a task (roster for the activity proxy).
    /// </summary>
    [HttpGet("{taskName}/players")]
    public async Task<List<string>> GetDoers(string taskName)
    {
        return await activity.GetDoers(taskName);
    }
}

public class TaskMetrics
{
    public string TaskName { get; set; }
    public int CurrentDoers { get; set; }
    public int Change20m { get; set; }
    public double TotalTrackedHours { get; set; }
}

public class CurrentTask
{
    public string TaskName { get; set; }
    public DateTime Since { get; set; }
    public string Source { get; set; }
}

public class MethodMetadata
{
    public string Name { get; set; }
    public string Description { get; set; }
}
