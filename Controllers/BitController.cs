using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Controller for bit tag mapping endpoints
/// </summary>
[ApiController]
[Route("[controller]")]
public class BitController : ControllerBase
{
    private readonly IBitService _bitService;

    /// <summary>
    /// Creates a new instance of <see cref="BitController"/>
    /// </summary>
    public BitController(IBitService bitService)
    {
        _bitService = bitService;
    }

    /// <summary>
    /// Gets the tag to bit value mappings for a specific shop type
    /// </summary>
    /// <param name="shopType">The shop type to filter by (e.g., "COMMUNITY_SHOP" or "BITS_SHOP")</param>
    /// <returns>Array of tag to bit mappings for the specified shop type</returns>
    [HttpGet]
    [Route("mappings/{shopType}")]
    public async Task<BitTagMapping[]> GetTagToBitMappings(string shopType)
    {
        return await _bitService.GetTagToBitMappings(shopType);
    }

    /// <summary>
    /// Gets all tag to bit value mappings across all shop types
    /// </summary>
    /// <returns>Array of all tag to bit mappings</returns>
    [HttpGet]
    [Route("mappings")]
    public async Task<BitTagMapping[]> GetAllTagToBitMappings()
    {
        return await _bitService.GetAllTagToBitMappings();
    }

    /// <summary>
    /// Gets the current coins-per-copper value (Garden currency) and the SkyMart items it was derived from.
    /// Falls back to a constant when too few items qualify (see <c>IsFallback</c>).
    /// </summary>
    [HttpGet]
    [Route("copper")]
    public Tasks.CopperValueInfo GetCopperValue()
    {
        return Tasks.CopperValue.Info;
    }
}
