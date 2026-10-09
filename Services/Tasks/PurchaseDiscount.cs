using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Keeps items bought on the bazaar out of the drops of the activity they were bought during. A bought item lands in the
/// inventory like a drop, so two signals discount it:
/// <list type="number">
/// <item>The chat line ("[Bazaar] Bought 64x ..."/"[Bazaar] Claimed 64x ...") says exactly how many were bought:
/// <see cref="ApplyChatPurchase"/> subtracts that from the period's tracked count, never below zero. What is not tracked yet
/// (the inventory update arrives after the chat line) is remembered in <see cref="ExtractedInfo.PendingPurchaseDiscounts"/>
/// and taken off the next increases within <see cref="PendingWindow"/>.</item>
/// <item>Fallback for a missed or unresolvable chat line: while a bazaar product page of the item was open
/// within <see cref="ProductPageGuardWindow"/> (<see cref="ArmProductGuard"/>), its increases are not counted
/// (<see cref="AdjustIncrease"/>); the skipped amount is remembered so the chat line, if it comes, does not discount it again.</item>
/// </list>
/// Both can never discount the same purchase: the chat line first uses up the guard's skipped amount, and a chat line that was
/// not matched by skipped items disarms the guard (its purchase is covered by the pending discount instead).
/// </summary>
public static class PurchaseDiscount
{
    /// <summary>An item increase this soon after a bazaar product page of the same item is treated as bought, not farmed.</summary>
    public static readonly TimeSpan ProductPageGuardWindow = TimeSpan.FromSeconds(45);
    /// <summary>A purchase from chat that was not yet seen as an inventory increase waits this long for it.</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A screen older than this at the time of the auction purchase chat line is not the one the purchase was made from
    /// (view, confirm and chat line follow within seconds).
    /// </summary>
    public static readonly TimeSpan AuctionScreenMaxAge = TimeSpan.FromMinutes(2);
    /// <summary>The auction item sits in the centre of the menu part ("BIN Auction View", "Auction View", "Confirm Purchase").</summary>
    public const int AuctionItemSlot = 13;

    /// <summary>Remembers that the bazaar product page of <paramref name="tag"/> is open (or was just seen).</summary>
    public static void ArmProductGuard(ExtractedInfo info, string tag, DateTime now)
    {
        if (string.IsNullOrEmpty(tag))
            return;
        if (info.BazaarProductTag != tag || now - info.BazaarProductSeenAt > ProductPageGuardWindow)
            info.BazaarGuardSkipped = 0;
        info.BazaarProductTag = tag;
        info.BazaarProductSeenAt = now;
    }

    /// <summary>
    /// A bazaar chat purchase of <paramref name="amount"/> x <paramref name="tag"/>: discounts what is already tracked in
    /// <paramref name="collected"/> and remembers the rest for the increases still to come. Returns the amount taken off now.
    /// </summary>
    public static int ApplyChatPurchase(Dictionary<string, int> collected, ExtractedInfo info, string tag, int amount, DateTime now)
    {
        if (string.IsNullOrEmpty(tag) || amount <= 0)
            return 0;
        return Apply(collected, info, tag, amount, now, useProductGuard: true);
    }

    /// <summary>
    /// An auction purchase of <paramref name="amount"/> x <paramref name="tag"/> found by <see cref="TryFindAuctionItem"/>: same
    /// discount and pending window as <see cref="ApplyChatPurchase"/>, but it never touches the bazaar product page guard
    /// (an auction purchase is not explained by a bazaar page, and must not use up what that page skipped).
    /// </summary>
    public static int ApplyAuctionPurchase(Dictionary<string, int> collected, ExtractedInfo info, string tag, int amount, DateTime now)
    {
        if (string.IsNullOrEmpty(tag) || amount <= 0)
            return 0;
        return Apply(collected, info, tag, amount, now, useProductGuard: false);
    }

    private static int Apply(Dictionary<string, int> collected, ExtractedInfo info, string tag, int amount, DateTime now, bool useProductGuard)
    {
        var remaining = amount;
        if (useProductGuard && info.BazaarProductTag == tag && info.BazaarGuardSkipped > 0)
        {
            // the guard already kept these out of the drops
            var skipped = Math.Min(remaining, info.BazaarGuardSkipped);
            info.BazaarGuardSkipped -= skipped;
            remaining -= skipped;
        }
        if (useProductGuard && info.BazaarProductTag == tag && info.BazaarGuardSkipped == 0)
            // everything the guard skipped is explained; later increases are covered by the pending discount below
            info.BazaarProductTag = null;
        var discounted = 0;
        if (remaining > 0 && collected.TryGetValue(tag, out var tracked) && tracked > 0)
        {
            discounted = Math.Min(remaining, tracked);
            ItemCountMath.Add(collected, tag, -discounted);
            if (collected.TryGetValue(tag, out var left) && left == 0)
                collected.Remove(tag);
            remaining -= discounted;
        }
        if (remaining > 0)
        {
            ExpirePending(info, now);
            info.PendingPurchaseDiscounts ??= new();
            info.PendingPurchaseDiscounts[tag] = info.PendingPurchaseDiscounts.GetValueOrDefault(tag) + remaining;
            info.PendingPurchaseDiscountAt = now;
        }
        return discounted;
    }

