using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

public partial class BitListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name == null)
            return;

        var chestName = args.msg.Chest.Name;
        string? shopType = null;

        // Only process if it's a Community Shop or Bits Shop; store the full chest name
        if (chestName.StartsWith("Community Shop") || chestName.StartsWith("Bits Shop"))
            shopType = chestName;
        else
            return;

        var service = args.GetService<IBitService>();

        // Process each item in the inventory
        foreach (var item in args.msg.Chest.Items ?? new List<Item>())
        {
            if (string.IsNullOrEmpty(item.Tag) || item.Description == null)
                continue;

            var bitValue = ParseBitValue(item.Description);
            if (bitValue == null)
                continue;

            await service.StoreTagToBitMapping(new BitTagMapping
            {
                ShopName = shopType,
                ItemTag = item.Tag,
                BitValue = bitValue.Value
            });
        }
    }

    /// <summary>
    /// Parses the bit value from an item's description/lore
    /// Looks for patterns like "Cost\n{amount} Bits" with Minecraft color codes
    /// </summary>
    /// <param name="description">Item description containing bit cost information</param>
    /// <returns>The parsed bit value or null if not found</returns>
    public static long? ParseBitValue(string description)
    {
        // Try to match patterns like "Cost\n1,500 Bits" (with Minecraft color codes like §b, §7)
        // Remove the color code characters (§ followed by any character)
        var cleanDescription = System.Text.RegularExpressions.Regex.Replace(description, "§.", "");
        var match = BitCostRegex().Match(cleanDescription);
        if (match.Success)
        {
            var amountString = match.Groups[1].Value.Replace(",", "");
            if (long.TryParse(amountString, out var amount))
                return amount;
        }

        return null;
    }

    [GeneratedRegex(@"Cost[:\s]*(\d+(?:,\d+)*)\s*Bits", RegexOptions.IgnoreCase)]
    private static partial Regex BitCostRegex();
}
