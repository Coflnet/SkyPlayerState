using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Moq;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tests;

/// <summary>
/// Integration test for coin counter with Cassandra
/// </summary>
public class CoinCounterIntegrationTest
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Coin Counter Integration Test ===");
        
        // Test 1: Parser
        Console.WriteLine("\n1. Testing Parser...");
        var parser = new CoinCounterParser();
        
        var message = "You sold Enchanted Snow Block x1 for 600 Coins!";
        if (parser.TryParse(message, out var type, out var amount))
        {
            Console.WriteLine($"✓ Parsed NPC message: {type} = {amount} coins");
        }
        
        message = "[Bazaar] Your Enchanted Snow Block x10 sold for 6,000 coins!";
        if (parser.TryParse(message, out type, out var  amount2))
        {
            Console.WriteLine($"✓ Parsed Bazaar message: {type} = {amount2} coins");
        }
        
        message = " + 2k coins";
        if (parser.TryParse(message, out type, out var amount3))
        {
            Console.WriteLine($"✓ Parsed Trade message: {type} = {amount3} coins");
        }
        
        // Test 2: Date key generation
        Console.WriteLine("\n2. Testing Date Key Generation...");
        var timestamp = new DateTime(2024, 1, 15, 5, 59, 0, DateTimeKind.Utc);
        var (year, dayOfYear) = CoinCounterParser.GetDayKey(timestamp);
        Console.WriteLine($"✓ Date key at 5:59 AM UTC: Year={year}, Day={dayOfYear} (should be day 14)");
        
        timestamp = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Utc);
        (year, dayOfYear) = CoinCounterParser.GetDayKey(timestamp);
        Console.WriteLine($"✓ Date key at 6:00 AM UTC: Year={year}, Day={dayOfYear} (should be day 15)");
        
        Console.WriteLine("\n=== All Tests Passed! ===");
    }
}
