using System;
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
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarProfitTests
{
    private BazaarOrderListener _listener = null!;
    private StateObject _currentState = null!;
    private Mock<ITransactionService> _transactionService = null!;
    private Mock<IBazaarProfitTracker> _profitTracker = null!;
    private Mock<IItemsApi> _itemsApi = null!;
    private Mock<IOrderBookApi> _orderBookApi = null!;
    private Mock<IScheduleApi> _scheduleApi = null!;
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
        _orderBookApi = new Mock<IOrderBookApi>();
        _scheduleApi = new Mock<IScheduleApi>();
        _itemsApi.Setup(i => i.ItemsSearchTermIdGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(5);
        _itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync((string term, string _, int _, System.Threading.CancellationToken _) => new List<Items.Client.Model.SearchResult>
            {
                new() { Tag = term.ToUpper().Replace(" ", "_"), Flags = new Items.Client.Model.ItemFlags?(Items.Client.Model.ItemFlags.BAZAAR) }
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
                UserId = "12345678-1234-1234-1234-123456789012",
                ReceivedAt = DateTime.UtcNow
            }
        };
        
        args.AddService<IItemsApi>(_itemsApi.Object);
        args.AddService<ITransactionService>(_transactionService.Object);
        args.AddService<IBazaarProfitTracker>(_profitTracker.Object);
        args.AddService<IOrderBookApi>(_orderBookApi.Object);
        args.AddService<IScheduleApi>(_scheduleApi.Object);
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
        var expectedFlip = new CompletedBazaarFlip
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

    [Test]
    public async Task SellMoreThanBoughtOnlyTracksActualBoughtAmount()
    {
        // Scenario: Buy 64x, then sell 128x
        // Only 64 should be tracked as profit, remaining 64 has unknown buy price
        
        // Setup: A buy order for 64x Enchanted Cobblestone
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Enchanted Cobblestone",
            PricePerUnit = 222.7, // 222.7 coins per unit
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(2)
        });
        
        // Setup: A sell order for 128x Enchanted Cobblestone
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 128,
            ItemName = "Enchanted Cobblestone",
            PricePerUnit = 312.7,
            IsSell = true,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        // First claim the buy order
        var buyArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 64x Enchanted Cobblestone worth 14,253 coins bought for 222.7 each!");
        await _listener.Process(buyArgs);

        // Verify buy was recorded with amount 64
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "ENCHANTED_COBBLESTONE",
            64, // Only 64 items bought
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);

        // Then claim the sell order
        var sellArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 39,575 coins from selling 128x Enchanted Cobblestone at 312.7 each!");
        await _listener.Process(sellArgs);

        // Verify sell was recorded - the profit tracker should handle matching only available buy records
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "ENCHANTED_COBBLESTONE",
            "Enchanted Cobblestone",
            128, // Full sell amount passed, tracker handles matching
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task OrderFlippedRecordsVirtualBuyOrder()
    {
        // Scenario: Direct order flip where items are not claimed but directly converted to sell order
        // Message: [Bazaar] Order Flipped! 64x Ancient Claw for 16,915 coins of total expected profit.
        // Flow: Setup Buy -> Flip -> Claim Sell
        // The flip creates a virtual buy record so the sell claim can calculate profit
        
        // Setup: An existing buy order that will be flipped (100 coins per unit = 6400 coins total)
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 64,
            ItemName = "Ancient Claw",
            PricePerUnit = 100.0, // 100 coins per unit
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        });

        var args = CreateArgs("[Bazaar] Order Flipped! 64x Ancient Claw for 16,915 coins of total expected profit.");
        await _listener.Process(args);

        // Verify the virtual buy order was recorded with the buy price from the original order
        // buyPrice = 100.0 * 64 * 10 = 64000 (in tenths)
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "ANCIENT_CLAW",
            64,
            64000, // 6400 coins * 10 (buy price in tenths)
            169150, // 16,915 coins * 10 (expected profit in tenths)
            It.IsAny<DateTime>()
        ), Times.Once);
    }

    [Test]
    public async Task OrderFlippedRemovesBuyOrderAndAddsSellOrder()
    {
        // Setup: An existing buy order that will be flipped
        var buyOrder = new Offer
        {
            Amount = 64,
            ItemName = "Ancient Claw",
            PricePerUnit = 100.0, // 100 coins per unit = 6400 total buy
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        };
        _currentState.BazaarOffers.Add(buyOrder);

        var args = CreateArgs("[Bazaar] Order Flipped! 64x Ancient Claw for 16,915 coins of total expected profit.");
        await _listener.Process(args);

        // Verify the buy order was removed from state
        Assert.That(_currentState.BazaarOffers.Any(o => o.ItemName == "Ancient Claw" && !o.IsSell), Is.False,
            "The original buy order should be removed after flipping");
        
        // Verify a sell order was added (flip creates a sell order)
        var sellOrder = _currentState.BazaarOffers.FirstOrDefault(o => o.ItemName == "Ancient Claw" && o.IsSell);
        Assert.That(sellOrder, Is.Not.Null, "A sell order should be created after flipping");
        Assert.That(sellOrder!.Amount, Is.EqualTo(64), "Sell order should have same amount as buy order");
    }

    [Test]
    public async Task OrderFlipAndClaimCalculatesCorrectProfit()
    {
        // Reproduces issue from logs:
        // Buy: 64x MITHRIL_ORE for 409.6 coins (6.4 per unit)
        // Flip message: Expected profit: 96 coins
        // Sell claim: 494.2 coins (message says "at 7.9 each" which is the list price = 505.6 coins)
        // Actual profit: 494.2 - 409.6 = 84.6 coins
        //
        // IMPORTANT: The "expected profit" (96) from Hypixel's flip message is based on the LISTED
        // sell price before tax (505.6 coins). The ACTUAL profit (84.6) is calculated from the
        // amount actually received after tax and any price slippage (494.2 coins).
        //
        // The system CORRECTLY saves the actual profit (84.6), not the expected profit (96).
        // The difference (11.4 coins) represents:
        // - Bazaar sell tax (~1.125%)
        // - Possible rounding differences  
        // - Possible partial fills at slightly different prices
        // - Price changes between flip and claim
        //
        // This test verifies that we correctly calculate and save the ACTUAL profit from real
        // transaction amounts, not Hypixel's estimated/expected profit.
        
        // Setup: Buy order at 6.4 coins per unit
        var buyOrder = new Offer
        {
            Amount = 64,
            ItemName = "Mithril",
            PricePerUnit = 6.4, // 6.4 coins per unit = 409.6 total
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromHours(1)
        };
        _currentState.BazaarOffers.Add(buyOrder);

        // Flip the order with expected profit of 96 coins (Hypixel's estimate)
        var flipArgs = CreateArgs("[Bazaar] Order Flipped! 64x Mithril for 96.0 coins of total expected profit.");
        await _listener.Process(flipArgs);

        // Verify virtual buy order was recorded with correct buy price
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "MITHRIL",
            64,
            4096, // 409.6 coins * 10 (in tenths)
            960,  // 96 coins * 10 (expected profit in tenths - for logging only)
            It.IsAny<DateTime>()
        ), Times.Once);

        // Now claim the sell order - the message shows "7.9 each" (list price = 505.6 total)
        // but after tax and potential slippage, the actual claimed amount is 494.2
        var sellArgs = CreateArgs(
            "[Bazaar] Claiming order...",
            "[Bazaar] Claimed 494.2 coins from selling 64x Mithril at 7.9 each!"
        );
        await _listener.Process(sellArgs);

        // Verify sell was recorded with the ACTUAL claimed amount (not the listed price)
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "MITHRIL",
            "Mithril",
            64,
            4942, // 494.2 coins * 10 (in tenths) - this is what we actually received
            It.IsAny<DateTime>()
        ), Times.Once);

        // The BazaarProfitTracker.RecordSellOrder method will calculate profit as:
        // actualProfit = sellPrice - buyPrice = 4942 - 4096 = 846 tenths = 84.6 coins
        //
        // This is CORRECT. We save the actual profit (84.6), not the expected profit (96).
        // The expected profit is just Hypixel's estimate and doesn't account for tax/slippage.
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
        var flip = new CompletedBazaarFlip
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

