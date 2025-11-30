using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Controller for Mayor Aura fundraising tracking endpoints
/// </summary>
[ApiController]
[Route("[controller]")]
public class MayorAuraController : ControllerBase
{
    private readonly IMayorAuraService _mayorAuraService;

    /// <summary>
    /// Creates a new instance of <see cref="MayorAuraController"/>
    /// </summary>
    public MayorAuraController(IMayorAuraService mayorAuraService)
    {
        _mayorAuraService = mayorAuraService;
    }

    /// <summary>
    /// Gets the history of total coins raised in the Mayor Aura fundraising event.
    /// Each entry contains a timestamp (rounded to minute) and the total coins raised at that time.
    /// </summary>
    /// <returns>Array of fundraising entries ordered by timestamp</returns>
    [HttpGet]
    [Route("fundraising")]
    public async Task<FundraisingEntry[]> GetFundraisingHistory()
    {
        return await _mayorAuraService.GetFundraisingHistory();
    }
}
