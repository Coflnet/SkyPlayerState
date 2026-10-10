using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>Read-only run-length statistics recorded by <see cref="IRunLengthRecorder"/>.</summary>
[ApiController]
[Route("[controller]")]
public class RunLengthController : ControllerBase
{
    private readonly IRunLengthRecorder recorder;

    public RunLengthController(IRunLengthRecorder recorder)
    {
        this.recorder = recorder;
    }

    /// <summary>Count, median, p25, p75 and window of every recorded key (e.g. <c>dungeon:M7</c>), in seconds.</summary>
    [HttpGet]
    public Task<List<RunLengthStats>> GetAll() => recorder.GetAll();

    /// <summary>Statistics of one key; 404 when nothing was recorded for it.</summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<RunLengthStats>> Get(string key)
    {
        var stats = await recorder.GetStats(key);
        if (stats == null)
            return NotFound($"no run lengths recorded for '{key}'");
        return stats;
    }

    /// <summary>The raw entries of one key (seconds and UTC time), newest first; 404 when nothing was recorded for it.</summary>
    [HttpGet("{key}/entries")]
    public async Task<ActionResult<List<RunLengthEntry>>> GetEntries(string key)
    {
        var entries = await recorder.GetEntries(key);
        if (entries == null || entries.Count == 0)
            return NotFound($"no run lengths recorded for '{key}'");
        return entries;
    }
}
