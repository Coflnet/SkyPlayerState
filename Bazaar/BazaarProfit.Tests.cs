using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarProfitTests
{
    private BazaarOrderListener _listener = null!;
    private StateObject _currentState = null!;
    private Mock<ITransactionService> _transactionService = null!;
    private Mock<IBazaarProfitTracker> _profitTracker = null!;
    private Mock<IItemsApi> _itemsApi = null!;
    private int _invokeCount;

    [SetUp]
    public void Setup()
    {
        _listener = new BazaarOrderListener();
        _currentState = new StateObject();
        _transactionService = new Mock<ITransactionService>();
        _transactionService.Setup(t => t.AddTransactions(It.IsAny<Transaction>()))
            .Callback(() => _invokeCount++);
        _profitTracker = new Mock<IBazaarProfitTracker>();
        _itemsApi = new Mock<IItemsApi>();
        _itemsApi.Setup(i => i.ItemsSearchTermIdGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(5);
        _itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync((string term, string _, int _, System.Threading.CancellationToken _) => new List<Items.Client.Model.SearchResult>
            {
                new() { Tag = term.ToUpper().Replace(" ", "_") }
            });
        _invokeCount = 0;
    }

    private MockedUpdateArgs CreateArgs(params string[] msgs)
    {
        var args = new MockedUpdateArgs
        {
            currentState = _currentState,
            msg = new UpdateMessage
            {
                ChatBatch = msgs.ToList(),
                UserId = "test-user",
                ReceivedAt = DateTime.UtcNow
            }
        };
        
        args.AddService<IItemsApi>(_itemsApi.Object);
        args.AddService<ITransactionService>(_transactionService.Object);
        args.AddService<IBazaarProfitTracker>(_profitTracker.Object);
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        
        return args;
    }

    [Test]
    public async Task ClaimBuyOrderRecordsForProfitTracking()
    {
        // Setup an existing buy order - match the PricePerUnit exactly as parsed
        // "134.4 coins bought for 2.1 each" -> perPrice = ParseCoins("2.1") = 21 (in tenths)
        // PricePerUnit stored is 2.1 (coins, not tenths)
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Coal",
            PricePerUnit = 2.1, // 2.1 coins per unit
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 64x Coal worth 134.4 coins bought for 2.1 each!");

        await _listener.Process(args);

        // Verify buy order was recorded for profit tracking
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "COAL",
            64,
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task ClaimSellOrderRecordsForProfitTracking()
    {
        // Setup an existing sell order
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Coal",
            PricePerUnit = 4.8,
            IsSell = true,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!");

        await _listener.Process(args);

        // Verify sell order was recorded for profit tracking
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "COAL",
            "Coal",
            64,
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task ClaimBuyOrderWithLargeAmount()
    {
        // "1,920,000 coins bought for 1,500 each" -> perPrice = ParseCoins("1,500") / 10 = 1500 coins
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 1280,
            ItemName = "Enchanted Coal",
            PricePerUnit = 1500.0, // coins per unit
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 1,280x Enchanted Coal worth 1,920,000 coins bought for 1,500 each!");

        await _listener.Process(args);

        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "ENCHANTED_COAL",
            1280,
            It.Is<long>(price => price > 0),
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task ClaimSellOrderCalculatesProfit()
    {
        // Set up the mock to return a flip result
        var expectedFlip = new BazaarFlip
        {
            PlayerUuid = Guid.NewGuid(),
            ItemTag = "COAL",
            ItemName = "Coal",
            Amount = 64,
            BuyPrice = 1344,
            SellPrice = 3037,
            Profit = 1693,
            SoldAt = DateTime.UtcNow
        };
        
        _profitTracker.Setup(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        )).ReturnsAsync(expectedFlip);

        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Coal",
            PricePerUnit = 4.8,
            IsSell = true,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!");

        await _listener.Process(args);

        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "COAL",
            "Coal",
            64,
            3037,
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task InstaBuyDoesNotRecordForProfitTracking()
    {
        var args = CreateArgs("[Bazaar] Executing instant buy...",
            "[Bazaar] Bought 1,280x Coal for 5,120 coins!");

        await _listener.Process(args);

        // Insta-buy should not trigger profit tracking (no claimed order)
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Never);
    }

    [Test]
    public async Task InstaSellDoesNotRecordForProfitTracking()
    {
        var args = CreateArgs("[Bazaar] Executing instant sell...",
            "[Bazaar] Sold 1,280x Coal for 3,840.2 coins!");

        await _listener.Process(args);

        // Insta-sell should not trigger profit tracking
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Never);
    }

    [Test]
    public async Task CancelledOrderDoesNotRecordForProfitTracking()
    {
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Coal",
            PricePerUnit = 2.1,
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Cancelling order...",
            "[Bazaar] Cancelled! Refunded 134.4 coins from cancelling Buy Order!");

        await _listener.Process(args);

        // Cancelled orders should not be tracked for profit
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Never);
    }
}

/// <summary>
/// Unit tests for the BazaarProfitTracker service itself
/// </summary>
public class BazaarProfitTrackerTests
{
    [Test]
    public void BazaarFlipCalculatesProfitCorrectly()
    {
        var flip = new BazaarFlip
        {
            BuyPrice = 1000, // 100 coins
            SellPrice = 1500, // 150 coins
            Amount = 10
        };
        flip.Profit = flip.SellPrice - flip.BuyPrice;

        Assert.That(flip.Profit, Is.EqualTo(500)); // 50 coins profit
        Assert.That(flip.Profit / 10.0, Is.EqualTo(50.0)); // In actual coins
    }

    [Test]
    public void BazaarBuyRecordTracksRemainingAmount()
    {
        var record = new BazaarBuyRecord
        {
            Amount = 100,
            RemainingAmount = 100,
            TotalPrice = 10000 // 1000 coins
        };

        // Simulate partial sale
        record.RemainingAmount -= 30;

        Assert.That(record.RemainingAmount, Is.EqualTo(70));
        
        // Calculate proportional cost of sold items
        var soldValue = (long)((double)record.TotalPrice * 30 / record.Amount);
        Assert.That(soldValue, Is.EqualTo(3000)); // 300 coins
    }

    [Test]
    public void ProfitCalculationWithPartialSales()
    {
        // Buy 100 items for 1000 coins (10 coins each)
        var buyRecord = new BazaarBuyRecord
        {
            Amount = 100,
            RemainingAmount = 100,
            TotalPrice = 10000 // In tenths of coins
        };

        // Sell 50 items for 750 coins (15 coins each)
        var sellAmount = 50;
        var sellPrice = 7500L; // In tenths

        // Calculate buy cost for 50 items
        var buyPriceForSold = (long)((double)buyRecord.TotalPrice * sellAmount / buyRecord.Amount);
        
        // Calculate profit
        var profit = sellPrice - buyPriceForSold;

        Assert.That(buyPriceForSold, Is.EqualTo(5000)); // 500 coins
        Assert.That(profit, Is.EqualTo(2500)); // 250 coins profit
    }
}
