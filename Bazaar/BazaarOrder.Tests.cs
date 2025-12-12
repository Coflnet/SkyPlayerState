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
    public async Task SellOrderCreateAndFill()
    {
        UpdateArgs args = CreateArgs("[Bazaar] Submitting sell order...",
                    "[Bazaar] Sell Offer Setup! 64x Coal for 208.8 coins.");
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

        scheduleApi.Verify(s => s.ScheduleUserIdPostAsync("5", It.IsAny<DateTime>(), It.Is<MessageContainer>(con =>
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

    private MockedUpdateArgs CreateArgs(params string[] msgs)
    {
        var args = new MockedUpdateArgs()
        {
            currentState = currentState,
            msg = new UpdateMessage()
            {
                ChatBatch = msgs.ToList(),
                UserId = "5"
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
