using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services.Gear;
using Coflnet.Sky.PlayerState.Tasks.Gear;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Gear setups per task, community data only: measured setups (<c>/Task/gear-clusters/{taskName}</c>) and the researched
/// setups (baseline, budget, mid, endgame) with estimated coins/hour (<c>/Task/gear-setups</c>).
/// </summary>
[ApiController]
[Route("Task/gear-setups")]
public class TaskGearController : ControllerBase
{
    internal const string TaskNotFound = "task_not_found";
    internal const string NoGearSetups = "gear_setups_not_found";

    private readonly GearSetupService gearSetups;
    private readonly GearClusterService gearClusters;

    public TaskGearController(GearSetupService gearSetups, GearClusterService gearClusters)
    {
        this.gearSetups = gearSetups;
        this.gearClusters = gearClusters;
    }

    /// <summary>
    /// Gear setups of every task that has a researched profile, best community coins/hour first.
    /// Empty when no profile is shipped. Recomputed with the community estimates (about once a minute).
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 120)]
    [ProducesResponseType(typeof(List<TaskGearSetups>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TaskGearSetups>>> GetAll(CancellationToken cancellationToken)
    {
        return await gearSetups.GetAll(cancellationToken);
    }

    /// <summary>
    /// Gear setups of one task by its method name (as in /Task/estimates). 404 with slug task_not_found for an
    /// unknown task, gear_setups_not_found for a task without a researched profile.
    /// </summary>
    [HttpGet("{taskName}")]
    [ResponseCache(Duration = 120)]
    [ProducesResponseType(typeof(TaskGearSetups), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskGearSetups>> Get(string taskName, CancellationToken cancellationToken)
    {
        if (!gearSetups.IsKnownTask(taskName))
            return NotFound(new ErrorResponse { Slug = TaskNotFound, Message = $"No task named '{taskName}' is registered." });
        var setups = await gearSetups.Get(taskName, cancellationToken);
        if (setups == null)
            return NotFound(new ErrorResponse { Slug = NoGearSetups, Message = $"No gear setups are researched for '{taskName}' yet." });
        return setups;
    }

    /// <summary>
    /// Measured gear setups of one task (as in /Task/estimates): up to 12 clusters of observed sessions, each from at
    /// least 2 distinct players and 1 hour (rounded to 2 significant digits with exactly 2 players), the measured
    /// effects of modifier changes, and the researched setups with their modelled estimates as the fallback (basis
    /// "estimate"). Derived hourly; never contains player data. 404 with slug task_not_found for an unknown task.
    /// Before the first derivation after a restart (computedAt null) the response is not cacheable (no-cache).
    /// </summary>
    [HttpGet("/Task/gear-clusters/{taskName}")]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(TaskGearClusters), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskGearClusters>> GetClusters(string taskName, CancellationToken cancellationToken)
    {
        var clusters = await gearClusters.GetResponse(taskName, cancellationToken);
        if (clusters == null)
            return NotFound(new ErrorResponse { Slug = TaskNotFound, Message = $"No task named '{taskName}' is registered." });
        if (clusters.ComputedAt == null && Response != null)
            Response.Headers.CacheControl = "no-cache";
        return clusters;
    }
}
