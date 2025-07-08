using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Cassandra.Data.Linq;
using System.Diagnostics;

namespace Coflnet.Sky.PlayerState.Services
{
    public interface IItemsService
    {
        Task<List<Item>> FindItems(IEnumerable<ItemIdSearch> ids);
        Task<List<Item>> FindOrCreate(IEnumerable<Item> original);
        Task<Item?> GetAsync(long id);
    }

#nullable enable
    public class ItemsService : IItemsService
    {
        private readonly ICassandraService cassandraService;
        private static CassandraItemCompare cassandraCompare = new();
        private Prometheus.Counter cassandraInsertCount = Prometheus.Metrics.CreateCounter("sky_playerstate_cassandra_insert", "How many items were inserted");

        public ItemsService(ICassandraService cassandraService)
        {
            this.cassandraService = cassandraService;
        }

        public async Task<Item?> GetAsync(long id) =>
            (await cassandraService.GetSplitItemsTable(await cassandraService.GetSession())
                .Where(i => i.Id == id).FirstOrDefault().ExecuteAsync())
            ?.ToTransfer();

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
    }
}