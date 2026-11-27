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

    private static readonly Regex ExactAmountRegex = new(@"^\((\d{1,3}(?:,\d{3})*)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Amount in tenths of a coin. Prefers the exact figure from the lore, e.g. "(1,152,000,000)",
    /// because the item name only holds an abbreviation like "1.2B".
    /// </summary>
    public long GetCoinAmount(Item item)
    {
        if (!IsCoins(item))
            return 0;
        if (TryParseExactAmount(item.Description, out var exact))
            return exact * 10;
        return ParseCoinAmount(ExtractAmountFromName(item.ItemName!));
    }

    private static bool TryParseExactAmount(string? description, out long coins)
    {
        coins = 0;
        if (string.IsNullOrEmpty(description))
            return false;
        var lines = MinecraftFormattingRegex.Replace(description, string.Empty).Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var match = ExactAmountRegex.Match(lines[i].Trim());
            if (!match.Success)
                continue;
            return long.TryParse(match.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out coins)
                && coins <= long.MaxValue / 10;
        }
        return false;
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