using System;
using System.Linq;
using System.Text.RegularExpressions;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Reads the lore footer ("§6§lLEGENDARY FARMING TOOL", recombobulated "§6§l§ka§6§lLEGENDARY SWORD §6§l§ka": the obfuscated
/// letter at both ends, with or without a space, is skipped) into the rarity and the
/// item type. Item types checked against the NotEnoughUpdates-REPO lore: FARMING TOOL, AXE, DRILL, PICKAXE, GAUNTLET,
/// FISHING ROD, SWORD, BOW, LONGSWORD, HELMET, CHESTPLATE, LEGGINGS, BOOTS, NECKLACE, CLOAK, BELT, GLOVES, BRACELET, BAIT.
/// The "DUNGEON" qualifier is dropped (a dungeon sword is a SWORD).
/// </summary>
public static class ItemFooter
{
    private static readonly Regex Footer = new(
        @"^(?:[a-z]\s*)?(?<rarity>VERY SPECIAL|COMMON|UNCOMMON|RARE|EPIC|LEGENDARY|MYTHIC|DIVINE|SPECIAL|ULTIMATE|ADMIN)(?:\s+DUNGEON)?(?:\s+(?<type>[A-Z][A-Z ]*?))?(?:\s*[a-z])?$",
        RegexOptions.Compiled);

    public static (string? Rarity, string? ItemType) Read(Item? item)
    {
        var line = LastLine(item?.Description);
        if (line == null)
            return (null, null);
        var match = Footer.Match(line);
        if (!match.Success)
            return (null, null);
        var rarity = match.Groups["rarity"].Value.Replace(' ', '_');
        var type = match.Groups["type"].Success ? match.Groups["type"].Value.Trim() : null;
        return (rarity, string.IsNullOrEmpty(type) ? null : type);
    }

    public static string? ItemType(Item? item) => Read(item).ItemType;

    private static string? LastLine(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        return description.Split('\n')
            .Select(l => MenuTitleNormalizer.StripColors(l).Trim())
            .LastOrDefault(l => l.Length > 0);
    }
}
