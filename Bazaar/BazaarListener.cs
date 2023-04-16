using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarListener : UpdateListener
{
    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Your Bazaar Orders")
            return Task.CompletedTask;
        var offers = new List<Offer>();
        foreach (var item in args.msg.Chest.Items)
        {
            if (string.IsNullOrWhiteSpace(item?.Description) || string.IsNullOrWhiteSpace(item.ItemName))
                continue;
            if (item.ItemName.Contains("Go Back"))
                break;

            try
            {
                Offer offer = ParseOffer(item);
                offers.Add(offer);
            }
            catch (Exception e)
            {
                if(args.currentState.PlayerId == null)
                    throw; // for test
                args.GetService<ILogger<BazaarListener>>().LogError(e, "Error parsing bazaar offer: {0}", JsonConvert.SerializeObject(item));
            }
        }
        Console.WriteLine($"Found {offers.Count} bazaar offers for {args.currentState.PlayerId}");
        args.currentState.BazaarOffers = offers;
        return Task.CompletedTask;
    }

    private static Offer ParseOffer(Item item)
    {
        var parts = item.Description.Split("\n");

        var amount = parts.Where(p => p.StartsWith("§7Offer amount: §a")).First().Split("§7Offer amount: §a").Last().Split("§").First();
        var pricePerUnit = parts.Where(p => p.StartsWith("§7Price per unit: §6")).First().Split("§7Price per unit: §6").Last().Split(" coins").First();

        var offer = new Offer()
        {
            IsSell = item.ItemName.StartsWith("§6§lSELL"),
            ItemTag = item.Tag,
            Amount = int.Parse(amount, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture),
            PricePerUnit = double.Parse(pricePerUnit, System.Globalization.CultureInfo.InvariantCulture),
            ItemName = item.ItemName.Substring("§6§lSELL ".Length),
            Created = item.Description.Contains("Expired") ? default : DateTime.Now,
            Customers = parts.Where(p => p.StartsWith("§8- §a")).Select(p => new Fill()
            {
                Amount = int.Parse(p.Split("§8- §a").Last().Split("§7x").First()),
                PlayerName = p.Split("§8- §a").Last().Split("§7x").Last().Split("§f §8").First().Trim(),
                TimeStamp = DateTime.Now
            }).ToList()
        };
        return offer;
    }
}