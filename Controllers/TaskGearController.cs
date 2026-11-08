using System;
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
/// Gear setups per task, community data only: the measured gear model (<c>/Task/gear-model/{taskName}</c>, also served
/// at the older <c>/Task/gear-clusters/{taskName}</c>) and the researched setups (baseline, budget, mid, endgame) with
/// estimated coins/hour (<c>/Task/gear-setups</c>).
/// </summary>
[ApiController]
[Route("Task/gear-setups")]
public class TaskGearController : ControllerBase
{
    internal const string TaskNotFound = "task_not_found";
    internal const string NoGearSetups = "gear_setups_not_found";

    private readonly GearSetupService gearSetups;
    private readonly GearModelService gearModels;

    public TaskGearController(GearSetupService gearSetups, GearModelService gearModels)
    {
        this.gearSetups = gearSetups;
        this.gearModels = gearModels;
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
    /// Measured gear model of one task (as in /Task/estimates): the gear score distribution over player-builds, the late
    /// game cohort (90th score percentile, widened to 2 players, never below the 75th), the rate curve over the score,
    /// the piece catalog with score contributions, the recommended "maximum achievable" endgame build, the measured
    /// modifier effects, and the researched setups with their modelled estimates as the fallback (basis "estimate").
    /// Every number comes from at least 2 distinct players (rounded to 2 significant digits below 5); never contains
    /// player data. Derived hourly. 404 with slug task_not_found for an unknown task. Before the first derivation after
    /// a restart (computedAt null) the response is not cacheable (no-cache).
    /// </summary>
    [HttpGet("/Task/gear-model/{taskName}")]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(TaskGearModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<TaskGearModel>> GetModel(string taskName, CancellationToken cancellationToken)
        => Model(taskName, cancellationToken);

    /// <summary>
    /// The route of the earlier measured setup clusters, kept for clients that still call it: it returns the same
    /// <see cref="TaskGearModel"/> as /Task/gear-model/{taskName} (the clusters are gone). Use /Task/gear-model.
    /// </summary>
    [Obsolete("Use GET /Task/gear-model/{taskName}; this route returns the same TaskGearModel.")]
    [HttpGet("/Task/gear-clusters/{taskName}")]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(TaskGearModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<TaskGearModel>> GetClusters(string taskName, CancellationToken cancellationToken)
        => Model(taskName, cancellationToken);

    private async Task<ActionResult<TaskGearModel>> Model(string taskName, CancellationToken cancellationToken)
    {
        var model = await gearModels.GetResponse(taskName, cancellationToken);
        if (model == null)
            return NotFound(new ErrorResponse { Slug = TaskNotFound, Message = $"No task named '{taskName}' is registered." });
        if (model.ComputedAt == null && Response != null)
            Response.Headers.CacheControl = "no-cache";
        return model;
    }
}
