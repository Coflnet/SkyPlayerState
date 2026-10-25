using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Bazaar.History;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Cross-player bazaar order and flip history for research calibration. Internal only: this service has no ingress
/// and SkyApi does not proxy these routes. Rows hold a salted account hash, never uuids or names.
/// </summary>
[ApiController]
[Route("api/bazaar/history")]
public class BazaarHistoryController : ControllerBase
{
    private readonly BazaarHistoryReader reader;

    public BazaarHistoryController(BazaarHistoryReader reader)
    {
        this.reader = reader;
    }

    /// <summary>Orders of one item created in a time span of at most 7 days (UTC day partitions).</summary>
    /// <param name="itemTag">Item tag, required</param>
    /// <param name="from">Start (inclusive), defaults to to minus one day</param>
    /// <param name="to">End (inclusive), defaults to now</param>
    /// <param name="pageSize">1 to 5000, default 1000</param>
    /// <param name="pagingState">Cursor returned by the previous page</param>
    [HttpGet("orders")]
    public async Task<ActionResult<HistoryResult<BazaarOrderHistoryDto>>> GetOrders(string? itemTag, DateTime? from = null, DateTime? to = null, int? pageSize = null, string? pagingState = null)
    {
        try
        {
            return await reader.GetOrders(itemTag, from, to, pageSize, pagingState);
        }
        catch (HistoryRequestException e)
        {
            return StatusCode(e.Status, e.Message);
        }
    }

    /// <summary>Completed flips of one item sold in a time span of at most 31 days (UTC month partitions), newest first.</summary>
    [HttpGet("flips")]
    public async Task<ActionResult<HistoryResult<BazaarFlipHistoryDto>>> GetFlips(string? itemTag, DateTime? from = null, DateTime? to = null, int? pageSize = null, string? pagingState = null)
    {
        try
        {
            return await reader.GetFlips(itemTag, from, to, pageSize, pagingState);
        }
        catch (HistoryRequestException e)
        {
            return StatusCode(e.Status, e.Message);
        }
    }
}