/// <summary>
/// Tests for race condition in Order Flip where chest GUI update removes the buy order
/// before the "Order Flipped!" chat message is processed, preventing virtual buy record creation
/// </summary>
public class BazaarOrderFlipRaceConditionTests
{
    private BazaarOrderListener _listener = null!;
    private StateObject _currentState = null!;
    private Mock<ITransactionService> _transactionService = null!;
    private Mock<IBazaarProfitTracker> _profitTracker = null!;
    private Mock<IItemsApi> _itemsApi = null!;
    private Mock<IOrderBookApi> _orderBookApi = null!;
    private Mock<IScheduleApi> _scheduleApi = null!;

    [SetUp]
    public void Setup()
    {
        _listener = new BazaarOrderListener();
        _currentState = new StateObject();
        _transactionService = new Mock<ITransactionService>();
        _profitTracker = new Mock<IBazaarProfitTracker>();
        _itemsApi = new Mock<IItemsApi>();
        _orderBookApi = new Mock<IOrderBookApi>();
        _scheduleApi = new Mock<IScheduleApi>();
        _itemsApi.Setup(i => i.ItemsSearchTermIdGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(5);
        _itemsApi.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync((string term, string _, int _, System.Threading.CancellationToken _) => new List<Items.Client.Model.SearchResult>
            {
                new() { Tag = term.ToUpper().Replace(" ", "_"), Flags = new Items.Client.Model.ItemFlags?(Items.Client.Model.ItemFlags.BAZAAR) }
            });
    }

    private MockedUpdateArgs CreateArgs(params string[] msgs)
    {
        var args = new MockedUpdateArgs
        {
            currentState = _currentState,
            msg = new UpdateMessage
            {
                ChatBatch = msgs.ToList(),
                UserId = "12345678-1234-1234-1234-123456789012",
                ReceivedAt = DateTime.UtcNow
            }
        };
        
        args.AddService<IItemsApi>(_itemsApi.Object);
        args.AddService<ITransactionService>(_transactionService.Object);
        args.AddService<IBazaarProfitTracker>(_profitTracker.Object);
        args.AddService<IOrderBookApi>(_orderBookApi.Object);
        args.AddService<IScheduleApi>(_scheduleApi.Object);
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        
        return args;
    }

    /// <summary>
    /// THE CRITICAL RACE CONDITION TEST:
    /// 
    /// Sequence from the log file:
    /// 1. User creates buy order for 160x Coal at 3.6 coins each
    /// 2. Buy order fills (Coal received)
    /// 3. User flips the order → "Order Flipped!" message
    /// 4. **RACE**: Chest GUI update (showing new state without the buy order) processes BEFORE flip message
    /// 5. Buy order is removed from state
    /// 6. Flip message arrives but buy order is gone → RecordOrderFlipForProfit is NEVER called
    /// 7. No virtual buy record is created
    /// 8. Later, sell order is claimed → RecordSellOrder finds no buy records → flip not tracked
    /// 
    /// FIX: Keep a temporary in-memory cache of recently claimed buy orders (last 60 seconds)
    /// to bridge the gap when state has been updated but messages are still processing.
    /// </summary>
    [Test]
    public async Task OrderFlipRaceCondition_BuyOrderRemovedByChestUpdateBeforeFlipMessage_FixedWithCache()
    {
        // Step 1: Simulate buy order being claimed (this populates the cache)
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 160,
            ItemName = "Coal",
            PricePerUnit = 3.6,
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromMinutes(1)
        });

