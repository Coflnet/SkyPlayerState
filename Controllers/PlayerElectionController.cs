using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Controller for Player Election voting tracking endpoints
/// </summary>
[ApiController]
[Route("[controller]")]
public class PlayerElectionController : ControllerBase
{
    private readonly IPlayerElectionService _playerElectionService;

    /// <summary>
    /// Creates a new instance of <see cref="PlayerElectionController"/>
    /// </summary>
    public PlayerElectionController(IPlayerElectionService playerElectionService)
    {
        _playerElectionService = playerElectionService;
    }

    /// <summary>
    /// Gets the history of player election votes.
    /// Each entry contains a timestamp (rounded to minute) and a dictionary of player names to vote counts.
    /// </summary>
    /// <returns>Array of player election entries ordered by timestamp</returns>
    [HttpGet]
    [Route("voting-history")]
    public async Task<PlayerElectionEntry[]> GetVotingHistory()
    {
        return await _playerElectionService.GetVotingHistory();
    }
}
