using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Core;
using Item = Coflnet.Sky.Core.Item;
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;
public class TradeInfoListener : UpdateListener
{
    public ILogger<TradeInfoListener> logger;

    public TradeInfoListener(ILogger<TradeInfoListener> logger)
    {
        this.logger = logger;
    }

    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.ReceivedAt < DateTime.UtcNow.AddSeconds(-30))
            return; // ignore old messages
        var chest = args.msg.Chest;
        if (chest.Name == null || !chest.Name.StartsWith("You                  "))
            return; // not a trade

        var previousChest = args.currentState.RecentViews.Where(t => t.Name?.StartsWith("You    ") ?? false).Reverse().Skip(1).Take(1).LastOrDefault();
        TradeDetect.ParseTradeWindow(chest, out _, out var received);
        TradeDetect.ParseTradeWindow(previousChest, out _, out var previousReceived);

        var newItems = received.Where(r => !previousReceived.Any(p => p.ItemName == r.ItemName && p.Count == r.Count)).ToList();
        var prices = await args.GetService<SniperService>().GetPrices(newItems);
        logger.LogInformation("Found " + newItems.Count + " new items in trade window {current} {previous}",
            JsonConvert.SerializeObject(received), JsonConvert.SerializeObject(previousReceived));
        for (int i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            var price = prices[i];
            args.SendMessage($"Item: {item.ItemName} Count: {item.Count} showed up in trade window");
            var uid = price?.Lbin?.AuctionId ?? 0;
            if (uid == 0)
            {
                args.SendMessage("No lbin found");
                continue;
            }
            var lbin = await GetAuction(args, uid);
            if (lbin == null)
            {
                args.SendMessage("Most similar lbin not found");
            }
            else
            {
                args.SendMessage($"Lbin is {price!.Lbin.Price:N0} - click to open on ah", $"/viewauction {lbin?.Uuid}");
                if (price?.SLbin != null && price.SLbin.AuctionId != 0)
                {
                    var slbin = await GetAuction(args, price.SLbin.AuctionId);
                    if (slbin != null)
                    {
                        args.SendMessage($"Second lowest BIN is {price.SLbin.Price} - click to open on ah", $"/viewauction {slbin?.Uuid}");
                    }
                }
            }
        }
    }

    private static async Task<SaveAuction?> GetAuction(UpdateArgs args, long uid)
    {
        var auctionClient = args.GetService<Sky.Api.Client.Api.IAuctionsApi>();
        var auction = await auctionClient.ApiAuctionAuctionUuidGetWithHttpInfoAsync(AuctionService.Instance.GetUuid(uid));
        return JsonConvert.DeserializeObject<SaveAuction>(auction.RawContent);
    }
}
