using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using System.Threading;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using Cassandra.Data.Linq;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace Coflnet.Sky.PlayerState.Services
{
    public interface IItemsService
    {
        Task CreateAsync(Item newItem);
        Task<List<Item>> FindItems(IEnumerable<ItemIdSearch> ids);
        Task<List<Item>> FindOrCreate(IEnumerable<Item> original);
        Task<List<Item>> GetAsync(IEnumerable<Item> search);
        Task<Item?> GetAsync(long id);
        Task RemoveAsync(long id);
        Task UpdateAsync(long id, Item updatedItem);
    }

#nullable enable
    public class ItemsService : IItemsService
    {
        private readonly IMongoCollection<StoredItem> collection;
        private readonly ICassandraService cassandraService;
        private static StoredCompare compare = new();
        private static CassandraItemCompare cassandraCompare = new();
        private Prometheus.Counter insertCount = Prometheus.Metrics.CreateCounter("sky_playerstate_mongo_insert", "How many items were inserted");
        private Prometheus.Counter cassandraInsertCount = Prometheus.Metrics.CreateCounter("sky_playerstate_cassandra_insert", "How many items were inserted");
        private Prometheus.Counter selectCount = Prometheus.Metrics.CreateCounter("sky_playerstate_mongo_select", "How many items were selected");

        public ItemsService(
            IOptions<MongoSettings> bookStoreDatabaseSettings, MongoClient mongoClient, ICassandraService cassandraService)
        {
            var mongoDatabase = mongoClient.GetDatabase(
                bookStoreDatabaseSettings.Value.DatabaseName);

            collection = mongoDatabase.GetCollection<StoredItem>(
                bookStoreDatabaseSettings.Value.ItemsCollectionName);
            this.cassandraService = cassandraService;
        }

        public async Task<List<Item>> GetAsync(IEnumerable<Item> search)
        {
            var found = await FindItemsOpen(ToStored(search));
            return found.Select(f => f.ToTransfer()).ToList();
        }

        public async Task<Item?> GetAsync(long id) =>
            (await cassandraService.GetSplitItemsTable(await cassandraService.GetSession())
                .Where(i => i.Id == id).FirstOrDefault().ExecuteAsync())
            ?.ToTransfer();

        public async Task CreateAsync(Item newItem) =>
            await collection.InsertOneAsync(new StoredItem(newItem));

        public async Task UpdateAsync(long id, Item updatedItem) =>
            await collection.ReplaceOneAsync(x => x.Id == id, new StoredItem(updatedItem));

        internal async Task<List<Item>> Find(Item item)
        {
            var builder = Builders<StoredItem>.Filter;
            var selectors = new List<FilterDefinition<StoredItem>>();
            if (!string.IsNullOrEmpty(item.Tag))
                selectors.Add(builder.Eq(e => e.Tag, item.Tag));
            if (!string.IsNullOrEmpty(item.ItemName))
                selectors.Add(builder.Eq(e => e.ItemName, item.ItemName));
            if (item.Enchantments != null)
                foreach (var enchant in item.Enchantments)
                {
                    selectors.Add(builder.Eq("Enchantments." + enchant.Key, enchant.Value));
                }
            if (item.ExtraAttributes != null)
            {
                foreach (var enchant in item.ExtraAttributes)
                {
                    if (enchant.Value is JObject ob)
                    {
                        var map = ob.Descendants()
                                .OfType<JValue>()
                                .ToDictionary(jv => jv.Path, jv => jv.Value);
                        foreach (var element in map)
                        {
                            selectors.Add(builder.Eq($"ExtraAttributes.{enchant.Key}." + element.Key, element.Value));
                        }
                    }
                    else
                        selectors.Add(builder.Eq("ExtraAttributes." + enchant.Key, enchant.Value));
                }
            }
            var filter = builder.And(selectors);
            //var renderedFilter = filter.Render(BsonSerializer.SerializerRegistry.GetSerializer<StoredItem>(), BsonSerializer.SerializerRegistry);
            //Console.WriteLine(renderedFilter);
            var query = await collection.FindAsync(filter);
            var res = await query.ToListAsync();
            return res.Select(i => i.ToTransfer()).ToList();
        }

        public async Task RemoveAsync(long id) =>
            await collection.DeleteOneAsync(x => x.Id == id);

        public async Task<List<Item>> FindOrCreate(IEnumerable<Item> original)
        {
            var cassandraItems = original.Select(i => new CassandraItem(i)).Where(c => c.ItemId != Guid.Empty).ToList();
            if (cassandraItems.Count == 0)
                return new List<Item>();
            var table = cassandraService.GetSplitItemsTable(await cassandraService.GetSession());
            //var oldTable = cassandraService.GetItemsTable(await cassandraService.GetSession());
            var tags = cassandraItems.Select(i => i.Tag).Where(t => t != null).Distinct().Take(30).ToList();
            var res = (await Task.WhenAll(cassandraItems.Select(item =>
            {
                var tag = item.Tag;
                var uuid = item.ItemId;
                return table.Where(i => i.Tag == tag && i.ItemId == uuid).Take(10).ExecuteAsync();
            }))).SelectMany(i => i);
            //var oldRes = await oldTable.Where(i => tags.Contains(i.Tag) && uuids.Contains(i.ItemId)).Take(2_000).ExecuteAsync();
            var found = res.ToList();
            var toCreate = cassandraItems.Except(found, cassandraCompare).Where(c => c.Tag != null).ToList();
            Activity.Current?.AddTag("tags", string.Join(",", tags));
            foreach (var item in toCreate.Where(c => c.Tag.Contains("LAPIS_ARMOR_H")))
            {
                Console.WriteLine("yay " + JsonConvert.SerializeObject(item, Formatting.Indented));
            }
            foreach (var item in found.Where(c => c.Tag.Contains("LAPIS_ARMOR_He")))
            {
                Console.WriteLine("exists " + JsonConvert.SerializeObject(item, Formatting.Indented));
            }
            await Task.WhenAll(toCreate.Select(i =>
            {
                try
                {
                    i.Id = ThreadSaveIdGenerator.NextId;
                    cassandraInsertCount.Inc();
                    Console.WriteLine("Inserting " + i.ItemName + " " + i.Id);
                    return table.Insert(i).ExecuteAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e + " " + JsonConvert.SerializeObject(i));
                    return Task.CompletedTask;
                }
            }));
            if (found.Count > 30)
            {
                await YeetBadData(table, found);
            }
            return found.Concat(toCreate).Select(s => s.ToTransfer()).ToList();
            /*
                        List<StoredItem> batch = ToStored(original);
                        List<StoredItem> found = await FindItems(batch);

                        var toCreate = batch.Except(found, compare).ToList();
                        await InsertBatch(toCreate);

                        return found.Concat(toCreate).Select(s => s.ToTransfer()).ToList();
            */
        }

        private static async Task YeetBadData(Table<CassandraItem> table, List<CassandraItem> found)
        {
            (IGrouping<(string Tag, Guid ItemId, int hash), CassandraItem> biggest, List<long?> matchingIds) = FindBadItems(found);
            if (matchingIds.Count == 0)
            {
                return;
            }
            await Task.WhenAll(matchingIds.Select(i => table.Where(o => o.Id == i && o.ItemId == biggest.Key.ItemId && o.Tag == biggest.Key.Tag).Delete().ExecuteAsync()));
        }

        public static (IGrouping<(string Tag, Guid ItemId, int code), CassandraItem> biggest, List<long?> matchingIds) FindBadItems(List<CassandraItem> found)
        {
            var biggest = found.GroupBy(f => (f.Tag, f.ItemId, (cassandraCompare as IEqualityComparer<CassandraItem>).GetHashCode(f))).OrderByDescending(g => g.Count()).First();
            var elements = biggest.OrderBy(biggest => biggest.Id).Skip(1).ToList();
            if (biggest.Count() <= 2)
            {
                if (found.Count > 90)
                    Console.WriteLine($"Found {found.Count} items with tag {biggest.Key.Tag} and uuid {biggest.Key.ItemId} deleting {biggest.Count()}");
                return (biggest, new List<long?>());
            }
            var matchingElement = elements.Skip(Random.Shared.Next(0, biggest.Count() - 1)).First();
            var matchingIds = elements.Where(e => cassandraCompare.Equals(e, matchingElement)).Select(e => e.Id).ToList();
            Console.WriteLine($"Found {found.Count} items with tag {biggest.Key.Tag} and uuid {biggest.Key.ItemId} deleting {matchingIds.Count} from {biggest.Count()}");
            return (biggest, matchingIds);
        }

        public async Task<List<Item>> FindItems(IEnumerable<ItemIdSearch> ids)
        {
            var table = cassandraService.GetSplitItemsTable(await cassandraService.GetSession());
            //var oldTable = cassandraService.GetItemsTable(await cassandraService.GetSession());
            var res = await table.Where(i => ids.Select(id => id.Tag).Contains(i.Tag) && ids.Select(id => id.Uuid).Contains(i.ItemId)).ExecuteAsync();
           // var oldRes = await oldTable.Where(i => ids.Select(id => id.Tag).Contains(i.Tag) && ids.Select(id => id.Uuid).Contains(i.ItemId)).ExecuteAsync();
            var found = res.ToList();
            return found.Select(i => i.ToTransfer()).ToList();
        }

        private static List<StoredItem> ToStored(IEnumerable<Item> original)
        {
            return original.Select(o => new StoredItem(o)).ToList();
        }

        private async Task<List<StoredItem>> FindItems(List<StoredItem> batch)
        {
            List<StoredItem> res = await FindItemsOpen(batch);

            var found = new List<StoredItem>();
            foreach (var item in batch)
            {
                var match = res.Where(r => compare.Equals(r, item)).FirstOrDefault();
                if (match != null)
                {
                    found.Add(match);
                    selectCount.Inc();
                }
            }

            return found;
        }

        private async Task<List<StoredItem>> FindItemsOpen(List<StoredItem> batch)
        {
            var builder = Builders<StoredItem>.Filter;
            var filter = builder.And(
                builder.In(e => e.ExtraAttributes, batch.Select(e => e.ExtraAttributes)),
                //builder.In(e => e.Enchantments, batch.Select(e => e.Enchantments)),
                builder.In(e => e.Tag, batch.Select(e => e.Tag))
                //builder.In(e => e.ItemName, batch.Select(e => e.ItemName)) the name sometimes changes depending on the inventory, we ignore this
                );


            var query = await collection.FindAsync(filter);
            var res = await query.ToListAsync();
            return res;
        }

        private async Task InsertBatch(List<StoredItem> toCreate)
        {
            if (toCreate.Count == 0)
                return;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    foreach (var item in toCreate)
                    {
                        Console.WriteLine("creating item " + item.ItemName + item.Tag + JsonConvert.SerializeObject(item.ExtraAttrib));
                        item.Id = ThreadSaveIdGenerator.NextId;
                    }
                    await collection.InsertManyAsync(toCreate);
                    insertCount.Inc(toCreate.Count);
                    return;
                }
                catch (System.Exception e)
                {
                    Console.WriteLine(e);
                    await Task.Delay(Random.Shared.Next(0, 100));
                }
            }
        }
    }
}