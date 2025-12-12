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
