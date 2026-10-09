using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Read-only access to the menu samples taken by <see cref="MenuSamplerListener"/>. Samples never contain the
/// player they came from. Chest names contain "/" ("(#/#) Loadouts"), so the name is a query parameter.
/// </summary>
[ApiController]
[Route("[controller]")]
public class MenuSampleController : ControllerBase
{
    /// <summary>Names scanned per redis round trip while searching.</summary>
    private const int SearchBatch = 100;
    public const int MaxPageSize = 200;
    public const int MaxSearchResults = 100;

    private readonly IMenuSampleStore store;

    public MenuSampleController(IMenuSampleStore store)
    {
        this.store = store;
    }

    /// <summary>
    /// Known chest names with sample count and last sample time, most recently sampled first.
    /// </summary>
    /// <param name="name">Optional case-insensitive substring of the (normalized) chest name</param>
    /// <param name="page">Zero based page</param>
    /// <param name="pageSize">1 to 200, default 50</param>
    [HttpGet]
    public async Task<MenuNamePage> GetNames(string? name = null, int page = 0, int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(0, page);
        var all = await store.ListNames();
        var filtered = string.IsNullOrWhiteSpace(name)
            ? all
            : all.Where(n => n.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return new MenuNamePage
        {
            Total = filtered.Count,
            Page = page,
            PageSize = pageSize,
            DroppedNames = await store.GetDroppedNameCount(),
            Items = filtered.Skip(page * pageSize).Take(pageSize).ToList()
        };
    }

    /// <summary>
    /// The samples (one per player, at most 3, newest first) of one chest name. The name is normalized like
    /// a title is on sampling, so a raw title such as "(2/3) Loadouts" works too; a stored name is tried as given first.
    /// </summary>
    [HttpGet]
    [Route("samples")]
    public async Task<ActionResult<List<MenuSample>>> GetSamples(string name)
    {
        // stored names are already normalized ("(2/#) Catacombs (M7) RNG Meter"): try the name as given first, a raw title second
        var exact = (name ?? "").Trim();
        var normalized = MenuTitleNormalizer.Normalize(name);
        var samples = exact.Length == 0 ? [] : await store.Get(exact);
        if (samples.Count == 0 && normalized.Length > 0 && normalized != exact)
            samples = await store.Get(normalized);
        if (samples.Count == 0)
            return NotFound($"no samples for '{normalized}', list the known names at GET /MenuSample");
        return samples;
    }

    /// <summary>
    /// Case-insensitive text search over chest name, item name, item tag and description (colour codes stripped).
    /// Bounded: scans the stored names newest first in batches and stops at <paramref name="limit"/> chest names
    /// (max 100), at most 10 slot matches per name. Worst case (no early stop) it reads every stored name once
    /// (at most the configured name cap, 3000 by default, values of a few KB to ~100 KB each).
    /// </summary>
    [HttpGet]
    [Route("search")]
    public async Task<ActionResult<MenuSearchResponse>> Search(string q, int limit = 20)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 100)
            return BadRequest("q must be 2 to 100 characters");
        limit = Math.Clamp(limit, 1, MaxSearchResults);
        var names = (await store.ListNames()).Select(n => n.Name).ToList();
        var response = new MenuSearchResponse { Query = q };
        foreach (var batch in names.Chunk(SearchBatch))
        {
            var samples = await store.GetMany(batch);
            foreach (var name in batch)
            {
                response.ScannedNames++;
                if (!samples.TryGetValue(name, out var list))
                    continue;
                var hit = MenuSampleSearch.Match(name, list, q);
                if (hit == null)
                    continue;
                response.Results.Add(hit);
                if (response.Results.Count >= limit)
                {
                    response.Truncated = response.ScannedNames < names.Count;
                    return response;
                }
            }
        }
        return response;
    }
}

public class MenuNamePage
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    /// <summary>Sampling attempts for new names that were dropped because the name cap was reached (all pods, since the counter was created).</summary>
    public long DroppedNames { get; set; }
    public List<MenuNameInfo> Items { get; set; } = [];
}

public class MenuSearchResponse
{
    public string Query { get; set; } = "";
    public int ScannedNames { get; set; }
    /// <summary>True when the result limit stopped the scan before all names were read.</summary>
    public bool Truncated { get; set; }
    public List<MenuSearchResult> Results { get; set; } = [];
}
