using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Controller for mythological ritual item data
/// </summary>
[ApiController]
[Route("[controller]")]
public class MythologicalRitualController : ControllerBase
{
    private readonly IMythologicalRitualService _service;

    public MythologicalRitualController(IMythologicalRitualService service)
    {
        _service = service;
    }

    /// <summary>
    /// Gets all known item tags that mention Mythological Ritual
    /// </summary>
    /// <returns>List of item tags with their metadata</returns>
    [HttpGet("tags")]
    public async Task<MythologicalRitualTagEntry[]> GetTags()
    {
        return await _service.GetTags();
    }
}
