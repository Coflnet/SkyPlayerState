using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly ILogger<TaskController> logger;

    public TaskController(IPersistenceService persistence, TaskEstimator estimator,
        TaskActivityService activity, TaskPriceService prices, ILogger<TaskController> logger)
    {
        this.persistence = persistence;
        this.estimator = estimator;
        this.activity = activity;
        this.prices = prices;
        this.logger = logger;
    }

    /// <summary>
    /// Ranked task estimates for one player, best coins per hour first.
    /// </summary>
    [HttpGet("{playerId}")]
    public async Task<List<TaskEstimate>> GetEstimates(string playerId)
    {
        var state = await persistence.GetStateObject(playerId);
        var priceLookup = await prices.GetPrices();
        var estimates = await estimator.EstimateAll(state, priceLookup);
        return estimates.OrderByDescending(e => e.CoinsPerHour).ToList();
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
        return counts.Select(c => new TaskMetrics
        {
            TaskName = c.Key,
            CurrentDoers = c.Value,
            Change20m = deltas.GetValueOrDefault(c.Key)
        }).OrderByDescending(m => m.CurrentDoers).ToList();
    }

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
