using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class ItemIdAssignUpdate : UpdateListener
{
    private const int ItemIdCacheLimit = 20_000;
    private ItemCompare comparer = new();
    private readonly ConcurrentDictionary<Item, long> itemIdCache = new(new ItemCompare());
    private readonly ConcurrentQueue<Item> itemIdCacheInsertionOrder = new();
    private static readonly Prometheus.Counter itemIdSearchCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_itemid_search_total",
        "Total number of item-id database searches performed.");
    private static readonly Prometheus.Counter itemIdCacheHitCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_itemid_cache_hit_total",
        "Total number of item IDs resolved from the process-local cache.");
    private static readonly Prometheus.Gauge itemIdCacheSize = Prometheus.Metrics.CreateGauge(
        "sky_playerstate_itemid_cache_size",
        "Number of item identities held in the process-local cache.");

    public override async Task Process(UpdateArgs args)
    {
        var service = args.GetService<IItemsService>();
        var collection = args.msg.Chest.Items;
        
        // Normalize items: convert JObjects to Dictionaries so they can be compared with stored items
        // This is necessary because Kafka deserialization creates JObject instances for nested structures,
        // while Cassandra retrieval converts them to Dictionaries
        foreach (var item in collection)
        {
            NormalizeItem(item);
        }
        
        var chestName = args.msg.Chest.Name;
        var toSearchFor = collection.Where(i => CanGetAnIdByStoring(i, chestName)).ToHashSet();
        var foundCached = toSearchFor.Where(TryAssignCachedId).ToList();
        var unresolved = toSearchFor.Where(item => item.Id == null).ToList();
        var localPresent = unresolved.Count == 0
            ? new Dictionary<Item, Item>(comparer)
            : new Dictionary<Item, Item>(args.currentState.RecentViews.SelectMany(s => s.Items)
                    .Where(i => i.Id != null)
                    .GroupBy(e => e, comparer)
                    .Select(e => e.First()).ToDictionary(e => e, comparer), comparer);
        var foundLocal = unresolved.Select(s => localPresent.GetValueOrDefault(s)).Where(s => s != null).ToList()!;
        var toSearchInDb = unresolved.Except(foundLocal, (IEqualityComparer<Item?>)comparer).ToList();
        itemIdSearchCount.Inc(toSearchInDb.Count);
        var itemsWithIds = toSearchInDb.Count > 0 ? await service.FindOrCreate(toSearchInDb!) : new List<Item>();
        Cache(itemsWithIds);

        Activity.Current?.AddTag("to search", toSearchFor.Count.ToString());
        Activity.Current?.AddTag("found local", foundLocal.Count.ToString());
        Activity.Current?.AddTag("found cached", foundCached.Count.ToString());
        Activity.Current?.AddTag("from db", itemsWithIds.Count.ToString());
        Activity.Current?.AddTag("present", localPresent.Count.ToString());
        Activity.Current?.AddTag("chest", chestName);
        args.msg.Chest.Items = Join(collection, itemsWithIds.Concat(foundLocal).Concat(foundCached)!).ToList();
    }

    private bool TryAssignCachedId(Item item)
    {
        if (!itemIdCache.TryGetValue(item, out var id))
            return false;
        item.Id = id;
        itemIdCacheHitCount.Inc();
        return true;
    }

    private void Cache(IEnumerable<Item> items)
    {
        foreach (var item in items.Where(item => item.Id != null))
        {
            // Display data is not part of ItemCompare identity; avoid retaining large lore strings.
            var cacheKey = new Item(item) { ItemName = null, Description = null, Count = null };
            if (itemIdCache.TryAdd(cacheKey, item.Id!.Value))
                itemIdCacheInsertionOrder.Enqueue(cacheKey);
        }
        while (itemIdCache.Count > ItemIdCacheLimit && itemIdCacheInsertionOrder.TryDequeue(out var oldest))
            itemIdCache.TryRemove(oldest, out _);
        itemIdCacheSize.Set(itemIdCache.Count);
    }

    private static void NormalizeItem(Item item)
    {
        if (item.ExtraAttributes == null) return;
        
        foreach (var key in item.ExtraAttributes.Keys.ToList())
        {
            // Convert JToken/JObject to native .NET types
            if (item.ExtraAttributes[key] is Newtonsoft.Json.Linq.JToken token)
            {
                item.ExtraAttributes[key] = CassandraItem.ConvertJTokenToNative(token);
            }
        }
    }

    private static bool CanGetAnIdByStoring(Item i, string chestName)
    {
        // one extra attribute is the tier
        return i.Id == null && i.ExtraAttributes != null && i.ExtraAttributes.Count > 1
            && !IsNpcSell(i) && !IsBazaar(chestName) && !IsDungeonItem(i)
            && !IsInAnInventory(i);
    }

    private static bool IsInAnInventory(Item i)
    {
        // Hypixel assigns ids for shift click detection every second, ignore those items, eg §eClick to take fuel out!
        return i.Description != null && i.Description.Contains("§eClick"); 
    }

    private static bool IsDungeonItem(Item i)
    {
        // eg. REVIVE_STONE
        return i.ExtraAttributes?.ContainsKey("dontSaveToProfile") ?? false;
    }

    private static bool IsBazaar(string chestName)
    {
        return chestName?.Contains("➜") ?? false;
    }

    private static bool IsNpcSell(Item i)
    {
        // Another valid indicator would be "Click to trade!"
        return (i.Description?.Contains("§7Cost\n") ?? false)
        // Firesales have different lore
        || (i.Description?.Contains("§7Cost: §a") ?? false);
    }

    public IEnumerable<Item> Join(IEnumerable<Item> original, IEnumerable<Item> stored)
    {
        var storedByItem = stored.Where(item => item.Id != null)
            .GroupBy(item => item, comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        foreach (var item in original)
        {
            if (storedByItem.TryGetValue(item, out var storedItem))
            {
                item.Id = storedItem.Id;
            }
            yield return item;
        }
    }
}
