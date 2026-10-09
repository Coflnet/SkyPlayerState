using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Detects Auction House/Bazaar purchases from chat (<see cref="PurchaseParser"/>) and records the
/// coins spent as <see cref="Tasks.PseudoItems"/> evidence tags on <see cref="ExtractedInfo"/>'s
/// current collection window, the exact same way a real item drop is tracked - see
/// <see cref="StateObject.ItemsCollectedRecently"/>. A Bazaar/Auction House purchase otherwise shows
/// up as a "collected" item (the bought item lands in the inventory) with no signal that it was
/// BOUGHT rather than earned, which is most of why ~59% of production coin value went unclassified -
/// see the class docs on <see cref="Tasks.PseudoItems"/>/<c>Tasks.HiddenTasks</c>.
/// </summary>
public class PurchaseListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        foreach (var chatMsg in args.msg.ChatBatch ?? [])
        {
            if (!PurchaseParser.TryParse(chatMsg, out var kind, out var coins) || coins <= 0)
                continue;

            var tag = kind switch
            {
                PurchaseKind.Auction => Tasks.PseudoItems.AUCTION_PURCHASE,
                PurchaseKind.Bazaar => Tasks.PseudoItems.BAZAAR_PURCHASE,
                _ => null
            };
            if (tag == null)
                continue;

            var amount = (int)Math.Min(coins, int.MaxValue);
            var collected = args.currentState.ItemsCollectedRecently;
            Tasks.ItemCountMath.Add(collected, tag, amount);
            Logger.LogDebug("[Purchase] {playerId}: {tag} +{amount} coins spent", args.msg.PlayerId, tag, amount);
            if (kind == PurchaseKind.Bazaar)
                await DiscountBoughtItems(args, chatMsg);
            else
                DiscountAuctionItem(args, chatMsg);
        }
    }

    /// <summary>
    /// The bought items land in the inventory like a drop: take exactly the bought amount off the tracked count of the
    /// item (see <see cref="Tasks.PurchaseDiscount"/>). An item name that cannot be resolved only leaves the product
    /// page guard of <see cref="CollectionListener"/> as protection.
    /// </summary>
    private async Task DiscountBoughtItems(UpdateArgs args, string chatMsg)
    {
        if (!PurchaseParser.TryParseBazaarBuy(chatMsg, out var bought, out var itemName, out _))
            return;
        try
        {
            var itemTag = await Bazaar.BazaarOrderListener.GetTagForName(args, itemName);
            if (string.IsNullOrWhiteSpace(itemTag))
                return;
            var discounted = Tasks.PurchaseDiscount.ApplyChatPurchase(args.currentState.ItemsCollectedRecently,
                args.currentState.ExtractedInfo, itemTag, bought, args.msg.ReceivedAt);
            Logger.LogDebug("[Purchase] {playerId}: {bought}x {tag} bought, {discounted} taken off the tracked drops", args.msg.PlayerId, bought, itemTag, discounted);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Could not resolve the item of bazaar purchase {item} for {playerId}", itemName, args.msg.PlayerId);
        }
    }

    /// <summary>
    /// The auction line has a decorated name and no amount, but the auction view / confirm screen shown just before holds the
    /// exact item: find it in the last screens (<see cref="Tasks.PurchaseDiscount.TryFindAuctionItem"/>) and discount its tag and
    /// count like a bazaar purchase. No matching screen discounts nothing.
    /// </summary>
    private void DiscountAuctionItem(UpdateArgs args, string chatMsg)
    {
        if (!PurchaseParser.TryParseAuctionBuy(chatMsg, out var itemName))
            return;
        if (!Tasks.PurchaseDiscount.TryFindAuctionItem(args.currentState.RecentViews, itemName, args.msg.ReceivedAt, out var itemTag, out var count))
        {
            Logger.LogDebug("[Purchase] {playerId}: no recent screen shows auction item {item}", args.msg.PlayerId, itemName);
            return;
        }
        var discounted = Tasks.PurchaseDiscount.ApplyAuctionPurchase(args.currentState.ItemsCollectedRecently,
            args.currentState.ExtractedInfo, itemTag, count, args.msg.ReceivedAt);
        Logger.LogDebug("[Purchase] {playerId}: {count}x {tag} bought at auction, {discounted} taken off the tracked drops", args.msg.PlayerId, count, itemTag, discounted);
    }
}
