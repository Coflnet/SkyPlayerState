using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class RngMeterUpdate : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest == null || chest.Name == null || !chest.Name.EndsWith("RNG Meter") || args.msg.UserId == null)
            return;

        var service = args.GetService<RngMeterService>();
        var items = chest.Items;
        if (items == null || items.Count == 0)
            return;

        // iterate items until the Go Back item
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null || string.IsNullOrWhiteSpace(it.ItemName) || it.ItemName == "§dRNG Meter")
                continue;
            if (!string.IsNullOrEmpty(it.ItemName) && it.ItemName.Contains("Go Back"))
                break;

            var cleanedDescription = it.Description == null ? null : Regex.Replace(it.Description, "§.", "");
            string? requirement = null;
            long? expTarget = null;

            if (!string.IsNullOrEmpty(cleanedDescription))
            {
                // Try to find a "Requires ..." line
                var reqLine = cleanedDescription.Split('\n').FirstOrDefault(l => l.Contains("Requires"));
                if (reqLine != null)
                    requirement = reqLine.Trim();

                // Try to extract slayer xp like "Slayer XP: 0/75,000,000" or similar
                var xpMatch = Regex.Match(cleanedDescription, @"XP:\s*([\d,]+)\s*/\s*([\d,]+)");
                if (xpMatch.Success)
                {
                    var tgt = xpMatch.Groups[2].Value.Replace(",", "");
                    if (long.TryParse(tgt, out var t))
                        expTarget = t;
                }
            }

            var record = new RngMeterItem
            {
                ChestName = chest.Name,
                ItemIndex = i,
                ItemTag = it.Tag,
                ItemName = it.ItemName,
                Requirement = requirement,
                ExpTarget = expTarget,
                Lore = cleanedDescription,
                LastUpdated = DateTime.UtcNow,
                LastUpdatedBy = args.msg.UserId + "-" + args.msg.PlayerId
            };

            try
            {
                await service.Save(record);
            }
            catch (Exception e)
            {
                args.GetService<ILogger<RngMeterUpdate>>()?.LogError(e, "Failed to save RNG meter item {Chest} {Index}", chest.Name, i);
            }
        }
    }
}

public class RngMeterService
{
    private Table<RngMeterItem> table;
    private readonly ILogger<RngMeterService> logger;

    public RngMeterService(ISession session, ILogger<RngMeterService> logger)
    {
        this.logger = logger;
        var mapping = new MappingConfiguration().Define(
            new Map<RngMeterItem>()
                .TableName("rng_meter_items")
                .PartitionKey(r => r.ChestName)
                .ClusteringKey(r => r.ItemIndex)
                .Column(r => r.ChestName, cm => cm.WithName("chest_name"))
                .Column(r => r.ItemIndex, cm => cm.WithName("item_index"))
                .Column(r => r.ItemTag, cm => cm.WithName("item_tag"))
                .Column(r => r.ItemName, cm => cm.WithName("item_name"))
                .Column(r => r.Requirement, cm => cm.WithName("requirement"))
                .Column(r => r.ExpTarget, cm => cm.WithName("exp_target"))
                .Column(r => r.Lore, cm => cm.WithName("lore"))
                .Column(r => r.LastUpdated, cm => cm.WithName("last_updated"))
                .Column(r => r.LastUpdatedBy, cm => cm.WithName("last_updated_by"))
        );
        table = new Table<RngMeterItem>(session, mapping);
        table.CreateIfNotExists();
    }

    public async Task Save(RngMeterItem item)
    {
        try
        {
            // Read existing record so we don't overwrite non-null values with nulls
            var existingRows = await table.Where(r => r.ChestName == item.ChestName && r.ItemIndex == item.ItemIndex).ExecuteAsync();
            var existing = existingRows.FirstOrDefault();
            if (existing != null)
            {
                // Merge fields: if the incoming item lacks a value, keep existing
                if (string.IsNullOrEmpty(item.ItemTag))
                    item.ItemTag = existing.ItemTag;
                if (string.IsNullOrEmpty(item.ItemName))
                    item.ItemName = existing.ItemName;
                if (string.IsNullOrEmpty(item.Requirement))
                    item.Requirement = existing.Requirement;
                if (item.ExpTarget == null)
                    item.ExpTarget = existing.ExpTarget;
                if (string.IsNullOrEmpty(item.Lore))
                    item.Lore = existing.Lore;
            }

            await table.Insert(item).ExecuteAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to save rng meter item {Chest}:{Index} {full}", item.ChestName, item.ItemIndex, JsonConvert.SerializeObject(item));
            throw;
        }
    }

    public async Task<System.Collections.Generic.IEnumerable<RngMeterItem>> Get(string chestName)
    {
        return await table.Where(r => r.ChestName == chestName).ExecuteAsync();
    }

    public async Task<System.Collections.Generic.IEnumerable<RngMeterItem>> GetAll()
    {
        return await table.ExecuteAsync();
    }
}

public class RngMeterItem
{
    public string ChestName { get; set; }
    public int ItemIndex { get; set; }
    public string ItemTag { get; set; }
    public string ItemName { get; set; }
    public string Requirement { get; set; }
    public long? ExpTarget { get; set; }
    public string Lore { get; set; }
    public DateTime LastUpdated { get; set; }
    public string LastUpdatedBy { get; set; }
}
