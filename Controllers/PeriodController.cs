using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.PlayerState.Controllers;

/// <summary>
/// Read-only access to recorded profit periods, so nobody needs to query the database by hand.
/// Reads go through <see cref="ReadOnlyPeriodQueries"/>/<see cref="ReadOnlyCqlRunner"/> only: fixed SELECTs over a
/// single player partition, no cross-player scan exists.
/// </summary>
[ApiController]
[Route("[controller]")]
public class PeriodController : ControllerBase
{
    private readonly IPersistenceService persistence;
    private readonly ReadOnlyCqlRunner reader;

    public PeriodController(IPersistenceService persistence, ReadOnlyCqlRunner reader)
    {
        this.persistence = persistence;
        this.reader = reader;
    }

    /// <summary>
    /// A player's recent stored periods (7 day retention), newest first.
    /// </summary>
    /// <param name="playerId">Minecraft name (like the /task routes) or uuid</param>
    /// <param name="limit">Rows read from the player's partition, 1 to 500, default 100. Filters apply to these rows only.</param>
    /// <param name="before">Only periods ended before this time (to page further back)</param>
    /// <param name="location">Case-insensitive substring of the location</param>
    /// <param name="task">Case-insensitive substring of the detected task</param>
    /// <param name="merge">Merge consecutive periods of the same server and location into stays (gap up to 2 minutes), oldest first</param>
    [HttpGet("{playerId}")]
    public async Task<PeriodResponse> GetPeriods(string playerId, int limit = 100, DateTime? before = null,
        string? location = null, string? task = null, bool merge = false)
    {
        var uuid = await ResolveUuid(playerId);
        var periods = await reader.ReadPeriods(ReadOnlyPeriodQueries.HistoryForPlayer(uuid, before, limit));
        var scanned = periods.Count;
        if (!string.IsNullOrWhiteSpace(location))
            periods = periods.Where(p => p.Location?.Contains(location.Trim(), StringComparison.OrdinalIgnoreCase) ?? false).ToList();
        var stays = merge ? PeriodStays.Merge(periods) : PeriodStays.Single(periods);
        if (!string.IsNullOrWhiteSpace(task))
            stays = stays.Where(s => s.Tasks.Any(t => t.Contains(task.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        return new PeriodResponse
        {
            Scanned = scanned,
            Merged = merge,
            Summary = PeriodStays.Summarize(stays),
            Items = stays
        };
    }

    private async Task<string> ResolveUuid(string playerId)
    {
        var plain = (playerId ?? "").Replace("-", "").ToLowerInvariant();
        if (plain.Length == 32 && plain.All(Uri.IsHexDigit))
            return plain;
        var state = await persistence.GetStateObject(playerId);
        if (state?.McInfo?.Uuid is { } uuid && uuid != Guid.Empty)
            return uuid.ToString("N");
        throw new CoflnetException("player_not_found", $"No player state for '{playerId}', pass the uuid instead.");
    }
}

public class PeriodResponse
{
    /// <summary>Rows read from the player's partition before filtering.</summary>
    public int Scanned { get; set; }
    public bool Merged { get; set; }
    public DurationSummary Summary { get; set; } = new();
    public List<PeriodStay> Items { get; set; } = [];
}
