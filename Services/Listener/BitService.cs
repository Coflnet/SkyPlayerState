using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Cassandra implementation of bit tag mapping storage
/// </summary>
public class BitService : IBitService
{
    private readonly Table<BitTagMappingDbEntry> _bitTagMappingTable;

    public BitService(ISession session)
    {
        var mapping = new MappingConfiguration().Define(
            new Map<BitTagMappingDbEntry>()
                .TableName("bit_tag_mappings")
                .PartitionKey(x => x.ShopType)
                .ClusteringKey(x => x.ItemTag)
                .Column(x => x.ShopType, cm => cm.WithName("shop_name"))
                .Column(x => x.ItemTag, cm => cm.WithName("item_tag"))
                .Column(x => x.BitValue, cm => cm.WithName("bit_value"))
                .Column(x => x.LastUpdated, cm => cm.WithName("last_updated"))
        );
        _bitTagMappingTable = new Table<BitTagMappingDbEntry>(session, mapping);
        _bitTagMappingTable.CreateIfNotExists();
    }

    public async Task StoreTagToBitMapping(BitTagMapping mapping)
    {
        var dbEntry = new BitTagMappingDbEntry
        {
            ShopType = mapping.ShopName,
            ItemTag = mapping.ItemTag,
            BitValue = mapping.BitValue,
            LastUpdated = DateTime.UtcNow
        };
        await _bitTagMappingTable.Insert(dbEntry).ExecuteAsync();
    }

    public async Task<BitTagMapping[]> GetTagToBitMappings(string shopType)
    {
        var entries = await _bitTagMappingTable
            .Where(x => x.ShopType == shopType)
            .ExecuteAsync();
        
        return entries
            .Select(e => new BitTagMapping
            {
                ShopName = e.ShopType,
                ItemTag = e.ItemTag,
                BitValue = e.BitValue
            })
            .ToArray();
    }

    public async Task<BitTagMapping[]> GetAllTagToBitMappings()
    {
        var entries = await _bitTagMappingTable
            .ExecuteAsync();
        
        return entries
            .Select(e => new BitTagMapping
            {
                ShopName = e.ShopType,
                ItemTag = e.ItemTag,
                BitValue = e.BitValue
            })
            .ToArray();
    }
}

/// <summary>
/// Database entity for bit tag mappings
/// </summary>
public class BitTagMappingDbEntry
{
    public string? ShopType { get; set; }
    public string? ItemTag { get; set; }
    public long BitValue { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Represents a tag to bit value mapping entry
/// </summary>
public class BitTagMapping
{
    public string? ShopName { get; set; }
    public string? ItemTag { get; set; }
    public long BitValue { get; set; }
}

/// <summary>
/// Service for storing and retrieving bit tag mappings
/// </summary>
public interface IBitService
{
    Task StoreTagToBitMapping(BitTagMapping mapping);
    Task<BitTagMapping[]> GetTagToBitMappings(string shopType);
    Task<BitTagMapping[]> GetAllTagToBitMappings();
}
