using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.PlayerName.Client.Api;

namespace Coflnet.Sky.PlayerState.Services;

public class TradeDetect : UpdateListener
{
    public const int IdForCoins = 1_000_001;
    public ILogger<TradeDetect> logger;
    private CoinParser parser = new();
    private Core.ItemDetails itemDetails;

    public TradeDetect(ILogger<TradeDetect> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (itemDetails == null)
            itemDetails = args.GetService<Core.ItemDetails>();
        if (args.msg.PlayerId == "Core" || (args.currentState.Settings?.DisableTradeTracking ?? false))
            {
                Console.WriteLine("trade tracking blocked for " + args.currentState.PlayerId);
                return;
            }
        if (args.msg.Kind == UpdateMessage.UpdateKind.CHAT)
        {
            var lastMessage = args.msg.ChatBatch.Last();
            if (!lastMessage.StartsWith(" + ") && !lastMessage.StartsWith(" - "))
                return;

            Console.WriteLine("trade completed by " + args.currentState.PlayerId);
            await StoreTrade(args);

            return;
        }
        var chest = args.msg.Chest;
        if (chest.Name == null || !chest.Name.StartsWith("You                  "))
            return; // not a trade
        var otherSide = args.msg.Chest.Name.Substring(21);
        Console.WriteLine("Got trade menu with " + args.msg.Chest.Name.Substring(21));
    }

    private async Task StoreTrade(UpdateArgs args)
    {
        var tradeView = args.currentState.RecentViews.Where(t => t.Name?.StartsWith("You    ") ?? false).LastOrDefault();
        if (tradeView == null)
        {
            logger.LogError("no trade view was found");
            return;
        }
        ParseTradeWindow(tradeView, out var spent, out var received);
        foreach (var item in spent)
        {
            Console.WriteLine("sent " + item.ItemName);
        }
        foreach (var item in received)
        {
            Console.WriteLine("got " + item.ItemName);
        }
        var timestamp = DateTime.UtcNow;
        var transactions = new List<Transaction>();
        transactions.AddRange(spent.Select(s =>
        {
            return CreateTransaction(args, s, timestamp, Transaction.TransactionType.TRADE | Transaction.TransactionType.REMOVE);
        }));
        transactions.AddRange(received.Select(s =>
        {
            return CreateTransaction(args, s, timestamp, Transaction.TransactionType.TRADE | Transaction.TransactionType.RECEIVE);
        }));

        // other player
        try
        {
            await AddOtherSideOfTrade(args, spent, received, timestamp, transactions, tradeView);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying to add other side of trade " + tradeView.Name);
        }

        var service = args.GetService<ITransactionService>();
        await service.AddTransactions(transactions.Where(t => t.ItemId > 0).ToList());
        try
        {
            StoreUuidtoItemMapping(service, spent, received);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying to store uuid to item mapping");
        }
        try
        {
            var tradeService = args.GetService<ITradeService>();
            var playerName = tradeView.Name.Substring(21).Trim();
            var trademodel = new TradeModel()
            {
                UserId = args.msg.UserId,
                MinecraftUuid = args.currentState.McInfo.Uuid,
                Spent = spent,
                Received = received,
                OtherSide = playerName,
                TimeStamp = timestamp
            };
            await tradeService.ProduceTrade(trademodel);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Producing player trade");
        }

    }

    public static void ParseTradeWindow(ChestView? tradeView, out List<Item> spent, out List<Item> received)
    {
        spent = new List<Item>();
        received = new List<Item>();
        var index = 0;
        if (tradeView == null)
            return;
        foreach (var item in tradeView.Items)
        {
            var i = index++;
            if (i >= 36 || IsLastItemInWindow(item))
                break;
            if (item == null || item.ItemName == null)
                continue;
            var column = i % 9;
            if (column < 4)
                spent.Add(item);
            else if (column > 4)
                received.Add(item);
        }

        static bool IsLastItemInWindow(Item item)
        {
            return item.ItemName != null && item.ItemName.Contains("Deal timer!");
        }
    }

    private static void StoreUuidtoItemMapping(ITransactionService service, List<Item> spent, List<Item> received)
    {
        var itemUuidAndItemId = spent.Concat(received).Where(i => i.Id > 0).Select(s => (s.ExtraAttributes?.GetValueOrDefault("uuid"), s.Id))
                    .Where(c => c.Item1 != null).Select(c => (Guid.Parse(c.Item1!.ToString()!), c.Id)).ToList();
        service.StoreUuidToItemMapping(itemUuidAndItemId);
    }

    private async Task AddOtherSideOfTrade(UpdateArgs args, List<Item> spent, List<Item> received, DateTime timestamp, List<Transaction> transactions, ChestView chest)
    {
        var nameService = args.GetService<IPlayerNameApi>();
        var playerName = chest.Name.Substring(21);
        var uuidString = await nameService.PlayerNameUuidNameGetAsync(playerName);
        logger.LogInformation($"other side of trade is {playerName} {uuidString}");
        var uuid = Guid.Parse(uuidString.Trim('"'));
        transactions.AddRange(spent.Select(s =>
        {
            return CreateTransaction(uuid, s, timestamp, Transaction.TransactionType.TRADE | Transaction.TransactionType.RECEIVE);
        }));
        transactions.AddRange(received.Select(s =>
        {
            return CreateTransaction(uuid, s, timestamp, Transaction.TransactionType.TRADE | Transaction.TransactionType.REMOVE);
        }));
    }

    private Transaction CreateTransaction(UpdateArgs args, Item s, DateTime timestamp, Transaction.TransactionType type)
    {
        var playerUuid = args.currentState.McInfo.Uuid;
        var trnsaction = CreateTransaction(playerUuid, s, timestamp, type);
        logger.LogInformation($"Creating transaction for {playerUuid} with {s.ItemName} {s.Id} {s.Tag}");
        return trnsaction;
    }

    private Transaction CreateTransaction(Guid playerUuid, Item s, DateTime timestamp, Transaction.TransactionType type)
    {
        var transaction = new Transaction()
        {
            PlayerUuid = playerUuid,
            Type = type,
            ItemId = s.Id ?? GetIdForItem(s),
            TimeStamp = timestamp,
            Amount = s.Count ?? -1
        };
        if (transaction.ItemId >= 1_000_000 && transaction.ItemId < 1_999_999) // special property id
        {
            transaction.Amount = transaction.ItemId switch
            {
                IdForCoins => parser.GetCoinAmount(s),
                _ => 0
            };
        }

        return transaction;
    }

    private int GetIdForItem(Item item)
    {
        if (parser.IsCoins(item))
            return IdForCoins;
        return itemDetails.GetItemIdForTag(item.Tag);
    }
}
