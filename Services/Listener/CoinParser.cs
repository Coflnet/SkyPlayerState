using System;
using System.Globalization;
using Coflnet.Sky.PlayerState.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

public class CoinParser
{
    private static readonly Regex MinecraftFormattingRegex = new("§.", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public long GetCoinAmount(Item item)
    {
        if (IsCoins(item))
        {
            return ParseCoinAmount(ExtractAmountFromName(item.ItemName!));
        }
        return 0;
    }

    /// <summary>
    /// Extracts the numeric portion from a coin item name (e.g. "§67M coins" or "7M coins" => "7M").
    /// Strips minecraft formatting codes and the trailing " coins" suffix instead of relying on
    /// fixed offsets, which break when the color prefix is missing.
    /// </summary>
    private static string ExtractAmountFromName(string itemName)
    {
        var cleaned = MinecraftFormattingRegex.Replace(itemName, string.Empty);
        if (cleaned.EndsWith(" coins", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^" coins".Length];
        return cleaned.Trim();
    }

    private static long ParseCoinAmount(string stringAmount)
    {
        return Core.CoinParser.ParseCoinAmount(stringAmount);
    }

    public long GetInventoryCoinSum(IEnumerable<Item> items)
    {
        if(Core.CoinParser.TryParseFromDescription(items.Select(i => i.Description), out var result))
        {
            return result;
        }
        return items.Sum(GetCoinAmount);
    }

    internal bool IsCoins(Item item)
    {
        return item.ItemName?.EndsWith(" coins") ?? false;
    }
}