    /// <summary>
    /// Finds the item an auction purchase chat line is about in the latest screens (<see cref="ExtractedInfo"/> keeps none of its
    /// own, <see cref="StateObject.RecentViews"/> already holds the last <see cref="Services.RecentViewsUpdate.MaxRecentViews"/>
    /// with full items). Newest first, the auction screens ("Auction View", "BIN Auction View", "Confirm Purchase") before any other
    /// screen; only the menu part of a screen is searched, its centre slot (<see cref="AuctionItemSlot"/>) first. Matches the item
    /// display name with colour codes stripped. Screens older than <see cref="AuctionScreenMaxAge"/> are ignored.
    /// </summary>
    public static bool TryFindAuctionItem(IEnumerable<ChestView> recentViews, string chatItemName, DateTime now, out string tag, out int count)
    {
        tag = null;
        count = 0;
        var wanted = PurchaseParser.StripFormatting(chatItemName).Trim();
        if (wanted.Length == 0 || recentViews == null)
            return false;
        var screens = recentViews
            .Where(v => v?.Items != null && (v.OpenedAt == default || now - v.OpenedAt <= AuctionScreenMaxAge))
            .Reverse().ToList();
        foreach (var auctionScreens in new[] { true, false })
        {
            foreach (var screen in screens.Where(v => IsAuctionScreen(v) == auctionScreens))
            {
                var menu = screen.Items.Take(Services.CollectionListener.AccessibleInventoryStart(screen.Items)).ToList();
                var candidates = (menu.Count > AuctionItemSlot ? new[] { menu[AuctionItemSlot] } : [])
                    .Concat(menu.Where((_, i) => i != AuctionItemSlot));
                var item = candidates.FirstOrDefault(i => i?.Tag != null && i.ItemName != null
                    && string.Equals(PurchaseParser.StripFormatting(i.ItemName).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
                item ??= FindOnConfirmScreen(screen, menu, wanted);
                if (item == null)
                    continue;
                tag = item.Tag;
                count = Math.Max(1, item.Count ?? 1);
                return true;
            }
        }
        return false;
    }

    private const string ConfirmScreenName = "Confirm Purchase";
    private const int ConfirmButtonSlot = 11;
    private const string BuyingItemName = "BUYING ITEM:";
    private const string PurchasingPrefix = "Purchasing:";

    /// <summary>
    /// The "Confirm Purchase" screen names its centre item "BUYING ITEM:" and puts the real name in the first lore line; the
    /// confirm button (slot 11) repeats it as "Purchasing: &lt;name&gt;". Returns the centre item when either says <paramref name="wanted"/>.
    /// </summary>
    private static Item FindOnConfirmScreen(ChestView screen, List<Item> menu, string wanted)
    {
        if (screen.Name != ConfirmScreenName || menu.Count <= AuctionItemSlot)
            return null;
        var item = menu[AuctionItemSlot];
        if (item?.Tag == null || PurchaseParser.StripFormatting(item.ItemName ?? "").Trim() != BuyingItemName)
            return null;
        var firstLine = LoreLines(item).FirstOrDefault();
        if (firstLine != null && string.Equals(firstLine, wanted, StringComparison.OrdinalIgnoreCase))
            return item;
        var purchasing = menu.Count > ConfirmButtonSlot ? LoreLines(menu[ConfirmButtonSlot])
            .FirstOrDefault(l => l.StartsWith(PurchasingPrefix, StringComparison.Ordinal)) : null;
        if (purchasing != null && string.Equals(purchasing.Substring(PurchasingPrefix.Length).Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            return item;
        return null;
    }

    /// <summary>Non-empty lore lines of an item with colour codes stripped (<see cref="Item.Description"/> joins them with newlines).</summary>
    private static IEnumerable<string> LoreLines(Item item)
        => (item?.Description ?? "").Split('\n')
            .Select(l => PurchaseParser.StripFormatting(l).Trim())
            .Where(l => l.Length > 0);

    private static bool IsAuctionScreen(ChestView view) =>
        view.Name is "Auction View" or "BIN Auction View" or "Confirm Purchase";

    /// <summary>
    /// The part of an inventory increase of <paramref name="count"/> x <paramref name="tag"/> that counts as a drop: pending chat
    /// purchases come off first, then the product page guard keeps the remainder out.
    /// </summary>
    public static int AdjustIncrease(ExtractedInfo info, string tag, int count, DateTime now)
    {
        if (count <= 0 || string.IsNullOrEmpty(tag))
            return count;
        ExpirePending(info, now);
        if (info.PendingPurchaseDiscounts != null && info.PendingPurchaseDiscounts.TryGetValue(tag, out var pending))
        {
            var taken = Math.Min(count, pending);
            count -= taken;
            if (pending - taken <= 0)
                info.PendingPurchaseDiscounts.Remove(tag);
            else
                info.PendingPurchaseDiscounts[tag] = pending - taken;
        }
        if (count > 0 && info.BazaarProductTag == tag && now - info.BazaarProductSeenAt <= ProductPageGuardWindow)
        {
            info.BazaarGuardSkipped += count;
            return 0;
        }
        return count;
    }

    private static void ExpirePending(ExtractedInfo info, DateTime now)
    {
        if (info.PendingPurchaseDiscounts is { Count: > 0 } && now - info.PendingPurchaseDiscountAt > PendingWindow)
            info.PendingPurchaseDiscounts.Clear();
    }
}
