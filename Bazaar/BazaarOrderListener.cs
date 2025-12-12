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
    /// <summary>
    /// In-memory cache to bridge the race condition gap between chest GUI updates and chat messages.
    /// Tracks recently flipped orders (item + amount + buy price) for 60 seconds.
    /// When a flip message arrives but the buy order is already removed from state,
    /// we can retrieve the buy price from this cache to create the virtual buy record.
    /// Key: (PlayerUuid, ItemName, Amount)
    /// Value: (BuyPrice, FlipTime)
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, string, int), (long, DateTime)> 
        _recentFlips = new();

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
            var match = Regex.Match(msg, @"([\d,]+)x\s+(.+?)\s+for\s+([\d\.,kKmM]+)\s+coins", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                Console.WriteLine("No setup match found for: " + msg);
                return;
            }
            var parts = match.Groups;
            var amountStr = parts[1].Value;
            if (string.IsNullOrWhiteSpace(amountStr))
            {
                Console.WriteLine("No amount captured in setup: " + msg);
                return;
            }
            amount = ParseInt(amountStr);
            itemName = parts[2].Value;
            price = ParseCoins(parts[3].Value);
            if (isSell)
            {
                price = (long)(price / (1 - 0.01125) - 0.1); // include tax (estimate)
                Console.WriteLine($"Adjusted price for tax {price} ({(double)price / amount / 10} per unit) for {amount}x {itemName}");
            }
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
                        var itemMatch = Regex.Match(item.Description!, @"Price per unit: §6([\d,]+\.?\d*) coins").Groups;
                        if (itemMatch.Count > 1)
                        {
                            var perUnit = ParseCoins(itemMatch[1].Value);
                            price = (long)(perUnit * amount);
                            Console.WriteLine($"Found from item {price} ({(double)price / amount / 10} per unit) for {amount}x {itemName}");
                        }
                        else
                        {
                            Console.WriteLine("No price match found in " + JsonConvert.SerializeObject(lastView.Items.Take(9 * 3).Where(i => i.Tag != null)) + " from " + args.msg.PlayerId);
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
                PricePerUnit = Math.Round((double)price / amount) / 10,
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
                var perPriceInCoins = perPrice / 10.0; // Convert from tenths to coins for comparison with PricePerUnit
                await AddItemTransaction(args, side | Transaction.TransactionType.Move, amount, itemName);
                var order = args.currentState.BazaarOffers.Where(o => o.ItemName == itemName && o.Amount == amount && o.PricePerUnit == perPriceInCoins).FirstOrDefault();

                // Track buy order for profit calculation
                await RecordBuyOrderForProfit(args, itemName, amount, price);
                
                // Cache this buy order for potential flip (race condition mitigation)
                // Store: PlayerUuid, ItemName, Amount -> (BuyPrice, FillTime)
                var playerUuid = args.currentState.McInfo.Uuid;
                var cacheKey = (playerUuid, itemName, amount);
                _recentFlips[cacheKey] = (price, args.msg.ReceivedAt);
                
                // Cleanup: Remove entries older than 60 seconds (only if cache is getting large)
                if (_recentFlips.Count > 1000)
                {
                    var cutoff = args.msg.ReceivedAt.AddSeconds(-60);
                    var expiredKeys = _recentFlips.Where(kvp => kvp.Value.Item2 < cutoff).Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys)
                    {
                        _recentFlips.TryRemove(key, out _);
                    }
                }
                
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
                var parts = Regex.Match(msg, @"Claimed ([\.\d,]+) coins from (.*) ([\d,]+)x (.*) at ").Groups;
                amount = ParseInt(parts[3].Value);
                itemName = parts[4].Value;
                price = ParseCoins(parts[1].Value);
                side |= Transaction.TransactionType.REMOVE;
                await AddCoinTransaction(args, Transaction.TransactionType.BazaarBuy | Transaction.TransactionType.Move, price);

                // Track sell order and calculate profit
                await RecordSellOrderForProfit(args, itemName, amount, price);
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
        if (msg.Contains("Order Flipped!"))
        {
            // Direct order flip: [Bazaar] Order Flipped! 64x Ancient Claw for 16,915 coins of total expected profit.
            // A flip is: Setup Buy -> Flip -> Claim Sell
            // The flip acts as a virtual "Claim Buy" + "Setup Sell", so we need to record the buy order
            // from the original setup so the later sell claim can calculate profit
            //
            // IMPORTANT: The "expected profit" in the flip message is Hypixel's ESTIMATE based on the
            // listed sell price BEFORE tax/fees. The ACTUAL profit calculated when claiming the sell
            // order will be lower due to:
            // - Bazaar sell tax (~1.125%)
            // - Rounding differences
            // - Price changes/slippage between flip and claim
            // - Partial fills at different prices
            //
            // We record the expected profit for logging, but the actual profit saved to the database
            // is calculated from real transaction amounts (sell claim - buy price).
            var parts = Regex.Match(msg, @"Order Flipped! ([\d,]+)x (.*) for ([\d,\.]+) coins of total expected profit").Groups;
            if (parts.Count > 3)
            {
                amount = ParseInt(parts[1].Value);
                itemName = parts[2].Value;
                var expectedProfit = ParseCoins(parts[3].Value);

                // Find the original buy order to get the buy price
                var buyOrder = args.currentState.BazaarOffers
                    .Where(o => o.ItemName == itemName && o.Amount == amount && !o.IsSell)
                    .FirstOrDefault();

                if (buyOrder != null)
                {
                    // Calculate total buy price from the order
                    var buyPrice = (long)(buyOrder.PricePerUnit * buyOrder.Amount * 10); // Convert to tenths

                    // Record the virtual buy order (so sell claim can match against it)
                    await RecordOrderFlipForProfit(args, itemName, amount, buyPrice, expectedProfit);

                    // Remove the buy order and add a sell order (the flip creates a sell order)
                    args.currentState.BazaarOffers.Remove(buyOrder);

                    // Add a sell order to state - the sell price can be calculated from buy + expected profit
                    var sellPrice = buyPrice + expectedProfit;
                    var sellOrder = new Offer
                    {
                        Amount = amount,
                        ItemName = itemName,
                        PricePerUnit = Math.Round((double)sellPrice / amount) / 10,
                        IsSell = true,
                        Created = args.msg.ReceivedAt
                    };
                    args.currentState.BazaarOffers.Add(sellOrder);
                    args.GetService<ILogger<BazaarOrderListener>>()
                        .LogInformation("Processed flipped order for {user}: {amount}x {item} bought at {buyPrice} for expected profit {expectedProfit}, created sell order at {sellPrice}",
                            args.currentState.McInfo.Name, amount, itemName, buyPrice, expectedProfit, sellPrice);

                    await ProduceFillEvent(args, itemName, buyOrder);
                }
                else
                {
                    // RACE CONDITION: Chest GUI update removed buy order before flip message arrived
                    // Check in-memory cache for recently filled buy orders
                    var playerUuid = args.currentState.McInfo.Uuid;
                    var cacheKey = (playerUuid, itemName, amount);
                    if (_recentFlips.TryGetValue(cacheKey, out var cachedData))
                    {
                        var (buyPrice, fillTime) = cachedData;
                        
                        // Verify the cached data isn't too old (max 60 seconds)
                        if ((args.msg.ReceivedAt - fillTime).TotalSeconds <= 60)
                        {
                            Console.WriteLine($"Found cached buy order for flip: {amount}x {itemName} (filled {(args.msg.ReceivedAt - fillTime).TotalSeconds:F1}s ago)");
                            
                            // Record the virtual buy order using cached buy price
                            await RecordOrderFlipForProfit(args, itemName, amount, buyPrice, expectedProfit);

                            // Add sell order to state (same as normal flip)
                            var sellPrice = buyPrice + expectedProfit;
                            var sellOrder = new Offer
                            {
                                Amount = amount,
                                ItemName = itemName,
                                PricePerUnit = Math.Round((double)sellPrice / amount) / 10,
                                IsSell = true,
                                Created = args.msg.ReceivedAt
                            };
                            args.currentState.BazaarOffers.Add(sellOrder);
                            
                            // Clean up cache entry (already used)
                            _recentFlips.TryRemove(cacheKey, out _);
                        }
                        else
                        {
                            Console.WriteLine($"Cached buy order for {amount}x {itemName} is too old ({(args.msg.ReceivedAt - fillTime).TotalSeconds:F1}s), ignoring");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No buy order found for flip: {amount}x {itemName}");
                    }
                }
            }
            return;
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
            string tag = await GetTagForName(args, itemName);
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
            string tag = await GetTagForName(args, itemName);
            await orderApi.RemoveOrderAsync(tag, args.msg.UserId, order.Created);
            args.GetService<ILogger<BazaarOrderListener>>().LogInformation("Removed order from order book for {user} {item} {amount} {price}", args.currentState.McInfo.Name, tag, order.Amount, order.PricePerUnit);
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error removing order from order book");
        }
    }

    private static async Task<string> GetTagForName(UpdateArgs args, string itemName)
    {
        var itemApi = args.GetService<IItemsApi>();
        var searchResult = await itemApi.ItemsSearchTermGetAsync(itemName);
        var tag = searchResult.OrderByDescending(s => (itemName.Equals(s.Text ?? "", StringComparison.OrdinalIgnoreCase) ? 2 : 0) 
            + (s.Flags.HasValue && s.Flags.Value.HasFlag(Items.Client.Model.ItemFlags.BAZAAR) ? 1 :0)).First().Tag;
        return tag;
    }

    private static async Task RecordBuyOrderForProfit(UpdateArgs args, string itemName, int amount, long price)
    {
        try
        {
            var profitTracker = args.GetService<IBazaarProfitTracker>();
            string tag = await GetTagForName(args, itemName);
            var playerUuid = args.currentState.McInfo.Uuid;
            await profitTracker.RecordBuyOrder(playerUuid, tag, amount, price, args.msg.ReceivedAt);
            args.GetService<ILogger<BazaarOrderListener>>().LogInformation(
                "Recorded bazaar buy order for {player}: {amount}x {item} at {price} coins",
                playerUuid, amount, itemName, price / 10.0);
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error recording buy order for profit tracking");
        }
    }

    private static async Task RecordSellOrderForProfit(UpdateArgs args, string itemName, int amount, long price)
    {
        try
        {
            var profitTracker = args.GetService<IBazaarProfitTracker>();
            string tag = await GetTagForName(args, itemName);
            var playerUuid = args.currentState.McInfo.Uuid;
            var flip = await profitTracker.RecordSellOrder(playerUuid, tag, itemName, amount, price, args.msg.ReceivedAt);
            if (flip != null)
            {
                args.GetService<ILogger<BazaarOrderListener>>().LogInformation(
                    "Recorded bazaar flip for {player}: {amount}x {item}, profit: {profit} coins",
                    args.currentState.McInfo.Name, flip.Amount, flip.ItemName, flip.Profit / 10.0);
            } else
            {
                args.GetService<ILogger<BazaarOrderListener>>().LogInformation(
                    "Recorded bazaar sell order for {player}: {amount}x {item} at {price} coins",
                    args.currentState.McInfo.Name, amount, itemName, price / 10.0);
            }
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error recording sell order for profit tracking");
        }
    }

    private static async Task RecordOrderFlipForProfit(UpdateArgs args, string itemName, int amount, long buyPrice, long expectedProfit)
    {
        try
        {
            var profitTracker = args.GetService<IBazaarProfitTracker>();
            string tag = await GetTagForName(args, itemName);
            var playerUuid = args.currentState.McInfo.Uuid;
            await profitTracker.RecordOrderFlip(playerUuid, tag, amount, buyPrice, expectedProfit, args.msg.ReceivedAt);
            args.GetService<ILogger<BazaarOrderListener>>().LogInformation(
                "Recorded virtual buy order from flip for {player}: {amount}x {item}, buy price: {buyPrice} coins, expected profit: {profit} coins",
                args.currentState.McInfo.Name, amount, itemName, buyPrice / 10.0, expectedProfit / 10.0);
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarOrderListener>>().LogError(e, "Error recording order flip for profit tracking");
        }
    }

    private static int ParseInt(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            var v = value.Trim();
            var last = char.ToLowerInvariant(v[v.Length - 1]);
            if (last == 'k' || last == 'm')
            {
                var numberPart = v.Substring(0, v.Length - 1).Replace(",", "").Trim();
                if (double.TryParse(numberPart, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
                {
                    var factor = last == 'k' ? 1_000d : 1_000_000d;
                    return (int)(d * factor);
                }
                return 0;
            }
            var cleaned = v.Replace(",", "").Trim();
            if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                return result;
            if (double.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d2))
                return (int)d2;
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long ParseCoins(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            var v = value.Trim();
            var last = char.ToLowerInvariant(v[v.Length - 1]);
            if (last == 'k' || last == 'm')
            {
                var numberPart = v.Substring(0, v.Length - 1).Replace(",", "").Trim();
                if (double.TryParse(numberPart, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    var factor = last == 'k' ? 1_000d : 1_000_000d;
                    return (long)(d * factor * 10);
                }
                return 0;
            }
            var cleaned = v.Replace(",", "").Trim();
            if (double.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var d2))
                return (long)(d2 * 10);
            return 0;
        }
        catch
        {
            return 0;
        }
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
