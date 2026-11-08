using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Stat lines of an item or pet lore, formats from production uploads (menu-samples, 2026-11-07):
/// "§7Farming Fortune: §6+82 §9(+25) §d(+5)", "§7Crit Chance: §9+17% §9(+17%)", "§7Strength: §c+12.25",
/// "§7Health: §c+255 §e(+60) §9(+7) §8(+1,394.4)" and the drill line "§8Breaking Power 10". The first value is the item's
/// total (the parentheses break it down: hot potato, reforge, gems, dungeon bonus). Ability text ("Grants +85 Farming
/// Fortune") has no "Name:" form and is ignored, so conditional bonuses are not counted.
/// </summary>
public static class LoreStats
{
    private static readonly Regex StatLine = new(@"^(?<name>[A-Za-z][A-Za-z' ]*?): (?<value>[+-]?\d[\d,]*(?:\.\d+)?)%?(?:\s.*)?$", RegexOptions.Compiled);
    private static readonly Regex BreakingPower = new(@"^Breaking Power (?<value>\d+)$", RegexOptions.Compiled);

    /// <summary>Stats of the lore whose name is in <paramref name="names"/>; the first line of a stat wins.</summary>
    public static Dictionary<string, double> Parse(string? description, IReadOnlySet<string> names)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(description))
            return result;
        foreach (var raw in description.Split('\n'))
            Add(result, MenuTitleNormalizer.StripColors(raw).Trim(), names);
        return result;
    }

    private static void Add(Dictionary<string, double> result, string line, IReadOnlySet<string> names)
    {
        var match = StatLine.Match(line);
        var name = match.Success ? match.Groups["name"].Value : "Breaking Power";
        if (!match.Success)
            match = BreakingPower.Match(line);
        if (!match.Success || !names.Contains(name) || result.ContainsKey(name))
            return;
        if (double.TryParse(match.Groups["value"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            result[name] = value;
    }
}
