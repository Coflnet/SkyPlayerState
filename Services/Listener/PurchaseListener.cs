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
    public override Task Process(UpdateArgs args)
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
        }
        return Task.CompletedTask;
    }
}
