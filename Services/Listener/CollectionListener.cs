using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.Sniper.Client.Api;

namespace Coflnet.Sky.PlayerState.Services;

public class CollectionListener : UpdateListener
{
    private Dictionary<string, string> NametoTagLookup;
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
        {
            await HandleScoreboard(args);
            return;
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
        {
            HandleInventory(args);
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.CHAT)
        {
            // stash messages
            foreach (var uploadedLine in args.msg.ChatBatch)
            {
                if (uploadedLine.StartsWith("You caught"))
                    await HandleShardCatch(args, uploadedLine);
                if (uploadedLine.StartsWith("Added items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.StartsWith("Removed items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.Contains("Chameleon (0."))
                    args.currentState.ItemsCollectedRecently["SHARD_CHAMELEON"] = args.currentState.ItemsCollectedRecently.GetValueOrDefault("SHARD_CHAMELEON", 0) + 1;
            }
        }
    }

    private async Task HandleShardCatch(UpdateArgs args, string uploadedLine)
    {
        // eg "You caught a Verdant Shard!" "You caught x2 Birries Shards!" "LOOT SHARE You received a Chill Shard for assisting Oden."
        var match = Regex.Match(uploadedLine, @"You (caught|received) (a|x\d) (.*) Shards?(!| for)");
        if (!match.Success)
        {
            Console.WriteLine($"Failed to match shard catch: {uploadedLine}");
            return;
        }
        var shardName = match.Groups[3].Value.Trim();
        var count = match.Groups[2].Value.StartsWith("x") ? int.Parse(match.Groups[2].Value.Substring(1)) : 1;
        var tag = "SHARD_" + shardName.ToUpperInvariant().Replace(" ", "_");
        args.currentState.ItemsCollectedRecently[tag] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(tag, 0) + count;
    }

    private async Task HandleSackNotification(UpdateArgs args, string uploadedLine)
    {
        if (NametoTagLookup == null)
        {
            var itemApi = args.GetService<Items.Client.Api.IItemsApi>();
            var names = await itemApi.ItemNamesGetAsync();
            NametoTagLookup = names.Where(g => g.Name != null).GroupBy(g => g.Name).Select(g => g.First()).ToDictionary(n => n.Name, n => n.Tag);
        }
        var lines = uploadedLine.Split('\n').Skip(1).Reverse().Skip(2).ToList();
        foreach (var item in lines)
        {
            // @" \+([\d,]+) ([^(]+) "
            var match = Regex.Match(item, @" ([+-]?[\d,]+) ([^(]+) ");
            if (match.Success)
            {
                var itemName = match.Groups[2].Value.Trim();
                if (int.TryParse(match.Groups[1].Value.Replace(",", ""), out var count))
                {
                    var tag = NametoTagLookup.GetValueOrDefault(itemName);
                    if (tag == null)
                    {
                        Console.WriteLine($"Item not found in lookup: {itemName}");
                        continue;
                    }
                    args.currentState.ItemsCollectedRecently[tag] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(tag, 0) + count;
                    Console.WriteLine($"Item collected from stash: {itemName} x{count} for player {args.currentState.PlayerId}");
                }
                else
                {
                    Console.WriteLine($"Failed to parse item count from chat: {match.Groups[1].Value}");
                }
            }
            else
            {
                Console.WriteLine($"Failed to match item from chat: {item}");
            }
        }
    }

    static void HandleInventory(UpdateArgs args)
    {
        var previousInventory = args.currentState.RecentViews.Reverse().Skip(1).FirstOrDefault();
        if (previousInventory == null)
            return;
        Dictionary<string, int?> mapOfItems = GetLookupItemCount(previousInventory);
        var currentInventory = GetLookupItemCount(args.msg.Chest);
        foreach (var item in currentInventory)
        {
            if (item.Value == null)
                continue;
            if (mapOfItems.TryGetValue(item.Key, out var previousCount))
            {
                if (previousCount == null)
                    continue;
                var diff = item.Value - previousCount;
                if (diff != 0)
                {
                    args.currentState.ItemsCollectedRecently[item.Key] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(item.Key, 0) + (int)diff;
                    Console.WriteLine($"Item collected: {item.Key} x{diff} for player {args.currentState.PlayerId}");
                }
            }
        }
        static Dictionary<string, int?> GetLookupItemCount(Models.ChestView? previousInventory)
        {
            // skip more than the 4 lines above and maybe 1 offhand slot
            var accessibleInventory = previousInventory.Items.Skip(previousInventory.Items.Count - 36 / 9 * 9).Take(36).ToList();
            var mapOfItems = accessibleInventory
                .Where(i => i.Tag != null && i.ItemName != null)
                .GroupBy(i => i.Tag)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
            return mapOfItems;
        }
    }

    private static async Task HandleScoreboard(UpdateArgs args)
    {
        // 07/14/15
        var currentDate = DateTime.UtcNow.ToString("MM/dd/yy");
        var yesterdayDate = DateTime.UtcNow.AddDays(-1).ToString("MM/dd/yy");
        var server = args.msg.Scoreboard?.FirstOrDefault(s => s.StartsWith(currentDate) || s.StartsWith(yesterdayDate))?.Split(' ')[1];
        if (server != null)
        {
            args.currentState.ExtractedInfo.CurrentServer = server;
        }
        var currentLocation = args.msg.Scoreboard?.FirstOrDefault(s => s.StartsWith(" ⏣ "))?.Substring(3).Trim();
        if (currentLocation == null)
        {
            return;
        }
        var previousLocation = args.currentState.ExtractedInfo.CurrentLocation;
        if (previousLocation != null && previousLocation != currentLocation
            // if the same location is used, attempt to store it for people staying in same location
            || args.currentState.ExtractedInfo.LastLocationChange < DateTime.UtcNow.AddMinutes(-5))
        {
            await StoreLocationProfit(args, previousLocation);
        }
        args.currentState.ExtractedInfo.CurrentLocation = currentLocation;
    }

    private static async Task StoreLocationProfit(UpdateArgs args, string previousLocation)
    {
        var profit = 0L;
        var collected = args.currentState.ItemsCollectedRecently;
        if (collected.Count > 0)
        {
            var cleanPrices = new Dictionary<string, double>();
            var ahPrices = await args.GetService<ISniperApi>().ApiSniperPricesCleanGetAsync();
            var bazaarPrices = await args.GetService<IBazaarApi>().ApiBazaarPricesGetAsync();
            foreach (var item in bazaarPrices)
            {
                cleanPrices[item.ProductId] = (int)item.SellPrice;
            }
            foreach (var item in ahPrices)
            {
                if (item.Value > 0)
                    cleanPrices[item.Key] = item.Value;
            }

            profit = (long)collected.Select(c =>
            {
                var price = cleanPrices.GetValueOrDefault(c.Key);
                return price * c.Value;
            }).Sum();
            await args.GetService<TrackedProfitService>().AddPeriod(new()
            {
                EndTime = DateTime.UtcNow,
                StartTime = args.currentState.ExtractedInfo.LastLocationChange,
                Location = previousLocation,
                PlayerUuid = args.currentState.McInfo.Uuid.ToString("N"),
                Server = args.currentState.ExtractedInfo.CurrentServer,
                ItemsCollected = new Dictionary<string, int>(args.currentState.ItemsCollectedRecently),
                Profit = profit
            });
            args.SendDebugMessage("You collected a total of " + profit + " coins worth of items in " + previousLocation + " " + string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}")));
            Console.WriteLine($"Profit summary for {args.currentState.PlayerId} at {previousLocation}: {profit} coins from {string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}"))}");
        }
        args.currentState.ItemsCollectedRecently.Clear();
        args.currentState.ExtractedInfo.LastLocationChange = DateTime.UtcNow;
    }
}
