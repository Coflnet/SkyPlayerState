using System.Threading.Tasks;
using Coflnet.Sky.PlayerName.Client.Api;
using System.Linq;
using System.Diagnostics;
using Coflnet.Sky.Api.Client.Api;
using RestSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Tries to find new listings from AH Browser
/// </summary>
public class AhBrowserListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest.Name == null || !args.msg.Chest.Name.Contains("Auction"))
            return;
        foreach (var item in args.msg.Chest.Items)
        {
            if (item?.Description == null)
                continue;
            if (item.Description.Contains("cancelled by admin"))
            {
                if (await Whiped(args, item))
                    return;
            }
            if (item.Description.Contains("05h 59m 5") || item.Description.Contains("Can buy in"))
            {
                if (item.Description.Contains("Refreshing"))
                    Console.WriteLine("found listing with no username: " + item.ItemName);
                var sellerName = item.Description.Split('\n')
                        .Where(x => x.StartsWith("§7Seller:"))
                        .FirstOrDefault()?.Replace("§7Seller: §7", "")
                        .Split(' ').Last(); // skip rank prefix
                if (sellerName == null)
                {
                    Console.WriteLine("found listing with no username: " + item.Description);
                    continue;
                }
                var nameService = args.GetService<IPlayerNameApi>();
                // var uuid = await nameService.PlayerNameUuidNameGetAsync(sellerName);
                // Console.WriteLine("Checking listings for " + sellerName + " uuid " + uuid + " " + args.msg.Chest.Name);
                // await args.GetService<IBaseApi>().BaseAhPlayerIdPostAsync(uuid, $"player: {args.msg.PlayerId}");
            }
            Activity.Current?.AddTag("name", args.msg.Chest.Name);
            if (item.Description.Contains("Sold for"))
            {
                var parts = item.Description.Split('\n');
                Console.WriteLine($"Item from {parts.Where(x => x.StartsWith("§7Seller:")).FirstOrDefault()?.Replace("§7Seller: ", "")} sold to: "
                        + parts.Where(x => x.StartsWith("§7Buyer:")).FirstOrDefault()?.Replace("§7Buyer: ", ""));
            }
        }
    }

    private static async Task<bool> Whiped(UpdateArgs args, Item item)
    {
        var logger = args.GetService<ILogger<AhBrowserListener>>();
        var restClient = new RestClient(args.GetService<IConfiguration>().GetValue<string>("INDEXER_BASE_URL") ?? throw new Exception("INDEXER_BASE_URL not set"));
        var playerApi = args.GetService<IPlayerApi>();
        var auctionApi = args.GetService<IAuctionsApi>();
        var nameService = args.GetService<IPlayerNameApi>();
        var sellerName = item.Description.Split('\n')
                                .Where(x => x.StartsWith("§7Seller:"))
                                .FirstOrDefault()?.Replace("§7Seller: §7", "")
                                .Split(' ').Last(); // skip rank prefix
        if (sellerName == null)
        {
            logger.LogWarning("found listing with no username: " + item.Description);
            return false;
        }
        var uuid = (await nameService.PlayerNameUuidNameGetAsync(sellerName)).Trim('"');
        if (uuid == null)
        {
            logger.LogWarning("Could not find uuid for " + sellerName);
            return false;
        }
        logger.LogInformation("Found whipe for {name} {uuid} ", sellerName, uuid);
        var startingBid = item.Description.Split('\n')
                                .Where(x => x.StartsWith("§7Buy it now:"))
                                .FirstOrDefault()?.Replace("§7Buy it now: §6", "").Replace(",", "");
        var auctions = await playerApi.ApiPlayerPlayerUuidAuctionsGetAsync(uuid, 0, new Dictionary<string, string> { { "StartingBid", startingBid }, { "EndAfter", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() } });
        var matchingAuctionId = auctions.FirstOrDefault()?.AuctionId;
        if (matchingAuctionId == null)
        {
            logger.LogWarning("Could not find matching auction for " + sellerName + " " + startingBid + " in " + item.Description);
            return true;
        }
        var auction = await auctionApi.ApiAuctionAuctionUuidGetAsync(matchingAuctionId);
        if (auction == null)
        {
            logger.LogWarning("Could not find auction for " + matchingAuctionId);
            return false;
        }
        var request = new RestRequest($"player/{auction.AuctioneerId}/{auction.ProfileId ?? "null"}/whiped", Method.Patch);
        var response = restClient.Execute(request);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            logger.LogWarning("Failed to whipe auction " + matchingAuctionId + " " + response.StatusCode + response.Content + restClient.BuildUri(request));
            return false;
        }
        logger.LogInformation("Whiped auction " + matchingAuctionId);
        return true;
    }
}
