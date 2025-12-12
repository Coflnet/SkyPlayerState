using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarListener : UpdateListener
{
    /// <summary>
    /// Tracks buy orders that vanish between chest updates.
    /// When chest GUI shows orders have been removed, we store them here
    /// so Order Flip messages can recover the buy price.
    /// Key: (PlayerUuid, ItemName, Amount)
    /// Value: (BuyPrice, VanishTime)
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, string, int), (long, DateTime)> 
        _vanishingOrders = new();

    public override async Task Process(UpdateArgs args)
    {
        if (args.currentState.Settings?.DisableBazaarTracking ?? false)
            return;
        if (args.msg.Chest?.Name != "Your Bazaar Orders" && args.msg.Chest?.Name != "Co-op Bazaar Orders")
            return;
        var offers = new List<Offer>();
        // only the first 5 rows (x9) are potential orders (to include bazaar upgrade)
        var bazaarItems = args.msg.Chest.Items.Take(45);
        var orderLookup = args.currentState.BazaarOffers.ToLookup(OrderKey, o => o);
        foreach (var item in bazaarItems)
        {
            if (string.IsNullOrWhiteSpace(item?.Description)
                || string.IsNullOrWhiteSpace(item.ItemName)
                || !item.Description.Contains("§7Price per unit: §6"))
                continue;
            if (item.ItemName.Contains("Go Back"))
                break;

            try
            {
                Offer offer = ParseOfferFromItem(item);
                var key = OrderKey(offer);
                if (orderLookup.Contains(key))
                {
                    var existing = orderLookup[key].OrderByDescending(o =>
                        (o.Customers.FirstOrDefault()?.PlayerName == offer.Customers.FirstOrDefault()?.PlayerName ? 10 : 0) - Math.Abs(o.Customers.Count - offer.Customers.Count))
                        .First();
                    offer.Created = existing.Created;
                    // update customer timestamps
                    foreach (var customer in offer.Customers)
                    {
                        var existingCustomer = existing.Customers.FirstOrDefault(c => c.PlayerName == customer.PlayerName);
                        if (existingCustomer != null)
                        {
                            customer.TimeStamp = existingCustomer.TimeStamp;
                        }
                    }
                }
                else
                {
                    offer.Created = args.msg.ReceivedAt;
                }
                offers.Add(offer);
            }
            catch (Exception e)
            {
                if (args.currentState.PlayerId == null)
                    throw; // for test
                args.GetService<ILogger<BazaarListener>>()
                    .LogError(e, "Error parsing bazaar offer: {0} {chest} {user}", JsonConvert.SerializeObject(item), args.msg.Chest.Name, args.currentState.PlayerId);
            }
        }
        Console.WriteLine($"Found {offers.Count} bazaar offers for {args.currentState.PlayerId}");
        
        // Track vanishing buy orders before replacing state
        TrackVanishingOrders(args, offers);
        
        args.currentState.BazaarOffers = offers;

        if (orderLookup.SelectMany(o => o).Count() == offers.Count)
            return;
        // order count changed update notifications
        try
        {
            var service = args.GetService<IScheduleApi>();
            var currentLookup = offers.ToLookup(OrderKey, o => o);
            if (args.msg.UserId == null)
                return; // not logged in, no notifications
            var notifications = await service.ScheduleUserIdGetAsync(args.msg.UserId);
            var bazaarNotifications = notifications.Where(n => n?.Message?.SourceType?.StartsWith("BazaarExpire") ?? false).ToList();
            args.GetService<ILogger<BazaarListener>>()
                .LogInformation("Found {count} bazaar notifications from {totalNotifications} for {user}", bazaarNotifications.Count, notifications.Count, args.msg.UserId);
            foreach (var notification in bazaarNotifications)
            {
                if (currentLookup.Contains(notification.Message.Reference))
                    continue;
                await service.ScheduleUserIdIdDeleteAsync(args.msg.UserId, notification.Id);
                args.GetService<ILogger<BazaarListener>>().LogInformation("Removed bazaar notification {id} for {playername}", notification.Id, args.currentState.PlayerId);
            }
        }
        catch (Exception e)
        {
            args.GetService<ILogger<BazaarListener>>().LogError(e, "Error updating bazaar notifications");
        }
    }

    /// <summary>
    /// Tracks buy orders that are vanishing (present in old state but not in new offers)
    /// </summary>
    private void TrackVanishingOrders(UpdateArgs args, List<Offer> newOffers)
    {
        var playerUuid = args.currentState.McInfo?.Uuid;
        if (playerUuid == null || playerUuid == Guid.Empty)
            return;

        var newOffersLookup = newOffers.ToHashSet(new OfferComparer());
        var vanishingBuyOrders = args.currentState.BazaarOffers
            .Where(o => !o.IsSell && !newOffersLookup.Contains(o));

        foreach (var order in vanishingBuyOrders)
        {
            var key = (playerUuid.Value, order.ItemName, (int)order.Amount);
            // Store total buy price in tenths of coins (price per unit * amount * 10)
            // This matches the format used in RecordOrderFlip and normal order processing
            var totalBuyPriceInTenths = (long)(order.PricePerUnit * order.Amount * 10);
            _vanishingOrders[key] = (totalBuyPriceInTenths, DateTime.UtcNow);
        }

        // Cleanup old entries (older than 2 minutes)
        if (_vanishingOrders.Count > 1000)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var oldKeys = _vanishingOrders.Where(kvp => kvp.Value.Item2 < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in oldKeys)
            {
                _vanishingOrders.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Gets a vanishing buy order from the cache
    /// </summary>
    /// <param name="playerUuid">The player's UUID</param>
    /// <param name="itemName">The item name</param>
    /// <param name="amount">The amount of items</param>
    /// <param name="buyPrice">Total buy price in tenths of coins (price * amount * 10)</param>
    /// <returns>True if order found and not expired</returns>
    public static bool TryGetVanishingOrder(Guid playerUuid, string itemName, int amount, out long buyPrice)
    {
        buyPrice = 0;
        if (_vanishingOrders.TryGetValue((playerUuid, itemName, amount), out var cached))
        {
            // Check if not too old (2 minutes max)
            if ((DateTime.UtcNow - cached.Item2).TotalMinutes < 2)
            {
                buyPrice = cached.Item1;
                return true;
            }
        }
        return false;
    }

    private class OfferComparer : IEqualityComparer<Offer>
    {
        public bool Equals(Offer? x, Offer? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.ItemName == y.ItemName && x.Amount == y.Amount && x.IsSell == y.IsSell;
        }

        public int GetHashCode(Offer obj)
        {
            return HashCode.Combine(obj.ItemName, obj.Amount, obj.IsSell);
        }
    }

    /// <summary>
    /// Creates a key for the offer to be able to compare them
    /// capped at 32 characters because the eventbroker doesn't allow more
    /// </summary>
    /// <param name="o"></param>
    /// <returns></returns>
    public static string OrderKey(Offer o)
    {
        return ((o.IsSell ? "s" : "b") + o.Amount + o.PricePerUnit + Regex.Replace(o.ItemName, "(§.)*", "")).Truncate(32);
    }

    /// <summary>
    /// Parses a bazaar offer from a chest item
    /// </summary>
    public static Offer ParseOfferFromItem(Models.Item item)
    {
        var parts = item.Description!.Split("\n");

        var amount = parts.Where(p => p.Contains("amount: §a")).First().Split("amount: §a").Last().Split("§").First();
        var pricePerUnit = parts.Where(p => p.StartsWith("§7Price per unit: §6")).First().Split("§7Price per unit: §6").Last().Split(" coins").First();
        var customers = parts.Where(p => p.StartsWith("§8- §a")).Select(p => new Fill()
        {
            Amount = ParseInt(p.Split("§8- §a").Last().Split("§7x").First()),
            PlayerName = p.Split("§8- §a").Last().Split("§7x").Last().Split("§f §8").First().Trim(),
            TimeStamp = DateTime.Now
        }).ToList();

        var offer = new Offer()
        {
            IsSell = item.ItemName!.Contains("SELL"),
            ItemTag = item.Tag,
            Amount = ParseInt(amount),
            PricePerUnit = double.Parse(pricePerUnit, System.Globalization.CultureInfo.InvariantCulture),
            ItemName = item.ItemName.Substring("§6§lSELL ".Length),
            Created = item.Description.Contains("Expired") ? default : DateTime.Now,
            Customers = customers
        };
        return offer;
    }

    private static int ParseInt(string amount)
    {
        return int.Parse(amount, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
    }
}
