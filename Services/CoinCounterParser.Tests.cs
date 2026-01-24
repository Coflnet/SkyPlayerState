using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Tests;

public class CoinCounterParserTests
{
    private CoinCounterParser parser;

    [SetUp]
    public void Setup()
    {
        parser = new CoinCounterParser();
    }

    [Test]
    public void ParseNpcSoldMessage()
    {
        var message = "You sold Enchanted Snow Block x1 for 600 Coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Npc, type);
        // Core.CoinParser returns 600 as 6000 (multiplies by 10)
        ClassicAssert.AreEqual(6000, amount);
    }

    [Test]
    public void ParseNpcSoldMessageWithThousands()
    {
        var message = "You sold Diamond Block x32 for 5,000 Coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Npc, type);
        // Core.CoinParser returns 5000 as 50000 (multiplies by 10)
        ClassicAssert.AreEqual(50000, amount);
    }

    [Test]
    public void ParseBazaarSoldMessage()
    {
        var message = "[Bazaar] Your Enchanted Snow Block x10 sold for 6,000 coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Bazaar, type);
        // Core.CoinParser returns 6000 as 60000 (multiplies by 10)
        ClassicAssert.AreEqual(60000, amount);
    }

    [Test]
    public void ParseBazaarSoldMessageLargeAmount()
    {
        var message = "[Bazaar] Your Summoning Eye x100 sold for 25,500,000 coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Bazaar, type);
        // Core.CoinParser returns 25500000 as 255000000 (multiplies by 10)
        ClassicAssert.AreEqual(255000000, amount);
    }

    [Test]
    public void ParseTradeReceivedMessage()
    {
        var message = " + 2k coins";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Trade, type);
        // Core.CoinParser returns 2000 as 20000 (multiplies by 10)
        ClassicAssert.AreEqual(20000, amount);
    }

    [Test]
    public void ParseTradeReceivedMessageWithCommas()
    {
        var message = " + 1,500 coins";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Trade, type);
        // Core.CoinParser returns 1500 as 15000 (multiplies by 10)
        ClassicAssert.AreEqual(15000, amount);
    }

    [Test]
    public void ParseTradeReceivedMessageMillion()
    {
        // Note: Core.CoinParser returns 5,000,000 as 50,000,000 (multiplies by 10)
        var message = " + 5,000,000 coins";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.Trade, type);
        ClassicAssert.AreEqual(50000000, amount);
    }

    [Test]
    public void ParseAuctionHouseSoldMessage()
    {
        var message = "[Auction] Your Auction for Aspect of the Dragons sold for 5,000,000 coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.AuctionHouse, type);
        // Core.CoinParser returns 5000000 as 50000000 (multiplies by 10)
        ClassicAssert.AreEqual(50000000, amount);
    }

    [Test]
    public void ParseAuctionHouseSoldMessageShort()
    {
        var message = "[Auction] Your item sold for 100k coins!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsTrue(success);
        ClassicAssert.AreEqual(CoinCounterType.AuctionHouse, type);
        // Core.CoinParser returns 100000 as 1000000 (multiplies by 10)
        ClassicAssert.AreEqual(1000000, amount);
    }

    [Test]
    public void ShouldNotParseUnrelatedMessage()
    {
        var message = "Hello world!";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsFalse(success);
        ClassicAssert.IsNull(type);
        ClassicAssert.AreEqual(0, amount);
    }

    [Test]
    public void ShouldNotParseTradeNegativeMessage()
    {
        var message = " - 2k coins";
        
        var success = parser.TryParse(message, out var type, out var amount);
        
        ClassicAssert.IsFalse(success);
    }

    [Test]
    public void TestDayKeyCalculation()
    {
        // Test at 5:59 AM UTC (should be previous day)
        var beforeReset = new DateTime(2024, 1, 15, 5, 59, 0, DateTimeKind.Utc);
        var (year1, day1) = CoinCounterParser.GetDayKey(beforeReset);
        
        ClassicAssert.AreEqual(2024, year1);
        ClassicAssert.AreEqual(14, day1); // Should be day 14, not 15
        
        // Test at 6:00 AM UTC (should be current day)
        var atReset = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Utc);
        var (year2, day2) = CoinCounterParser.GetDayKey(atReset);
        
        ClassicAssert.AreEqual(2024, year2);
        ClassicAssert.AreEqual(15, day2);
        
        // Test at 6:01 AM UTC (should be current day)
        var afterReset = new DateTime(2024, 1, 15, 6, 1, 0, DateTimeKind.Utc);
        var (year3, day3) = CoinCounterParser.GetDayKey(afterReset);
        
        ClassicAssert.AreEqual(2024, year3);
        ClassicAssert.AreEqual(15, day3);
    }
}
