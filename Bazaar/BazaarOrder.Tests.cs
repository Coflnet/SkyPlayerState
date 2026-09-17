using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.EventBroker.Client.Model;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderTests
{
    Mock<ITransactionService> transactionService = null!;
    BazaarOrderListener listener = new BazaarOrderListener();
    StateObject currentState = null!;
    int invokeCount = 0;
    private Mock<IScheduleApi> scheduleApi;
    private Mock<IOrderBookApi> orderBookApi;
    private Mock<IItemsApi> itemsApi;

    [SetUp]
    public void Setup()
    {
        transactionService = new Mock<ITransactionService>();
        transactionService.Setup(t => t.AddTransactions(It.IsAny<Transaction>()))
            .Callback(() =>
            {
                invokeCount++;
            });
        currentState = new();
        invokeCount = 0;
    }
    [Test]
    public async Task BuyOrderCreateAndFill()
    {
        UpdateArgs args = CreateArgs("[Bazaar] Submitting buy order...",
                    "[Bazaar] Buy Order Setup! 64x Coal for 134.4 coins");
        await listener.Process(args);

        // coins are locked up
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == Transaction.TransactionType.BazaarListSell
            && t.Amount == 1344
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once);
        Assert.That(1, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(64, Is.EqualTo(currentState.BazaarOffers[0].Amount));
        Assert.That(134.4 / 64, Is.EqualTo(currentState.BazaarOffers[0].PricePerUnit));

        return;
        await listener.Process(CreateArgs("[Bazaar] Your Buy Order for 64x Coal was filled!"));
        AssertCoalBuy();
        Assert.That(1, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(3, Is.EqualTo(invokeCount));
    }


    [Test]
    public async Task BuyOrderCreateExpensive()
    {
        UpdateArgs args = CreateArgs("[Bazaar] Submitting buy order...",
                    "[Bazaar] Buy Order Setup! 4x Magma Core for 2,182,638 coins.");
        await listener.Process(args);

        // coins are locked up
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == Transaction.TransactionType.BazaarListSell
            && t.Amount == 21826384
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once);
        Assert.That(1, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(4, Is.EqualTo(currentState.BazaarOffers[0].Amount));
        Assert.That(545659.6, Is.EqualTo(currentState.BazaarOffers[0].PricePerUnit));
    }

    [Test]
    public async Task SellOrderCreateExpensive()
    {
        UpdateArgs args = CreateArgs("[Bazaar] Submitting buy order...",
                    "[Bazaar] Sell Offer Setup! 64x Agatha's Coupon for 764,796 coins.");
        args.currentState.RecentViews.Enqueue(JsonConvert.DeserializeObject<ChestView>("""
        {"Items":[{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":"§aBuy Order","Tag":"MAGMA_CORE","ExtraAttributes":{},"Enchantments":null,"Color":null,
        "Description":"§8Bazaar\n\n§7Price per unit: §612,085.9 coins\n\n§7Selling: §a64§7x §aAgatha's Coupon\n§7You earn: §6764,796 coins\n\n§bOrders are shared by co-op!\n§8Current tax: 1.1%\n\n§7§eClick to submit order!","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1}
        ],"Name":"Confirm Buy Order","Position":null,"OpenedAt":"2025-08-25T11:44:56.1953913Z"}
        """)!);
        await listener.Process(args);

        // coins are locked up
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == Transaction.TransactionType.BazaarListSell
            && t.Amount == 64
            && t.ItemId == 5
            )
        ), Times.Once);
        Assert.That(1, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(64, Is.EqualTo(currentState.BazaarOffers[0].Amount));
        Assert.That(12085.9, Is.EqualTo(currentState.BazaarOffers[0].PricePerUnit));
    }

    private void AssertCoalBuy()
    {
        // coins exchanged to item
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                        t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE)
                        && t.Amount == 64
                        && t.ItemId == 5
                        )
                    ), Times.Once);
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.REMOVE)
            && t.Amount == 1344
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once);
    }
    [TestCase("[Bazaar] Buy Order Setup! 1x Ultimate Wise V for 3,570,083 coins.")]
    [TestCase("[Bazaar] Buy Order Setup! 1x Experience IV for 353,667 coins.")]
    [TestCase("[Bazaar] Buy Order Setup! 1x Experience IV for 353k coins.")]
    [TestCase("[Bazaar] Cancelled! Refunded 3,554,406 coins from cancelling Buy Order!")]
    [TestCase("[Bazaar] Claimed 187x Melon worth 112.2 coins bought for 0.6 each!")]
    public async Task RunParse(string line)
    {
        await listener.Process(CreateArgs(line));
    }

    [Test]
    public async Task ClaimBuyOrderIgnoresNullOfferEntries()
    {
        currentState.BazaarOffers.Add(null!);

        await listener.Process(CreateArgs("[Bazaar] Claimed 187x Melon worth 112.2 coins bought for 0.6 each!"));
    }

    [Test]
    public async Task CancelBuyOrderIgnoresNullOfferEntries()
    {
        currentState.BazaarOffers.Add(null!);

        await listener.Process(CreateArgs("[Bazaar] Cancelled! Refunded 148,877,624 coins from cancelling Buy Order!"));
    }

    [Test]
    public async Task SellOrderCreateAndFill()
    {
        UpdateArgs args = CreateArgs("[Bazaar] Submitting sell order...",
                    "[Bazaar] Sell Offer Setup! 64x Coal for 208.8 coins.");
        var receivedAt = DateTime.UtcNow.AddHours(-2);
        args.msg.ReceivedAt = receivedAt;
        await listener.Process(args);

        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                        t.Type == Transaction.TransactionType.BazaarListSell
                        && t.Amount == 64
                        && t.ItemId == 5
                        )
                    ), Times.Once);
        Assert.That(currentState.BazaarOffers.Count, Is.EqualTo(1));
        Assert.That(currentState.BazaarOffers[0].Amount, Is.EqualTo(64));
        Assert.That(currentState.BazaarOffers[0].PricePerUnit, Is.EqualTo(3.3));

        scheduleApi.Verify(s => s.ScheduleUserIdPostAsync("5", receivedAt.AddDays(7), It.Is<MessageContainer>(con =>
            con.Message == "Your bazaar order for Coal expired"
        ), 0, default), Times.Once);
    }

    private void AssertCoalSell()
    {
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction[]>(t =>
                                t.First().Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.REMOVE)
                                && t.First().Amount == 64
                                && t.First().ItemId == 5
                                )
                            ), Times.Once);
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE)
            && t.Amount == 3037
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once);
    }

    [Test]
    public async Task ClaimSellWithNoFill()
    {
        var createTime = DateTime.Now - TimeSpan.FromHours(1);
        currentState.BazaarOffers.Add(new Offer()
        {
            Amount = 64,
            ItemName = "Coal",
            PricePerUnit = 4.8,
            IsSell = true,
            Created = createTime,
        });
        var args = CreateArgs("[Bazaar] Claiming order...",
                "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!");
        itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(() => new List<Items.Client.Model.SearchResult>(){new(){
                Tag = "COAL",
                Flags = Items.Client.Model.ItemFlags.BAZAAR
            }});
        await listener.Process(args);

        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE | Transaction.TransactionType.Move)
            && t.Amount == 3037
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once);
        AssertCoalSell();
        Assert.That(3, Is.EqualTo(invokeCount));
        orderBookApi.Verify(o => o.RemoveOrderAsync("COAL", "5", createTime, 0, default), Times.Once);
    }
    [Test]
    public async Task InstaBuy()
    {
        await listener.Process(CreateArgs("[Bazaar] Executing instant buy...",
                "[Bazaar] Bought 1,280x Coal for 5,120 coins!"));
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.REMOVE)
                    && t.Amount == 51200
                    && t.ItemId == TradeDetect.IdForCoins
                    )
                ), Times.Once);
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE)
                    && t.Amount == 1280
                    && t.ItemId == 5
                    )
                ), Times.Once);
        Assert.That(0, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(2, Is.EqualTo(invokeCount));
    }
    [Test]
    public async Task InstaSell()
    {
        await listener.Process(CreateArgs("[Bazaar] Executing instant sell...",
            "[Bazaar] Sold 1,280x Coal for 3,840.2 coins!"));
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.REMOVE)
                    && t.Amount == 1280
                    && t.ItemId == 5
                    )
                ), Times.Once);
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE)
                    && t.Amount == 38400
                    && t.ItemId == TradeDetect.IdForCoins
                    )
                ), Times.Once);
        Assert.That(0, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(2, Is.EqualTo(invokeCount));
    }
    [Test]
    public async Task CancelOrder()
    {
        currentState.BazaarOffers.Add(new Offer()
        {
            Amount = 926,
            ItemName = "Enchanted End Stone",
            PricePerUnit = 303.7,
            IsSell = true,
            Created = DateTime.Now - TimeSpan.FromHours(1),
        });
        await listener.Process(CreateArgs("[Bazaar] Cancelling order...",
                "[Bazaar] Cancelled! Refunded 926x Enchanted End Stone from cancelling Sell Offer!"));
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.Move | Transaction.TransactionType.RECEIVE)
                    && t.Amount == 926
                    && t.ItemId == 5
                    )
                ), Times.Once);
        Assert.That(0, Is.EqualTo(currentState.BazaarOffers.Count));
        transactionService.VerifyAll();
    }

    [Test]
    public async Task CancelBuyOrder()
    {
        currentState.BazaarOffers.Add(new Offer()
        {
            Amount = 64,
            ItemName = "Enchanted End Stone",
            PricePerUnit = 60000,
            IsSell = false,
            Created = DateTime.Now - TimeSpan.FromHours(1),
        });
        await listener.Process(CreateArgs("[Bazaar] Cancelling order...",
                "[Bazaar] Cancelled! Refunded 3,840,000 coins from cancelling Buy Order!"));
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
                    t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.Move | Transaction.TransactionType.RECEIVE)
                    && t.Amount == 38400000
                    && t.ItemId == TradeDetect.IdForCoins
                    )
                ), Times.Once);
        Assert.That(0, Is.EqualTo(currentState.BazaarOffers.Count));
        transactionService.VerifyAll();
    }

    [Test]
    public async Task IgnoresMessage()
    {
        await listener.Process(CreateArgs("[Bazaar] There are no Buy Orders for this product!"));
        Assert.That(0, Is.EqualTo(currentState.BazaarOffers.Count));
        Assert.That(0, Is.EqualTo(invokeCount));
    }

    /// <summary>
    /// Race condition: When claiming a sell order, the BazaarListener may have already
    /// processed a chest update that removed the order from state before the chat message
    /// is processed by BazaarOrderListener.
    /// 
    /// Timeline from logs:
    /// 1. 19:30:16.222 - Chest update with 4 offers (including Coal sell order)
    /// 2. 19:30:17.938 - Chest update with 3 offers (Coal already claimed/removed from GUI)
    /// 3. 19:30:18.168 - Chat: "Claimed 1,548.4 coins from selling 160x Coal at 9.9 each!"
    /// 
    /// The BazaarListener processes the second chest update and removes the Coal order
    /// before BazaarOrderListener can track the claim.
    /// </summary>
    [Test]
    public async Task ClaimSellOrderWhenOrderAlreadyRemovedByChestUpdate()
    {
        // Initially the order exists
        var createTime = DateTime.Now - TimeSpan.FromMinutes(5);
        currentState.BazaarOffers.Add(new Offer()
        {
            Amount = 160,
            ItemName = "Coal",
            PricePerUnit = 9.9,
            IsSell = true,
            Created = createTime,
        });

        // Simulate BazaarListener processing a chest update that no longer contains the order
        // (order was claimed in GUI but chat message not yet processed)
        var bazaarListener = new BazaarListener();
        var chestArgs = CreateArgs();
        chestArgs.msg.Chest = JsonConvert.DeserializeObject<ChestView>("""
        {
            "Name": "Co-op Bazaar Orders",
            "Items": [
                {"ItemName": "§6§lSELL §aAgatha's Coupon", "Tag": "AGATHA_COUPON", "Description": "§8Worth 5M coins\n\n§7Offer amount: §a300§7x\n\n§8Expired!\n\n§7Price per unit: §617,000.0 coins\n\n§7By: §b[MVP§4+§b] Ekwav\n\n§eClick to view options!", "Count": 1},
                {"ItemName": "§aGo Back", "Tag": null, "Description": "§7To Bazaar", "Count": 1}
            ]
        }
        """);
        chestArgs.AddService<ILogger<BazaarListener>>(NullLoggerFactory.Instance.CreateLogger<BazaarListener>());
        await bazaarListener.Process(chestArgs);

        // Order should be removed by BazaarListener (simulating race condition)
        Assert.That(currentState.BazaarOffers.Count, Is.EqualTo(1), "BazaarListener should have updated offers from chest");
        Assert.That(currentState.BazaarOffers.Any(o => o.ItemName == "Coal"), Is.False, "Coal order should be removed");

        // Now process the claim chat message - order is no longer in state
        itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(() => new List<Items.Client.Model.SearchResult>(){new(){
                Tag = "COAL",
                Flags = Items.Client.Model.ItemFlags.BAZAAR
            }});
        var claimArgs = CreateArgs("[Bazaar] Claiming order...",
                "[Bazaar] Claimed 1,548.4 coins from selling 160x Coal at 9.9 each!");
        await listener.Process(claimArgs);

        // Should still record the coin transaction even if order not found
        transactionService.Verify(t => t.AddTransactions(It.Is<Transaction>(t =>
            t.Type == (Transaction.TransactionType.BAZAAR | Transaction.TransactionType.RECEIVE | Transaction.TransactionType.Move)
            && t.Amount == 15484
            && t.ItemId == TradeDetect.IdForCoins
            )
        ), Times.Once, "Coin transaction should be recorded even when order is missing from state");
    }

    [Test]
    public async Task FilledChatRetainsExactSideUntilClaimed()
    {
        var created = DateTime.UtcNow.AddMinutes(-1);
        currentState.BazaarOffers.Add(new() { ItemName = "Coal", Amount = 64, PricePerUnit = 9.9, Created = created });
        currentState.BazaarOffers.Add(new() { ItemName = "Coal", Amount = 64, IsSell = true, PricePerUnit = 9.9,
            Created = created.AddSeconds(1) });
        var args = CreateArgs("[Bazaar] Your Sell Offer for 64x Coal was filled!");
        itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(new List<Items.Client.Model.SearchResult> { new() { Tag = "COAL" } });
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers[0].FilledAmount, Is.Zero);
        Assert.That(currentState.BazaarOffers[1].FilledAmount, Is.EqualTo(64));
        Assert.That(currentState.BazaarUpdatedAt, Is.EqualTo(args.msg.ReceivedAt));
        orderBookApi.Verify(a => a.AddOrderAsync(It.Is<Coflnet.Sky.Bazaar.Client.Model.OrderEntry>(o =>
            o.IsSell == true && o.Filled == 64 && o.Timestamp == created.AddSeconds(1)), 0, default), Times.Once);
        orderBookApi.Verify(a => a.RemoveOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 0, default), Times.Never);
        args.msg.ChatBatch = new() { "[Bazaar] Claimed 633.6 coins from selling 64x Coal at 9.9 each!" };
        await listener.Process(args);
        orderBookApi.Verify(a => a.RemoveOrderAsync("COAL", "5", created.AddSeconds(1), 0, default), Times.Once);
        Assert.That(currentState.BazaarOffers.Single().IsSell, Is.False);
    }

    [Test]
    public async Task CancellingPartlyFilledBuyOrderRemovesItFromAuthority()
    {
        var created = DateTime.UtcNow.AddMinutes(-1);
        currentState.BazaarOffers.Add(new() { ItemTag = "COAL", ItemName = "Coal", Amount = 64,
            FilledAmount = 32, PricePerUnit = 10, Created = created });
        var args = CreateArgs("[Bazaar] Cancelled! Refunded 320 coins from cancelling Buy Order!");
        await listener.Process(args);
        orderBookApi.Verify(a => a.RemoveOrderAsync("COAL", "5", created, 0, default), Times.Once);
        Assert.That(currentState.BazaarOffers, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PartialClaimRetainsOrderUntilEntireAmountWithdrawn(bool sell)
    {
        var created = DateTime.UtcNow.AddMinutes(-1);
        var order = new Offer { ItemName = "Coal", ItemTag = "COAL", Amount = 1024,
            FilledAmount = 1024, PricePerUnit = 7.1, IsSell = sell, IsExpired = true, Created = created };
        currentState.BazaarOffers.Add(order);
        var message = sell ? "[Bazaar] Claimed 3,635.2 coins from selling 512x Coal at 7.1 each!"
            : "[Bazaar] Claimed 512x Coal worth 3,635.2 coins bought for 7.1 each!";
        var args = CreateArgs(message);
        var sync = new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient());
        args.AddService(sync.Object);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers.Single(), Is.SameAs(order));
        Assert.That(order.FilledAmount, Is.EqualTo(1024));
        Assert.That(order.ClaimedAmount, Is.EqualTo(512));
        sync.Verify(s => s.Claim(args, order), Times.Once);
        orderBookApi.Verify(a => a.RemoveOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 0, default), Times.Never);
        args.msg.ReceivedAt = args.msg.ReceivedAt.AddSeconds(1);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers, Is.Empty);
        orderBookApi.Verify(a => a.RemoveOrderAsync("COAL", "5", created, 0, default), Times.Once);
        orderBookApi.Verify(a => a.AddOrderAsync(It.IsAny<Coflnet.Sky.Bazaar.Client.Model.OrderEntry>(), 0, default), Times.Never,
            "Withdrawing an old fill must not announce a new completion");
    }

    [Test]
    public async Task LaterChatForAnotherOrderDoesNotDiscardPartialClaim()
    {
        var order = new Offer { ItemName = "Coal", ItemTag = "COAL", Amount = 1024, FilledAmount = 1024, PricePerUnit = 7.1 };
        currentState.BazaarOffers.Add(order);
        var args = CreateArgs("[Bazaar] Claimed 512x Coal worth 3,635.2 coins bought for 7.1 each!");
        var laterChat = args.msg.ReceivedAt.AddSeconds(1);
        currentState.BazaarUpdatedAt = laterChat;
        args.AddService(new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient()).Object);
        await listener.Process(args);
        Assert.That(order.ClaimedAmount, Is.EqualTo(512));
        Assert.That(currentState.BazaarUpdatedAt, Is.EqualTo(laterChat));
    }

    [Test]
    public async Task NewerMenuPreventsLateSetupChatFromDuplicatingOrder()
    {
        currentState.BazaarOffers.Add(new() { ItemName = "Coal", Amount = 64, PricePerUnit = 2.1 });
        var args = CreateArgs("[Bazaar] Buy Order Setup! 64x Coal for 134.4 coins");
        currentState.BazaarObservedAt = args.msg.ReceivedAt.AddSeconds(1);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers, Has.Count.EqualTo(1));
        orderBookApi.Verify(a => a.AddOrderAsync(It.IsAny<Coflnet.Sky.Bazaar.Client.Model.OrderEntry>(), 0, default), Times.Never);
        transactionService.Verify(t => t.AddTransactions(It.IsAny<Transaction>()), Times.Once);
    }

    [Test]
    public async Task MenuAlreadyIncludingClaimPreventsDoubleSubtraction()
    {
        currentState.BazaarOffers.Add(new() { ItemName = "Coal", Amount = 1024,
            FilledAmount = 1024, ClaimedAmount = 512, PricePerUnit = 7.1 });
        var args = CreateArgs("[Bazaar] Claimed 512x Coal worth 3,635.2 coins bought for 7.1 each!");
        currentState.BazaarObservedAt = args.msg.ReceivedAt.AddSeconds(1);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers.Single().ClaimedAmount, Is.EqualTo(512));
    }

    [TestCase(-5, false)]
    [TestCase(0, false)]
    [TestCase(5, false)]
    [TestCase(2000, false)]
    [TestCase(5, true)]
    public async Task MenuAndChatForSamePartialClaimOnlyWithdrawOnce(int chatDelayMs, bool repeatMenu)
    {
        var order = new Offer { ItemName = "Gill Membrane", ItemTag = "GILL_MEMBRANE", Amount = 1024,
            FilledAmount = 1024, ClaimedAmount = 512, PricePerUnit = 7.1, IsExpired = true,
            Created = DateTime.UtcNow.AddMinutes(-1) };
        currentState.BazaarOffers.Add(order);
        var args = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 256x Gill Membrane worth 1,817.6 coins bought for 7.1 each!");
        args.AddService(new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient()).Object);
        var observedAt = args.msg.ReceivedAt;
        args.msg.Chest = new ChestView { Name = "Co-op Bazaar Orders", Items = new() { new() {
            ItemName = "§a§lBUY §aGill Membrane", Tag = "GILL_MEMBRANE",
            Description = "§7Order amount: §a1,024§7x\n§7Filled: §a1k§7/1k §a§l100%!\nExpired!\n§7Price per unit: §67.1 coins\n§aYou have §2256 items §ato claim!"
        } } };
        var menus = new BazaarListener();
        await menus.Process(args);
        if (repeatMenu)
            await menus.Process(args);
        args.msg.ReceivedAt = observedAt.AddMilliseconds(chatDelayMs);
        await listener.Process(args);
        var remaining = currentState.BazaarOffers.Single();
        Assert.That(remaining.Created, Is.EqualTo(order.Created));
        Assert.That(remaining.FilledAmount - remaining.ClaimedAmount, Is.EqualTo(256));
        orderBookApi.Verify(a => a.RemoveOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 0, default), Times.Never);

        // A second, genuine withdrawal must still consume the remaining items.
        args.msg.ReceivedAt = observedAt.AddSeconds(5);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers, Is.Empty);
        orderBookApi.Verify(a => a.RemoveOrderAsync("GILL_MEMBRANE", "5", order.Created, 0, default), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MenuDoesNotReserveClaimsAlreadyReportedInChatOrHistoricalClaims(bool chatFirst)
    {
        var order = new Offer { ItemName = "Coal", ItemTag = "COAL", Amount = 1024,
            FilledAmount = 1024, ClaimedAmount = chatFirst ? 256 : null, PricePerUnit = 7.1,
            Created = DateTime.UtcNow.AddMinutes(-1) };
        currentState.BazaarOffers.Add(order);
        var args = CreateArgs("[Bazaar] Claimed 256x Coal worth 1,817.6 coins bought for 7.1 each!");
        args.AddService(new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient()).Object);
        if (chatFirst)
            await listener.Process(args);
        args.msg.ReceivedAt = args.msg.ReceivedAt.AddMilliseconds(5);
        args.msg.Chest = new ChestView { Name = "Co-op Bazaar Orders", Items = new() { new() {
            ItemName = "§a§lBUY §aCoal", Tag = "COAL",
            Description = "§7Order amount: §a1,024§7x\nFilled: 1k/1k 100%!\n§7Price per unit: §67.1 coins\nYou have 512 items to claim!"
        } } };
        await new BazaarListener().Process(args);
        args.msg.ReceivedAt = args.msg.ReceivedAt.AddSeconds(1);
        await listener.Process(args);
        Assert.That(currentState.BazaarOffers.Single().ClaimedAmount, Is.EqualTo(768));
    }

    private MockedUpdateArgs CreateArgs(params string[] msgs)
    {
        var args = new MockedUpdateArgs()
        {
            currentState = currentState,
            msg = new UpdateMessage()
            {
                ChatBatch = msgs.ToList(),
                UserId = "5",
                ReceivedAt = DateTime.UtcNow
            }
        };
        currentState.RecentViews.Enqueue(JsonConvert.DeserializeObject<ChestView>("""
        {"Items":[{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1},
        {"Id":null,"ItemName":"§aBuy Order","Tag":"MAGMA_CORE","ExtraAttributes":{},"Enchantments":null,"Color":null,
        "Description":"§8Bazaar\n\n§7Price per unit: §6545,659.6 coins\n\n§7Order: §a4§7x §9Magma Core\n§7Total price: §62,182,638 coins\n\n§bOrders are shared by co-op!\n\n§7§eClick to submit order!","Count":1},
        {"Id":null,"ItemName":" ","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"","Count":1}
        ],"Name":"Confirm Buy Order","Position":null,"OpenedAt":"2025-08-25T11:44:56.1953913Z"}
        """)!);
        itemsApi = new Mock<IItemsApi>();
        scheduleApi = new Mock<IScheduleApi>();
        orderBookApi = new Mock<IOrderBookApi>();
        itemsApi.Setup(i => i.ItemsSearchTermIdGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(5);
        args.AddService<IItemsApi>(itemsApi.Object);
        args.AddService<ITransactionService>(transactionService.Object);
        args.AddService(orderBookApi.Object);
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        args.AddService(scheduleApi.Object);

        return args;
    }
}
