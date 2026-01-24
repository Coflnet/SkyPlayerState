using System;
using System.Text.RegularExpressions;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Parses chat messages to extract coin transaction information
/// </summary>
public class CoinCounterParser
{
    // NPC sold: "You sold Enchanted Snow Block x1 for 600 Coins!"
    private static readonly Regex NpcSoldRegex = new Regex(
        @"You sold .+ for ([\d,\.kmb]+) Coins?!",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Bazaar sold: "[Bazaar] Your Enchanted Snow Block x10 sold for 6,000 coins!"
    private static readonly Regex BazaarSoldRegex = new Regex(
        @"\[Bazaar\] Your .+ sold for ([\d,\.kmb]+) coins?!",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Trade received: " + 2k coins" or " + 1,500 coins"
    private static readonly Regex TradeReceivedRegex = new Regex(
        @"^ \+ ([\d,\.kmb]+) coins?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Auction House sold: "[Auction] Your Auction for Aspect of the Dragons sold for 5,000,000 coins!"
    private static readonly Regex AuctionHouseSoldRegex = new Regex(
        @"\[Auction\] .+ sold for ([\d,\.kmb]+) coins?!",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Attempts to parse a chat message and extract coin transaction information
    /// </summary>
    /// <param name="chatMessage">The chat message to parse</param>
    /// <param name="type">The type of transaction if successful</param>
    /// <param name="amount">The amount of coins if successful</param>
    /// <returns>True if the message was successfully parsed</returns>
    public bool TryParse(string chatMessage, out CoinCounterType? type, out long amount)
    {
        type = null;
        amount = 0;

        if (string.IsNullOrWhiteSpace(chatMessage))
            return false;

        // Try NPC sold
        var match = NpcSoldRegex.Match(chatMessage);
        if (match.Success)
        {
            type = CoinCounterType.Npc;
            amount = Core.CoinParser.ParseCoinAmount(match.Groups[1].Value);
            return true;
        }

        // Try Bazaar sold
        match = BazaarSoldRegex.Match(chatMessage);
        if (match.Success)
        {
            type = CoinCounterType.Bazaar;
            amount = Core.CoinParser.ParseCoinAmount(match.Groups[1].Value);
            return true;
        }

        // Try Trade received
        match = TradeReceivedRegex.Match(chatMessage);
        if (match.Success)
        {
            type = CoinCounterType.Trade;
            amount = Core.CoinParser.ParseCoinAmount(match.Groups[1].Value);
            return true;
        }

        // Try Auction House sold
        match = AuctionHouseSoldRegex.Match(chatMessage);
        if (match.Success)
        {
            type = CoinCounterType.AuctionHouse;
            amount = Core.CoinParser.ParseCoinAmount(match.Groups[1].Value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the current day key for 6am UTC reset
    /// Day resets at 6am UTC, so 5:59am UTC is still the previous day
    /// </summary>
    /// <param name="timestamp">The timestamp to get the day for</param>
    /// <returns>A tuple of (year, dayOfYear)</returns>
    public static (int year, int dayOfYear) GetDayKey(DateTime timestamp)
    {
        // Adjust for 6am UTC reset
        var adjustedTime = timestamp.AddHours(-6);
        return (adjustedTime.Year, adjustedTime.DayOfYear);
    }
}
