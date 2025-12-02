using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Service interface for mythological ritual item tracking
/// </summary>
public interface IMythologicalRitualService
{
    /// <summary>
    /// Stores an item tag that mentions Mythological Ritual if not already known
    /// </summary>
    Task<bool> TryStoreTag(string itemTag, string description, string reporter);
    
    /// <summary>
    /// Gets all known mythological ritual item tags
    /// </summary>
    Task<MythologicalRitualTagEntry[]> GetTags();
    
    /// <summary>
    /// Checks if a tag is already known (in-memory check)
    /// </summary>
    bool IsKnownTag(string itemTag);
}

/// <summary>
/// Cassandra implementation of mythological ritual item tracking
/// </summary>
public class MythologicalRitualService : IMythologicalRitualService
{
    private readonly Table<MythologicalRitualDbEntry> _table;
    private readonly ConcurrentDictionary<string, byte> _knownTags = new();

    public MythologicalRitualService(ISession session)
    {
        var mapping = new MappingConfiguration().Define(
            new Map<MythologicalRitualDbEntry>()
                .TableName("mythological_ritual_items")
                .PartitionKey(x => x.ItemTag)
                .Column(x => x.ItemTag, cm => cm.WithName("item_tag"))
                .Column(x => x.ReportedAt, cm => cm.WithName("reported_at"))
                .Column(x => x.Reporter, cm => cm.WithName("reporter"))
                .Column(x => x.Description, cm => cm.WithName("description"))
        );
        _table = new Table<MythologicalRitualDbEntry>(session, mapping);
        _table.CreateIfNotExists();
    }

    public bool IsKnownTag(string itemTag)
    {
        return _knownTags.ContainsKey(itemTag);
    }

    public async Task<bool> TryStoreTag(string itemTag, string description, string reporter)
    {
        // Check in-memory first to avoid unnecessary DB writes
        if (!_knownTags.TryAdd(itemTag, 0))
        {
            return false; // Already known
        }

        var entry = new MythologicalRitualDbEntry
        {
            ItemTag = itemTag,
            ReportedAt = DateTime.UtcNow,
            Reporter = reporter,
            Description = description
        };
        
        await _table.Insert(entry).ExecuteAsync();
        return true;
    }

    public async Task<MythologicalRitualTagEntry[]> GetTags()
    {
        var entries = await _table.ExecuteAsync();
        
        return entries
            .Select(e => new MythologicalRitualTagEntry
            {
                ItemTag = e.ItemTag,
                ReportedAt = e.ReportedAt,
                Reporter = e.Reporter,
                Description = e.Description
            })
            .OrderBy(e => e.ItemTag)
            .ToArray();
    }
}

/// <summary>
/// Database entity for mythological ritual items
/// </summary>
public class MythologicalRitualDbEntry
{
    public string ItemTag { get; set; } = string.Empty;
    public DateTime ReportedAt { get; set; }
    public string Reporter { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// API response model for mythological ritual tags
/// </summary>
public class MythologicalRitualTagEntry
{
    public string ItemTag { get; set; } = string.Empty;
    public DateTime ReportedAt { get; set; }
    public string Reporter { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