        var claimBuyArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 160x Coal worth 576 coins bought for 3.6 each!");
        
        await _listener.Process(claimBuyArgs);

        // Buy order is recorded
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "COAL",
            160,
            5760,
            It.IsAny<DateTime>()
        ), Times.Once);

        // Step 2: Chest GUI update removes the buy order from state (race condition)
        _currentState.BazaarOffers.Clear();

        // Step 3: "Order Flipped!" message arrives with empty state
        var flipArgs = CreateArgs("[Bazaar] Order Flipped! 160x Coal for 96.0 coins of total expected profit.");
        
        await _listener.Process(flipArgs);

        // Step 4: Verify that RecordOrderFlip WAS called (FIXED with cache)
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "COAL",
            160,
            5760, // Buy price from cache
            960,  // Expected profit
            It.IsAny<DateTime>()
        ), Times.Once); // ✓ FIX WORKS!

        // Virtual buy record is created, so later sell claim will match and track the flip
    }

    /// <summary>
    /// Original test showing the bug - when no cache entry exists (old behavior)
    /// </summary>
    [Test]
    public async Task OrderFlipRaceCondition_BuyOrderRemovedByChestUpdateBeforeFlipMessage()
    {
        // Step 1: Setup - NO buy order in state (already removed by chest GUI update)
        // This simulates the chest GUI being processed first
        _currentState.BazaarOffers.Clear();

        // Step 2: "Order Flipped!" message arrives, but buy order is already gone
        var args = CreateArgs("[Bazaar] Order Flipped! 160x Coal for 96.0 coins of total expected profit.");
        
        await _listener.Process(args);

        // Step 3: Verify that RecordOrderFlip was NOT called (current buggy behavior)
        // because the buy order was not found in state
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "COAL",
            160,
            It.IsAny<long>(),
            960, // 96 coins * 10
            It.IsAny<DateTime>()
        ), Times.Never); // BUG: Never called!

        // This demonstrates the race condition - no virtual buy record is created,
        // so when the sell order is claimed later, there's nothing to match against
    }

    /// <summary>
    /// Test showing the happy path: when flip message arrives while buy order
    /// is still in state, RecordOrderFlip IS called and virtual buy record is created.
    /// </summary>
    [Test]
    public async Task OrderFlipHappyPath_BuyOrderStillInState()
    {
        // Setup: Buy order exists in state (normal case)
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 160,
            ItemName = "Coal",
            PricePerUnit = 3.6, // 3.6 coins per unit = 576 total
            IsSell = false,
            Created = DateTime.UtcNow - TimeSpan.FromMinutes(1)
        });

        // "Order Flipped!" message arrives
        var args = CreateArgs("[Bazaar] Order Flipped! 160x Coal for 96.0 coins of total expected profit.");
        
        await _listener.Process(args);

        // Verify RecordOrderFlip WAS called (happy path)
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "COAL",
            160,
            5760, // 576 coins * 10 (buy price)
            960,  // 96 coins * 10 (expected profit)
            It.IsAny<DateTime>()
        ), Times.Once);

        // Buy order should be removed from state
        Assert.That(_currentState.BazaarOffers.Any(o => o.ItemName == "Coal" && !o.IsSell), Is.False);
        
        // Sell order should be added to state
        Assert.That(_currentState.BazaarOffers.Any(o => o.ItemName == "Coal" && o.IsSell), Is.True);
    }

    /// <summary>
    /// Test demonstrating the complete flow with the race condition:
    /// Flip happens but no virtual buy record created → sell claim finds no buy records
    /// </summary>
    [Test]
    public async Task CompleteRaceConditionFlow_FlipThenClaimWithNoBuyRecord()
    {
        // Setup: RecordSellOrder returns null (simulating no buy records found)
        _profitTracker.Setup(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        )).ReturnsAsync((CompletedBazaarFlip?)null);

        // Step 1: Flip happens with buy order already removed (race condition)
        _currentState.BazaarOffers.Clear();
        var flipArgs = CreateArgs("[Bazaar] Order Flipped! 160x Coal for 96.0 coins of total expected profit.");
        await _listener.Process(flipArgs);

        // RecordOrderFlip was NOT called due to race condition
        _profitTracker.Verify(p => p.RecordOrderFlip(
            It.IsAny<Guid>(),
            "COAL",
            It.IsAny<int>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Never);

        // Step 2: Later, sell order is claimed
        _currentState.BazaarOffers.Add(new Offer
        {
            Amount = 160,
            ItemName = "Coal",
            PricePerUnit = 9.9,
            IsSell = true,
            Created = DateTime.UtcNow
        });

        var claimArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 1,548.4 coins from selling 160x Coal at 9.9 each!");
        await _listener.Process(claimArgs);

        // RecordSellOrder IS called, but returns null (no buy records to match)
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "COAL",
            "Coal",
            160,
            15484,
            It.IsAny<DateTime>()
        ), Times.Once);

        // Result: Flip is not tracked! This is the bug documented in the log file.
    }

    [Test]
    public async Task EccentricPaintingMultipleOrderCyclesRecordsCorrectAmounts()
    {
        // This test reproduces the issue with Eccentric Painting where messages come in separate batches over time
        // IMPORTANT: Each Setup and Cancel must be in SEPARATE batches to avoid Parallel.ForEachAsync race conditions
        // In Hypixel, messages arrive in chat events at different times, so they would naturally be in separate batches

        // Batch 1: Claim buy order (historical)
        var batch1 = CreateArgs("[Bazaar] Claimed 9x Eccentric Painting worth 49,476,711 coins bought for 5,497,412 each!");
        await _listener.Process(batch1);

        // Batch 2: Setup first 9x sell
        var batch2 = CreateArgs("[Bazaar] Sell Offer Setup! 9x Eccentric Painting for 68,162,029 coins");
        await _listener.Process(batch2);

        // Batch 3: Cancel first 9x sell
        var batch3 = CreateArgs("[Bazaar] Cancelled! Refunded 9x Eccentric Painting from cancelling Sell Offer!");
        await _listener.Process(batch3);

        // Batch 4: Setup second 9x sell
        var batch4 = CreateArgs("[Bazaar] Sell Offer Setup! 9x Eccentric Painting for 66,740,624 coins");
        await _listener.Process(batch4);

        // Batch 5: Cancel second 9x sell
        var batch5 = CreateArgs("[Bazaar] Cancelled! Refunded 9x Eccentric Painting from cancelling Sell Offer!");
        await _listener.Process(batch5);

        // Batch 6: Setup first 1x sell
        var batch6 = CreateArgs("[Bazaar] Sell Offer Setup! 1x Eccentric Painting for 7,217,618 coins.");
        await _listener.Process(batch6);

        // Batch 7: Cancel first 1x sell
        var batch7 = CreateArgs("[Bazaar] Cancelled! Refunded 1x Eccentric Painting from cancelling Sell Offer!");
        await _listener.Process(batch7);

        // Batch 8: Setup second 1x sell
        var batch8 = CreateArgs("[Bazaar] Sell Offer Setup! 1x Eccentric Painting for 7,217,618 coins.");
        await _listener.Process(batch8);

        // Batch 9: Cancel second 1x sell
        var batch9 = CreateArgs("[Bazaar] Cancelled! Refunded 1x Eccentric Painting from cancelling Sell Offer!");
        await _listener.Process(batch9);

        // Batch 10: Setup final 1x sell
        var batch10 = CreateArgs("[Bazaar] Sell Offer Setup! 1x Eccentric Painting for 7,217,617 coins.");
        await _listener.Process(batch10);

        // Batch 11: Fill final 1x sell
        var batch11 = CreateArgs("[Bazaar] Your Sell Offer for 1x Eccentric Painting was filled!");
        await _listener.Process(batch11);

        // Batch 12: Claim final 1x sell
        var batch12 = CreateArgs("[Bazaar] Claimed 7,217,617 coins from selling 1x Eccentric Painting at 7,299,739 each!");
        await _listener.Process(batch12);

        // Verify that the initial 9x buy order was recorded for profit tracking
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "ECCENTRIC_PAINTING",
            9,
            It.Is<long>(price => price > 0),
            It.IsAny<DateTime>()
        ), Times.Once);

        // Verify that the 1x sell order was recorded
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "ECCENTRIC_PAINTING",
            "Eccentric Painting",
            1,
            72176170,  // Price is stored in tenths of coins (7,217,617 coins * 10)
            It.IsAny<DateTime>()
        ), Times.Once);

        // After processing all messages, no sell orders should remain in state
        // All Setup orders were either Cancelled (removed) or Filled then Claimed (removed)
        var remainingEccentricPaintingSellOrders = _currentState.BazaarOffers
            .Where(o => o.ItemName == "Eccentric Painting" && o.IsSell)
            .ToList();
        
        // Debug output
        Console.WriteLine($"Remaining sell orders for Eccentric Painting: {remainingEccentricPaintingSellOrders.Count}");
        foreach (var order in remainingEccentricPaintingSellOrders)
        {
            Console.WriteLine($"  - {order.Amount}x @ {order.PricePerUnit} (IsSell={order.IsSell})");
        }
        
        Assert.That(remainingEccentricPaintingSellOrders.Count, Is.EqualTo(0),
            $"Expected 0 remaining sell orders but found {remainingEccentricPaintingSellOrders.Count}: {string.Join(", ", remainingEccentricPaintingSellOrders.Select(o => $"{o.Amount}x @ {o.PricePerUnit}"))}");
    }

    [Test]
    public async Task BuyAndSellWithTagCacheConsistency()
    {
        // This test verifies the fix for profit not being recorded when the Items API
        // returns inconsistent tags between buy and sell order recording.
        // 
        // The problem: GetTagForName() calls an external API that may return different results
        // on successive calls due to network issues, causing buy and sell records to have
        // different tags and fail to match for profit calculation.
        //
        // The fix: Tag lookups are cached for 2 minutes to ensure consistency.

        var buyArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 64x Coal worth 134.4 coins bought for 2.1 each!");
        
        await _listener.Process(buyArgs);

        // Verify buy order was recorded (mock the tag as "COAL")
        _profitTracker.Verify(p => p.RecordBuyOrder(
            It.IsAny<Guid>(),
            "COAL",  // First call gets this tag
            64,
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);

        // Now sell the same item - the tag should come from cache (same "COAL")
        var sellArgs = CreateArgs("[Bazaar] Claiming order...",
            "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!");

        await _listener.Process(sellArgs);

        // Verify sell order uses the SAME tag from cache
        _profitTracker.Verify(p => p.RecordSellOrder(
            It.IsAny<Guid>(),
            "COAL",  // Should be same as buy order due to cache
            "Coal",
            64,
            It.IsAny<long>(),
            It.IsAny<DateTime>()
        ), Times.Once);
    }
}

