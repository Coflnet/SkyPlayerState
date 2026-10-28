using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.PlayerName.Client.Api;

namespace Coflnet.Sky.PlayerState.Services;

public class TradeDetect : UpdateListener
{
    public const int IdForCoins = (int)SpecialTransactionItemIds.Coins;
    private static readonly Regex MinecraftFormattingRegex = new("§.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsernameRegex = new(@"\b[A-Za-z0-9_]{3,16}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public ILogger<TradeDetect> logger;
    private static CoinParser parser = new();
    private Core.ItemDetails? itemDetails;

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
            Logger.LogDebug("trade tracking blocked for {playerId}", args.currentState.PlayerId);
            return;
        }
        if (args.msg.Kind == UpdateMessage.UpdateKind.CHAT)
        {
            var lastMessage = args.msg.ChatBatch?.LastOrDefault();
            if (lastMessage == null)
                return;
            if (!lastMessage.StartsWith(" + ") && !lastMessage.StartsWith(" - "))
                return;

            Logger.LogInformation("trade completed by {playerId}", args.currentState.PlayerId);
            await StoreTrade(args);

            return;
        }
        var chest = args.msg.Chest;
        if (chest?.Name == null || !chest.Name.StartsWith("You                  "))
            return; // not a trade
        var otherSide = ExtractTradePartnerNameFromChest(chest.Name);
        Logger.LogDebug("Got trade menu with {otherSide}", otherSide);
    }

    private async Task StoreTrade(UpdateArgs args)
    {
        var tradeView = args.currentState.RecentViews.Where(t => t.Name?.StartsWith("You    ") ?? false).LastOrDefault();
        if (tradeView == null)
        {
            // expected when the trade window already left the recent views (only the last few are kept) or
            // the chat line was not a trade at all; only a real "Trade completed" line is worth a warning
            var completed = args.msg.ChatBatch?.Any(l => StripMinecraftFormatting(l).StartsWith("Trade completed with ", StringComparison.Ordinal)) ?? false;
            logger.Log(completed ? LogLevel.Warning : LogLevel.Debug,
                "no trade view found for {playerId}, trade completed line present: {completed}", args.currentState.PlayerId, completed);
            return;
        }
        ParseTradeWindow(tradeView, out var spent, out var received);
        var completedLines = GetLatestTradeCompletedLines(args.msg.ChatBatch);
        AddCoinsMissingFromWindow(completedLines, " - ", spent);
        AddCoinsMissingFromWindow(completedLines, " + ", received);
        foreach (var item in spent)
        {
            Logger.LogDebug("sent {itemName}", item.ItemName);
        }
        foreach (var item in received)
        {
            Logger.LogDebug("got {itemName}", item.ItemName);
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
            var playerName = ResolveOtherSideName(args, tradeView);
            await AddOtherSideOfTrade(args, spent, received, timestamp, transactions, playerName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying to add other side of trade {tradeName}", tradeView.Name);
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
            var playerName = ResolveOtherSideName(args, tradeView);
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

    private string ResolveOtherSideName(UpdateArgs args, ChestView tradeView)
    {
        var candidate = TryExtractTradePartnerNameFromChat(args.msg.ChatBatch)
            ?? TryExtractTradePartnerNameFromStatusItem(tradeView)
            ?? ExtractTradePartnerNameFromChest(tradeView.Name);

        return ExpandTradePartnerName(candidate, args.currentState.LastTab);
    }

    internal static string ExtractTradePartnerNameFromChest(string? chestName)
    {
        if (string.IsNullOrWhiteSpace(chestName))
            return string.Empty;
        if (!chestName.StartsWith("You"))
            return chestName.Trim();

        return chestName.AsSpan(3).TrimStart().ToString().Trim();
    }

    internal static string ExpandTradePartnerName(string? playerName, IEnumerable<string>? tabEntries)
    {
        var cleanedName = StripMinecraftFormatting(playerName).Trim();
        if (string.IsNullOrWhiteSpace(cleanedName) || tabEntries == null)
            return cleanedName;

        var candidates = tabEntries
            .SelectMany(ExtractPlayerNamesFromTabEntry)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var exactMatch = candidates.FirstOrDefault(name => name.Equals(cleanedName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return exactMatch;

        var prefixMatches = candidates
            .Where(name => name.StartsWith(cleanedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prefixMatches.Count == 1)
            return prefixMatches[0];

        return cleanedName;
    }

    private static IEnumerable<string> ExtractPlayerNamesFromTabEntry(string? tabEntry)
    {
        if (string.IsNullOrWhiteSpace(tabEntry))
            return Enumerable.Empty<string>();

        var cleanedEntry = StripMinecraftFormatting(tabEntry);
        return UsernameRegex.Matches(cleanedEntry).Select(match => match.Value);
    }

    private static string? TryExtractTradePartnerNameFromChat(IEnumerable<string>? chatBatch)
    {
        if (chatBatch == null)
            return null;

        var completedLine = chatBatch
            .Select(StripMinecraftFormatting)
            .FirstOrDefault(line => line.StartsWith("Trade completed with ", StringComparison.Ordinal));
        if (completedLine == null)
            return null;

        return completedLine["Trade completed with ".Length..].Trim().TrimEnd('!', '.');
    }

    private static string? TryExtractTradePartnerNameFromStatusItem(ChestView tradeView)
    {
        var statusLine = tradeView.Items
            .Where(item => !string.IsNullOrWhiteSpace(item?.Description))
            .SelectMany(item => item!.Description!.Split('\n'))
            .Select(StripMinecraftFormatting)
            .FirstOrDefault(line => line.StartsWith("Trading with ", StringComparison.Ordinal));
        if (statusLine == null)
            return null;

        return statusLine["Trading with ".Length..].Trim().TrimEnd('!', '.');
    }

    private static string StripMinecraftFormatting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return MinecraftFormattingRegex.Replace(value, string.Empty);
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
            if (item == null || item.ItemName == null || item.Tag == null && !parser.IsCoins(item))
                continue;
            var column = i % 9;
            if (column < 4)
                spent.Add(item);
            else if (column > 4)
                received.Add(item);
        }

        static bool IsLastItemInWindow(Item item)
        {
            return item.ItemName != null && (
                item.ItemName.Contains("Deal timer!") || item.ItemName.Contains("Deal!") || item.ItemName.Contains("Deal accepted!") || item.ItemName == "§eNew deal");
        }
    }

    /// <summary>
    /// The trade window snapshot can predate the last offer change (e.g. the other player adding coins
    /// right before both confirmed), while the "Trade completed" chat lists the final coins.
    /// Adds a coin item from the chat line when the window side has none.
    /// </summary>
    internal static void AddCoinsMissingFromWindow(List<string> completedLines, string prefix, List<Item> side)
    {
        if (side.Any(parser.IsCoins))
            return;
        var coinLine = completedLines
            .LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(" coins", StringComparison.Ordinal));
        if (coinLine == null)
            return;
        var amount = coinLine[prefix.Length..^" coins".Length].Trim();
        var coinItem = new Item()
        {
            ItemName = $"§6{amount} coins",
            Description = $"§6Total Coins Offered:\n§7{amount}",
            Count = 1
        };
        if (parser.GetCoinAmount(coinItem) > 0)
            side.Add(coinItem);
    }

    /// <summary>
    /// The trailing " + "/" - " lines of the batch, i.e. the trade that triggered this store, so coins of
    /// an earlier trade in the same batch are never attached to it.
    /// </summary>
    private static List<string> GetLatestTradeCompletedLines(IEnumerable<string>? chatBatch)
    {
        return (chatBatch ?? Enumerable.Empty<string>())
            .Select(StripMinecraftFormatting)
            .Reverse()
            .TakeWhile(line => line.StartsWith(" + ", StringComparison.Ordinal) || line.StartsWith(" - ", StringComparison.Ordinal))
            .ToList();
    }

    private static void StoreUuidtoItemMapping(ITransactionService service, List<Item> spent, List<Item> received)
    {
        var itemUuidAndItemId = spent.Concat(received).Where(i => i.Id > 0).Select(s => (s.ExtraAttributes?.GetValueOrDefault("uuid"), s.Id))
                    .Where(c => c.Item1 != null).Select(c => (Guid.Parse(c.Item1!.ToString()!), c.Id)).ToList();
        service.StoreUuidToItemMapping(itemUuidAndItemId);
    }

    private async Task AddOtherSideOfTrade(UpdateArgs args, List<Item> spent, List<Item> received, DateTime timestamp, List<Transaction> transactions, string playerName)
    {
        var nameService = args.GetService<IPlayerNameApi>();
        var uuidString = await nameService.PlayerNameUuidNameGetAsync(playerName);
        if (!Guid.TryParse(uuidString?.Trim().Trim('"'), out var uuid))
        {
            // nicked/hidden names have no uuid, the other side is simply not tracked
            logger.LogDebug("no uuid for other side of trade {playerName}, got {uuidString}", playerName, uuidString);
            return;
        }
        logger.LogInformation("other side of trade is {playerName} {uuid}", playerName, uuid);
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
        return itemDetails!.GetItemIdForTag(item.Tag);
    }
}
