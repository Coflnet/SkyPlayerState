using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// In-memory implementation of Mayor Aura fundraising data storage
/// </summary>
public class MayorAuraService : IMayorAuraService
{
    private readonly ConcurrentDictionary<DateTime, FundraisingEntry> _entries = new();

    public Task StoreFundraising(FundraisingEntry entry)
    {
        // Store by timestamp (minute precision), updating if already exists
        _entries[entry.Timestamp] = entry;
        return Task.CompletedTask;
    }

    public Task<FundraisingEntry[]> GetFundraisingHistory()
    {
        return Task.FromResult(_entries.Values
            .OrderBy(e => e.Timestamp)
            .ToArray());
    }
}
