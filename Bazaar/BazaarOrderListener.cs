using System.Threading.Tasks;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Models;
using System.Text.RegularExpressions;
using System.Linq;
using Newtonsoft.Json;
using System.Globalization;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.Bazaar.Client.Api;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.currentState.Settings?.DisableBazaarTracking ?? false)
            return;
        await Parallel.ForEachAsync(args.msg.ChatBatch, async (item, ct) =>
        {
            if (!item.StartsWith("[Bazaar]") || item.StartsWith("[Bazaar] There are no"))
                return;
            await HandleUpdate(item, args);
        });
    }
    /// <summary>
    /// Listing => coins/item locked up (REMOVE)
    /// Filled => coins/item exchanged
    /// Canceled => coins/item unlocked (RECEIVE)
    /// Claimed => coins/item unlocked (RECEIVE)
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    private static async Task HandleUpdate(string msg, UpdateArgs args)
    {
        Console.WriteLine(msg);
        var side = Transaction.TransactionType.BAZAAR;
        var amount = 0;
        var itemName = "";
        long price = 0;
        if (msg.Contains("Buy Order"))
            side |= Transaction.TransactionType.RECEIVE;
        var isSell = msg.Contains("Sell Offer");
        if (isSell)
            side |= Transaction.TransactionType.REMOVE;
        if (msg.Contains("Setup!"))
        {
            side |= Transaction.TransactionType.Move;
            var parts = Regex.Match(msg, @"([\d,]+)x (.+) for ([\d,]+\.?\d*) coins").Groups;
            amount = ParseInt(parts[1].Value);
            itemName = parts[2].Value;
            price = ParseCoins(parts[3].Value);
            if (price > 10000)
            {
                // hypixel doesn't display the decimal price anymore, we got to parse it from the item in previous gui
                Console.WriteLine($"Found from message {price} ({(double)price / amount / 10} per unit) for {amount}x {itemName}");
                var lastView = args.currentState.RecentViews.LastOrDefault(v => v.Name.Contains("Confirm"));
                if (lastView != null)
                {
                    var item = lastView.Items.FirstOrDefault(i => i.ItemName != null && (i.ItemName.Contains("Buy Order") || i.ItemName.Contains("Sell Offer")));
                    if (item != null)
                    {
                        var match = Regex.Match(item.Description, @"Price per unit: §6([\d,]+\.?\d*) coins").Groups;
                        if (match.Count > 1)
                        {
                            var perUnit = ParseCoins(match[1].Value);
                            price = (long)(perUnit * amount);
                            Console.WriteLine($"Found from item {price} ({(double)price / amount / 10} per unit) for {amount}x {itemName}");
                        }
                        else
                        {
                            Console.WriteLine("No price match found in " + JsonConvert.SerializeObject(lastView.Items.Take(9*3).Where(i=>i.Tag != null)) + " from " + args.msg.PlayerId);
                        }
                    }
                    else
                    {
                        Console.WriteLine("No order item found in " + JsonConvert.SerializeObject(lastView.Items));
                    }
                }
                else
                {
                    Console.WriteLine("No last view found");
                }
            }
            var order = new Offer()
            {
                Amount = amount,
                ItemName = itemName,
                PricePerUnit = (double)price / amount / 10,
                IsSell = side.HasFlag(Transaction.TransactionType.REMOVE),
                Created = args.msg.ReceivedAt,
            };
            args.currentState.BazaarOffers.Add(order);

            if (isSell)
                await AddItemTransaction(args, Transaction.TransactionType.BazaarListSell, amount, itemName);
            else
                await AddCoinTransaction(args, Transaction.TransactionType.BazaarListSell, price);


            if (args.msg.UserId == null)
                return;
            await RegisterUserEvents(args, side, amount, itemName, price, order);

            return;
        }
        if (msg.Contains("filled!"))
        {
            var parts = Regex.Match(msg, @"Your .*(Buy Order|Sell Offer) for ([\d,]+)x (.+) was filled!").Groups;
            amount = ParseInt(parts[2].Value);
            itemName = parts[3].Value;
            // find price from order
            var order = args.currentState.BazaarOffers.Where(o => o.ItemName == itemName && o.Amount == amount).FirstOrDefault();
            if (order == null)
            {
                Console.WriteLine("No order found for " + itemName + " " + amount);
                return;
            }
            order.Customers.Add(new Fill()
            {
                Amount = amount - order.Customers.Select(c => c.Amount).DefaultIfEmpty(0).Sum(),
                PlayerName = "unknown",
                TimeStamp = args.msg.ReceivedAt,
            });
            await ProduceFillEvent(args, itemName, order);
            return;
        }
        if (msg.Contains("Cancelled!"))
        {
            if (msg.Contains("coins"))
            {
                var buyParts = Regex.Match(msg, @"Refunded ([.\d,]+) coins from cancelling").Groups;
                price = ParseCoins(buyParts[1].Value);
                var buyOrder = args.currentState.BazaarOffers.Where(o => (long)(o.PricePerUnit * 10 * o.Amount) == price).FirstOrDefault();
                if (buyOrder != null)
                    args.currentState.BazaarOffers.Remove(buyOrder);
                await AddCoinTransaction(args, Transaction.TransactionType.BazaarBuy | Transaction.TransactionType.Move, price);
                return;
            }
            var parts = Regex.Match(msg, @"Refunded ([\d,]+)x (.*) from cancelling").Groups;
            amount = ParseInt(parts[1].Value);
            itemName = parts[2].Value;
            side |= Transaction.TransactionType.Move;
            // invert side
            side ^= Transaction.TransactionType.RECEIVE ^ Transaction.TransactionType.REMOVE;

            var order = args.currentState.BazaarOffers.Where(o => o.ItemName == itemName && o.Amount == amount).FirstOrDefault();
            if (order != null)
                args.currentState.BazaarOffers.Remove(order);
            else
                Console.WriteLine("No order found for " + itemName + " " + amount + " to cancel " + JsonConvert.SerializeObject(args.currentState.BazaarOffers));
            await AddItemTransaction(args, side, amount, itemName);
            return;
        }
        if (msg.Contains("Claimed "))
        {
            var isBuy = msg.Contains("bought");
            if (isBuy)
            {
                Console.WriteLine("Claimed buy order");
                var parts = Regex.Match(msg, @"Claimed ([.\d,]+)x (.*) worth ([.\d,]+) coins bought for ([.\d,]+) each").Groups;
                amount = ParseInt(parts[1].Value);
                itemName = parts[2].Value;
                price = ParseCoins(parts[3].Value);
                side |= Transaction.TransactionType.RECEIVE;
                var perPrice = ParseCoins(parts[4].Value);
                await AddItemTransaction(args, side | Transaction.TransactionType.Move, amount, itemName);
                var order = args.currentState.BazaarOffers.Where(o => o.ItemName == itemName && o.Amount == amount && o.PricePerUnit == perPrice).FirstOrDefault();
                if (order == null)
                {
                    Console.WriteLine("No order found for " + itemName + " " + amount);
                    return;
                }
                args.currentState.BazaarOffers.Remove(order);
                await ProduceFillEvent(args, itemName, order);
            }
            else
            {
                var parts = Regex.Match(msg, @"Claimed ([.\d,]+) coins from (.*) ([\d,]+)x (.*) at ").Groups;
                amount = ParseInt(parts[3].Value);
                itemName = parts[4].Value;
                price = ParseCoins(parts[1].Value);
                side |= Transaction.TransactionType.REMOVE;
                await AddCoinTransaction(args, Transaction.TransactionType.BazaarBuy | Transaction.TransactionType.Move, price);

                var order = args.currentState.BazaarOffers.Where(o => o.ItemName == itemName && o.Amount == amount).FirstOrDefault();
                if (order == null)
                {
                    Console.WriteLine("No order found for " + itemName + " " + amount);
                    return;
                }

                args.currentState.BazaarOffers.Remove(order);
                await ProduceFillEvent(args, itemName, order);
            }
        }
        if (msg.StartsWith("[Bazaar] Sold ") || msg.StartsWith("[Bazaar] Bought "))
        {
            var parts = Regex.Match(msg, @"(Sold|Bought) ([\d,]+)x (.*) for ([\d,]+)").Groups;
            var isSold = parts[1].Value == "Sold";
            amount = ParseInt(parts[2].Value);
            itemName = parts[3].Value;
            price = ParseCoins(parts[4].Value);
            if (isSold)
                side |= Transaction.TransactionType.REMOVE;
            else
                side |= Transaction.TransactionType.RECEIVE;
        }

        if (side == Transaction.TransactionType.BAZAAR)
            return; // no order affecting message
        if (itemName == null)
            throw new ArgumentNullException(nameof(itemName), $"in {msg} no item name was found");
        var itemTransactionTask = AddItemTransaction(args, side, amount, itemName);
        if (price != 0)
        {
            await AddCoinTransaction(args, InvertSide(side), price);
        }
        await itemTransactionTask;

        static async Task AddItemTransaction(UpdateArgs args, Transaction.TransactionType side, int amount, string itemName)
        {
            var itemApi = args.GetService<IItemsApi>();
            var itemId = await itemApi.ItemsSearchTermIdGetAsync(itemName);
            var mainTransaction = new Transaction()
            {
                Amount = amount,
                ItemId = itemId,
                PlayerUuid = args.currentState.McInfo.Uuid,
                TimeStamp = args.msg.ReceivedAt,
                Type = side,
            };
            await args.GetService<ITransactionService>().AddTransactions(mainTransaction);
        }
    }

    private static async Task RegisterUserEvents(UpdateArgs args, Transaction.TransactionType side, int amount, string itemName, long price, Offer order)
    {
        var scheduleApi = args.GetService<IScheduleApi>();
        await scheduleApi.ScheduleUserIdPostAsync(args.msg.UserId, DateTime.UtcNow + TimeSpan.FromDays(7), new()
        {
            Summary = "Bazaar order expired",
            Message = $"Your bazaar order for {itemName} expired",
            Reference = BazaarListener.OrderKey(order),
            SourceType = "BazaarExpire",
            SourceSubId = itemName
        });

        try
        {
            var orderBookApi = args.GetService<IOrderBookApi>();
            var itemApi = args.GetService<IItemsApi>();
            var searchResult = await itemApi.ItemsSearchTermGetAsync(itemName);
            var tag = searchResult.First().Tag;
            await orderBookApi.AddOrderAsync(new()
            {
                Amount = amount,
                UserId = args.msg.UserId,
                ItemId = tag,
                PricePerUnit = (double)price / amount / 10,
                IsSell = side.HasFlag(Transaction.TransactionType.REMOVE),
                Timestamp = args.msg.ReceivedAt,
                PlayerName = args.currentState.McInfo.Name
            });
            args.GetService<ILogger<BazaarOrderListener>>().LogInformation("Added order to order book for {user} {item} {amount} {price}", args.currentState.McInfo.Name, tag, amount, price);
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error adding order to order book");
        }
    }

    private static async Task ProduceFillEvent(UpdateArgs args, string itemName, Offer order)
    {
        try
        {
            var orderApi = args.GetService<IOrderBookApi>();
            var itemApi = args.GetService<IItemsApi>();
            var searchResult = await itemApi.ItemsSearchTermGetAsync(itemName);
            var tag = searchResult.First().Tag;
            await orderApi.RemoveOrderAsync(tag, args.msg.UserId, order.Created);
            args.GetService<ILogger<BazaarOrderListener>>().LogInformation("Removed order from order book for {user} {item} {amount} {price}", args.currentState.McInfo.Name, tag, order.Amount, order.PricePerUnit);
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error removing order from order book");
        }
    }

    private static int ParseInt(string value)
    {
        return int.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
    }

    private static long ParseCoins(string value)
    {
        return (long)(double.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture) * 10);
    }

    private static async Task AddCoinTransaction(UpdateArgs args, Transaction.TransactionType side, double price)
    {
        var coinTransaction = new Transaction()
        {
            Amount = (int)(price),
            ItemId = TradeDetect.IdForCoins,
            PlayerUuid = args.currentState.McInfo.Uuid,
            TimeStamp = args.msg.ReceivedAt,
            Type = side,
        };
        await args.GetService<ITransactionService>().AddTransactions(coinTransaction);
    }

    private static Transaction.TransactionType InvertSide(Transaction.TransactionType side)
    {
        return side ^ Transaction.TransactionType.RECEIVE ^ Transaction.TransactionType.REMOVE;
    }
}
