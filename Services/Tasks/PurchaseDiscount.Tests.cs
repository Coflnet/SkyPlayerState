using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Items.Client.Api;
using SearchResult = Coflnet.Sky.Items.Client.Model.SearchResult;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Production: an item bought on the bazaar during farming showed up as a drop of that activity.
/// </summary>
public class PurchaseDiscountTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string Diamond = "ENCHANTED_DIAMOND";

    [Test]
    public void ChatPurchase_TakesExactlyTheBoughtAmountOffTheTrackedCount()
    {
        var collected = new Dictionary<string, int> { [Diamond] = 100, ["WHEAT"] = 5 };
        var info = new ExtractedInfo();

        var discounted = PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 64, T0);

        discounted.Should().Be(64);
        collected[Diamond].Should().Be(36, "36 were farmed on top of the 64 bought");
        collected["WHEAT"].Should().Be(5);
        info.PendingPurchaseDiscounts.Should().BeEmpty();
    }

    [Test]
    public void ChatPurchase_NeverGoesBelowWhatWasObserved_AndRemainderWaitsForTheInventoryUpdate()
    {
        var collected = new Dictionary<string, int> { [Diamond] = 10 };
        var info = new ExtractedInfo();

        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 64, T0).Should().Be(10);

        collected.Should().NotContainKey(Diamond, "no negative or zero entry is left behind");
        info.PendingPurchaseDiscounts[Diamond].Should().Be(54);
        // the inventory update that follows the chat line is the bought stack
        PurchaseDiscount.AdjustIncrease(info, Diamond, 54, T0.AddSeconds(2)).Should().Be(0);
        info.PendingPurchaseDiscounts.Should().BeEmpty();
    }

    [Test]
    public void ChatPurchase_DoesNotTouchANegativeCount()
    {
        var collected = new Dictionary<string, int> { [Diamond] = -20 };
        var info = new ExtractedInfo();

        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 64, T0).Should().Be(0);

        collected[Diamond].Should().Be(-20);
    }

    [Test]
    public void ChatBeforeInventory_OnlyTheBoughtPartOfALaterIncreaseIsDiscounted()
    {
        var info = new ExtractedInfo();
        PurchaseDiscount.ApplyChatPurchase(new Dictionary<string, int>(), info, Diamond, 64, T0);

        // 64 bought plus 20 farmed arrive in one inventory update
        PurchaseDiscount.AdjustIncrease(info, Diamond, 84, T0.AddSeconds(3)).Should().Be(20);
        // and the next increase is a plain drop again
        PurchaseDiscount.AdjustIncrease(info, Diamond, 5, T0.AddSeconds(8)).Should().Be(5);
    }

    [Test]
    public void PendingPurchase_ExpiresSoALaterDropIsNotEaten()
    {
        var info = new ExtractedInfo();
        PurchaseDiscount.ApplyChatPurchase(new Dictionary<string, int>(), info, Diamond, 64, T0);

        PurchaseDiscount.AdjustIncrease(info, Diamond, 30, T0 + PurchaseDiscount.PendingWindow + TimeSpan.FromSeconds(1)).Should().Be(30);
    }

    [Test]
    public void ProductPageGuard_KeepsTheBoughtItemOutWhenNoChatLineCame()
    {
        var info = new ExtractedInfo();
        PurchaseDiscount.ArmProductGuard(info, Diamond, T0);

        PurchaseDiscount.AdjustIncrease(info, Diamond, 64, T0.AddSeconds(10)).Should().Be(0);
        PurchaseDiscount.AdjustIncrease(info, "WHEAT", 64, T0.AddSeconds(10)).Should().Be(64, "only the page's own item is guarded");
        PurchaseDiscount.AdjustIncrease(info, Diamond, 64, T0 + PurchaseDiscount.ProductPageGuardWindow + TimeSpan.FromSeconds(1))
            .Should().Be(64, "the guard is short lived");
    }

    [Test]
    public void GuardThenChat_IsNotDiscountedTwice()
    {
        var info = new ExtractedInfo();
        var collected = new Dictionary<string, int> { [Diamond] = 20 };
        PurchaseDiscount.ArmProductGuard(info, Diamond, T0);
        // the guard kept the 64 bought ones out, the 20 farmed earlier are tracked
        PurchaseDiscount.AdjustIncrease(info, Diamond, 64, T0.AddSeconds(5)).Should().Be(0);

        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 64, T0.AddSeconds(6)).Should().Be(0, "the guard already took them");

        collected[Diamond].Should().Be(20);
        info.PendingPurchaseDiscounts.Should().BeEmpty("nothing left for a later drop to be eaten by");
        PurchaseDiscount.AdjustIncrease(info, Diamond, 8, T0.AddSeconds(9)).Should().Be(8, "the guard disarmed once the chat explained its skips");
    }

    [Test]
    public void ChatThenInventory_WithTheProductPageStillOpen_IsNotDiscountedTwice()
    {
        var info = new ExtractedInfo();
        var collected = new Dictionary<string, int>();
        PurchaseDiscount.ArmProductGuard(info, Diamond, T0);

        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 64, T0.AddSeconds(1));
        // the pending discount takes the 64; the guard must not take the 16 farmed in the same update as well
        PurchaseDiscount.AdjustIncrease(info, Diamond, 80, T0.AddSeconds(2)).Should().Be(16);
    }

    [Test]
    public void TwoPurchasesUnderTheGuard_AreBothExplainedByTheirChatLines()
    {
        var info = new ExtractedInfo();
        var collected = new Dictionary<string, int>();
        PurchaseDiscount.ArmProductGuard(info, Diamond, T0);
        PurchaseDiscount.AdjustIncrease(info, Diamond, 32, T0.AddSeconds(2)).Should().Be(0);
        PurchaseDiscount.AdjustIncrease(info, Diamond, 32, T0.AddSeconds(4)).Should().Be(0);

        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 32, T0.AddSeconds(5));
        PurchaseDiscount.ApplyChatPurchase(collected, info, Diamond, 32, T0.AddSeconds(6));

        info.PendingPurchaseDiscounts.Should().BeEmpty();
        info.BazaarGuardSkipped.Should().Be(0);
    }

    [TestCase("[Bazaar] Bought 64x Enchanted Diamond for 10,240 coins!", 64, "Enchanted Diamond", 10_240)]
    [TestCase("§6[Bazaar] §7Bought §a1,024§7x §9Enchanted Coal §7for §610.5k coins!", 1024, "Enchanted Coal", 10_500)]
    [TestCase("[Bazaar] Claimed 64x Enchanted Diamond worth 10,240 coins bought for 160 each!", 64, "Enchanted Diamond", 10_240)]
    public void BazaarBuyLines_ReportWhatWasBought(string line, int amount, string name, long coins)
    {
        // the coloured sample is not a real line, it only has to survive the formatting codes
        line = line.Replace("§6[Bazaar] §7Bought §a1,024§7x §9Enchanted Coal §7for §610.5k coins!", "§6[Bazaar] Bought 1,024x Enchanted Coal for 10.5k coins!");
        PurchaseParser.TryParseBazaarBuy(line, out var parsedAmount, out var parsedName, out var parsedCoins).Should().BeTrue();
        parsedAmount.Should().Be(amount);
        parsedName.Should().Be(name);
        parsedCoins.Should().Be(coins);
    }

    [TestCase("[Bazaar] Sold 64x Enchanted Diamond for 10,240 coins!")]
    [TestCase("You purchased Hyperion for 970,000,000 coins!")]
    public void NonBazaarBuyLines_AreNotAPurchaseOfItems(string line)
    {
        PurchaseParser.TryParseBazaarBuy(line, out _, out _, out _).Should().BeFalse();
    }

    private static MockedUpdateArgs ChatArgs(StateObject state, string line, DateTime at, Mock<IItemsApi>? items = null)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ReceivedAt = at, ChatBatch = [line] }
        };
        items ??= new Mock<IItemsApi>();
        items.Setup(i => i.ItemsSearchTermGetAsync("Enchanted Diamond", null, 0, default))
            .ReturnsAsync([new SearchResult { Tag = Diamond, Text = "Enchanted Diamond" }]);
        args.AddService(items.Object);
        args.AddService<ILogger<Bazaar.BazaarOrderListener>>(NullLogger<Bazaar.BazaarOrderListener>.Instance);
        return args;
    }

    [Test]
    public async Task PurchaseListener_DiscountsTheBoughtItemAndStillRecordsTheCoins()
    {
        var state = new StateObject();
        state.ItemsCollectedRecently[Diamond] = 90;

        await new PurchaseListener().Process(ChatArgs(state, "[Bazaar] Bought 64x Enchanted Diamond for 10,240 coins!", T0));

        state.ItemsCollectedRecently[Diamond].Should().Be(26);
        state.ItemsCollectedRecently[PseudoItems.BAZAAR_PURCHASE].Should().Be(10_240, "the purchase coins are handled as before");
    }

    [Test]
    public async Task PurchaseListener_ClaimedBuyOrderIsDiscountedToo()
    {
        var state = new StateObject();
        state.ItemsCollectedRecently[Diamond] = 64;

        await new PurchaseListener().Process(ChatArgs(state,
            "[Bazaar] Claimed 64x Enchanted Diamond worth 10,240 coins bought for 160 each!", T0));

        state.ItemsCollectedRecently.Should().NotContainKey(Diamond);
        state.ItemsCollectedRecently[PseudoItems.BAZAAR_PURCHASE].Should().Be(10_240);
    }

    [Test]
    public async Task PurchaseListener_ItemLookupFailing_StillRecordsTheCoinsAndDiscountsNothing()
    {
        // an item name nobody resolved before: GetTagForName caches successful lookups for two hours
        var state = new StateObject();
        state.ItemsCollectedRecently["UNLOOKED_UP_ITEM"] = 64;
        var broken = new Mock<IItemsApi>();
        broken.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default)).ThrowsAsync(new Exception("down"));
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ReceivedAt = T0,
                ChatBatch = ["[Bazaar] Bought 64x Unlooked Up Item for 10,240 coins!"] }
        };
        args.AddService(broken.Object);
        args.AddService<ILogger<Bazaar.BazaarOrderListener>>(NullLogger<Bazaar.BazaarOrderListener>.Instance);

        await new PurchaseListener().Process(args);

        state.ItemsCollectedRecently["UNLOOKED_UP_ITEM"].Should().Be(64, "nothing is discounted without a tag");
        state.ItemsCollectedRecently[PseudoItems.BAZAAR_PURCHASE].Should().Be(10_240);
    }

    private static Item Button(string name) => new() { ItemName = name };

    private static ChestView ProductPage(string tag)
    {
        var items = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        items[10] = Button("§aBuy Instantly");
        items[11] = Button("§6Create Buy Order");
        items[13] = new Item { Tag = tag, ItemName = "Enchanted Diamond", Count = 1 };
        items[49] = Button("Close");
        for (var i = 0; i < 36; i++)
            items.Add(new Item());
        return new ChestView { Name = "Enchanted Diamond", Items = items };
    }

    [Test]
    public async Task OpeningTheProductPage_ArmsTheGuardForThatItem()
    {
        var state = new StateObject();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", ReceivedAt = T0, Chest = ProductPage(Diamond) }
        };
        args.AddService<ILogger<CollectionListener>>(NullLogger<CollectionListener>.Instance);
        await new RecentViewsUpdate().Process(args);

        await new CollectionListener().Process(args);

        state.ExtractedInfo.BazaarProductTag.Should().Be(Diamond);
        state.ExtractedInfo.BazaarProductSeenAt.Should().Be(T0);
    }

    private static async Task View(StateObject state, ChestView chest, DateTime at)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", ReceivedAt = at, Chest = chest }
        };
        args.AddService<ILogger<CollectionListener>>(NullLogger<CollectionListener>.Instance);
        await new RecentViewsUpdate().Process(args);
        await new CollectionListener().Process(args);
    }

    private static ChestView Inventory(int diamonds)
    {
        var items = Enumerable.Range(0, 9).Select(_ => new Item()).ToList();
        items.Add(new Item { Tag = Diamond, ItemName = Diamond, Count = diamonds });
        while (items.Count < 46)
            items.Add(new Item());
        return new ChestView { Name = "Crafting", Items = items };
    }

    [Test]
    public async Task IncreaseShortlyAfterTheProductPage_IsNotADrop_ButALaterOneIs()
    {
        var state = new StateObject();
        await View(state, Inventory(10), T0);
        await View(state, ProductPage(Diamond), T0.AddSeconds(5));
        await View(state, Inventory(74), T0.AddSeconds(15));
        state.ItemsCollectedRecently.GetValueOrDefault(Diamond).Should().Be(0, "the 64 came from the bazaar");

        await View(state, Inventory(80), T0.AddSeconds(15) + PurchaseDiscount.ProductPageGuardWindow);
        state.ItemsCollectedRecently.GetValueOrDefault(Diamond).Should().Be(6, "farmed after the guard ran out");
    }

    [Test]
    public async Task IncreaseWithoutAProductPage_IsADrop()
    {
        var state = new StateObject();
        await View(state, Inventory(10), T0);
        await View(state, Inventory(74), T0.AddSeconds(15));

        state.ItemsCollectedRecently.GetValueOrDefault(Diamond).Should().Be(64);
    }

    [Test]
    public async Task ChatLineAfterTheInventoryUpdate_TakesTheBoughtAmountOffTheTrackedIncrease()
    {
        var state = new StateObject();
        await View(state, Inventory(10), T0);
        await View(state, Inventory(100), T0.AddSeconds(15)); // 64 bought + 26 farmed
        state.ItemsCollectedRecently[Diamond].Should().Be(90);

        await new PurchaseListener().Process(ChatArgs(state, "[Bazaar] Bought 64x Enchanted Diamond for 10,240 coins!", T0.AddSeconds(16)));

        state.ItemsCollectedRecently[Diamond].Should().Be(26);
    }

    private const string Hyperion = "HYPERION";
    private const string HyperionChat = "You purchased §dWithered Hyperion §6✪✪✪✪✪ for 970,000,000 coins!";

    private static ChestView AuctionScreen(string name, string itemName, string tag, int count, DateTime openedAt)
    {
        var items = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        items[13] = new Item { Tag = tag, ItemName = itemName, Count = count };
        for (var i = 0; i < 36; i++)
            items.Add(new Item());
        return new ChestView { Name = name, Items = items, OpenedAt = openedAt };
    }

    private static ChestView Filler(DateTime openedAt) => new() { Name = "Crafting", Items = [.. Enumerable.Range(0, 46).Select(_ => new Item())], OpenedAt = openedAt };

    private static StateObject StateWithViews(params ChestView[] views)
    {
        var state = new StateObject();
        foreach (var v in views)
            state.RecentViews.Enqueue(v);
        return state;
    }

    private static Task ChatAuction(StateObject state, DateTime at, string line = HyperionChat)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ReceivedAt = at, ChatBatch = [line] }
        };
        return new PurchaseListener().Process(args);
    }

    [Test]
    public async Task AuctionPurchase_FoundInTheNewestScreen_IsDiscountedAndCoinsStay()
    {
        var state = StateWithViews(AuctionScreen("Confirm Purchase", "§dWithered Hyperion §6✪✪✪✪✪", Hyperion, 1, T0));
        state.ItemsCollectedRecently[Hyperion] = 1;
        state.ItemsCollectedRecently["WHEAT"] = 7;

        await ChatAuction(state, T0.AddSeconds(3));

        state.ItemsCollectedRecently.Should().NotContainKey(Hyperion);
        state.ItemsCollectedRecently["WHEAT"].Should().Be(7);
        state.ItemsCollectedRecently[PseudoItems.AUCTION_PURCHASE].Should().Be(970_000_000);
    }

    [Test]
    public async Task AuctionPurchase_FoundInTheThirdLastScreen()
    {
        var state = StateWithViews(AuctionScreen("BIN Auction View", "§dWithered Hyperion §6✪✪✪✪✪", Hyperion, 1, T0), Filler(T0), Filler(T0));
        state.ItemsCollectedRecently[Hyperion] = 1;

        await ChatAuction(state, T0.AddSeconds(3));

        state.ItemsCollectedRecently.Should().NotContainKey(Hyperion);
    }

    [Test]
    public async Task AuctionPurchase_InAnEvictedFourthLastScreen_DiscountsNothing()
    {
        var state = StateWithViews(AuctionScreen("BIN Auction View", "§dWithered Hyperion §6✪✪✪✪✪", Hyperion, 1, T0), Filler(T0), Filler(T0));
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", ReceivedAt = T0, Chest = Filler(T0) }
        };
        await new RecentViewsUpdate().Process(args);
        state.RecentViews.Should().HaveCount(3);
        state.ItemsCollectedRecently[Hyperion] = 1;

        await ChatAuction(state, T0.AddSeconds(3));

        state.ItemsCollectedRecently[Hyperion].Should().Be(1);
        state.ItemsCollectedRecently[PseudoItems.AUCTION_PURCHASE].Should().Be(970_000_000);
    }

    [Test]
    public async Task AuctionPurchase_StackCountComesFromTheItem()
    {
        var state = StateWithViews(AuctionScreen("BIN Auction View", "§9Enchanted Diamond", Diamond, 32, T0));
        state.ItemsCollectedRecently[Diamond] = 40;

        await ChatAuction(state, T0.AddSeconds(2), "You purchased §9Enchanted Diamond for 5,000 coins!");

        state.ItemsCollectedRecently[Diamond].Should().Be(8);
    }

    [Test]
    public async Task AuctionPurchase_NoMatchingScreen_DiscountsNothing()
    {
        var state = StateWithViews(AuctionScreen("BIN Auction View", "§dSome Other Sword", "OTHER_SWORD", 1, T0));
        state.ItemsCollectedRecently[Hyperion] = 1;
        state.ItemsCollectedRecently["OTHER_SWORD"] = 1;

        await ChatAuction(state, T0.AddSeconds(3));

        state.ItemsCollectedRecently[Hyperion].Should().Be(1);
        state.ItemsCollectedRecently["OTHER_SWORD"].Should().Be(1);
        state.ItemsCollectedRecently[PseudoItems.AUCTION_PURCHASE].Should().Be(970_000_000);
        state.ExtractedInfo.PendingPurchaseDiscounts.Should().BeEmpty();
    }

    [Test]
    public async Task AuctionPurchase_ScreenOlderThanTheMaxAge_IsIgnored()
    {
        var state = StateWithViews(AuctionScreen("BIN Auction View", "§dWithered Hyperion §6✪✪✪✪✪", Hyperion, 1, T0));
        state.ItemsCollectedRecently[Hyperion] = 1;

        await ChatAuction(state, T0 + PurchaseDiscount.AuctionScreenMaxAge + TimeSpan.FromSeconds(1));

        state.ItemsCollectedRecently[Hyperion].Should().Be(1);
    }

    [Test]
    public async Task AuctionPurchase_ChatBeforeInventory_IsPendingAndTakenOffTheNextIncrease()
    {
        var state = StateWithViews(AuctionScreen("Confirm Purchase", "§dWithered Hyperion §6✪✪✪✪✪", Hyperion, 1, T0));

        await ChatAuction(state, T0.AddSeconds(2));
        state.ExtractedInfo.PendingPurchaseDiscounts[Hyperion].Should().Be(1);

        PurchaseDiscount.AdjustIncrease(state.ExtractedInfo, Hyperion, 1, T0.AddSeconds(4)).Should().Be(0);
        state.ExtractedInfo.PendingPurchaseDiscounts.Should().BeEmpty();
        PurchaseDiscount.AdjustIncrease(state.ExtractedInfo, Hyperion, 1, T0.AddSeconds(5)).Should().Be(1, "only the bought one was discounted");
    }

    [Test]
    public async Task AuctionPurchase_DoesNotUseUpTheBazaarProductGuard()
    {
        var state = StateWithViews(AuctionScreen("Confirm Purchase", "§9Enchanted Diamond", Diamond, 1, T0));
        PurchaseDiscount.ArmProductGuard(state.ExtractedInfo, Diamond, T0);
        PurchaseDiscount.AdjustIncrease(state.ExtractedInfo, Diamond, 64, T0.AddSeconds(1)); // bazaar buy skipped by the guard

        await ChatAuction(state, T0.AddSeconds(2), "You purchased §9Enchanted Diamond for 5,000 coins!");

        state.ExtractedInfo.BazaarGuardSkipped.Should().Be(64);
        state.ExtractedInfo.BazaarProductTag.Should().Be(Diamond);
    }

    private static ChestView RealConfirmScreen(string purchasingLore, string itemLore, string tag, int count)
    {
        var screen = AuctionScreen("Confirm Purchase", "BUYING ITEM:", tag, count, T0);
        screen.Items[11] = new Item { ItemName = "Confirm Purchase", Description = purchasingLore };
        screen.Items[13].Description = itemLore;
        return screen;
    }

    [TestCase("Radiant Power Orb")]
    [TestCase("§aRadiant Power Orb")]
    public void ConfirmScreen_BuyingItemSlot_MatchesTheFirstLoreLine(string chatName)
    {
        var screen = RealConfirmScreen("§7Cost: §6349,000 coins\n\nClick to confirm!", "\nRadiant Power Orb\n§6Ability: Mana Infusion", "RADIANT_POWER_ORB", 1);

        PurchaseDiscount.TryFindAuctionItem([screen], chatName, T0.AddSeconds(5), out var tag, out var count).Should().BeTrue();
        tag.Should().Be("RADIANT_POWER_ORB");
        count.Should().Be(1);
    }

    [Test]
    public void ConfirmScreen_PetItem_MatchesViaThePurchasingLine_AndKeepsTheItemCount()
    {
        var screen = RealConfirmScreen("§7Purchasing: §7[Lvl 1] §5Slug\n§7Cost: §6500 coins\n\nClick to confirm!", "\nSomething else\nFarming Pet", "PET_SLUG", 3);

        PurchaseDiscount.TryFindAuctionItem([screen], "[Lvl 1] Slug", T0.AddSeconds(5), out var tag, out var count).Should().BeTrue();
        tag.Should().Be("PET_SLUG");
        count.Should().Be(3);
    }

    [Test]
    public void ConfirmScreen_BuyingItemSlot_DoesNotMatchAnotherName()
    {
        var screen = RealConfirmScreen("§7Purchasing: §aRadiant Power Orb", "\nRadiant Power Orb\n§6Ability", "RADIANT_POWER_ORB", 1);

        PurchaseDiscount.TryFindAuctionItem([screen], "Mana Flux Power Orb", T0.AddSeconds(5), out _, out _).Should().BeFalse();
    }
}
