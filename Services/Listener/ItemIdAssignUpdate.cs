using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class ItemIdAssignUpdate : UpdateListener
{
    private ItemCompare comparer = new();
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
        var localPresent = new Dictionary<Item, Item>(args.currentState.RecentViews.SelectMany(s => s.Items)
                .Where(i => i.Id != null)
                .GroupBy(e => e, comparer)
                .Select(e => e.First()).ToDictionary(e => e, comparer), comparer);
        var foundLocal = toSearchFor.Select(s => localPresent.GetValueOrDefault(s)).Where(s => s != null).ToList()!;
        var toSearchInDb = toSearchFor.Except(foundLocal, (IEqualityComparer<Item?>)comparer).ToList();
        var itemsWithIds = toSearchInDb.Count > 0 ? await service.FindOrCreate(toSearchInDb!) : new List<Item>();

        if (toSearchFor.Count > 0)
            Console.WriteLine("to search: " + toSearchFor.Count + " found local: " + foundLocal.Count + " from db: " + itemsWithIds.Count + " present: " + localPresent.Count);
        Activity.Current?.AddTag("to search", toSearchFor.Count.ToString());
        Activity.Current?.AddTag("found local", foundLocal.Count.ToString());
        Activity.Current?.AddTag("from db", itemsWithIds.Count.ToString());
        Activity.Current?.AddTag("present", localPresent.Count.ToString());
        Activity.Current?.AddTag("chest", chestName);
        args.msg.Chest.Items = Join(collection, itemsWithIds.Concat(foundLocal)!).ToList();
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
        return i.ExtraAttributes != null && i.ExtraAttributes.Count > 1
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
        foreach (var item in original)
        {
            var inMogo = stored.Where(m => comparer.Equals(item, m)).Where(m => m.Id != null).FirstOrDefault();
            if (inMogo != null)
            {
                item.Id = inMogo.Id;
            }
            yield return item;
        }
    }
}
