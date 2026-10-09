using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// What the lore of an item in the "(1/3) Loadouts" menu says is worn. Values are the display names of the
/// pieces (e.g. "Fig Cap"), not item tags; null = the slot is empty ("None").
/// Not used for task ownership yet, see <see cref="MenuSamplerListener"/>: real samples come first.
/// </summary>
public record LoadoutLore(string? Name, IReadOnlyDictionary<string, string?> Pieces)
{
    /// <summary>The worn slots in the order the lore lists them.</summary>
    public static readonly string[] Slots =
        ["Helmet", "Chestplate", "Leggings", "Boots", "Necklace", "Cloak", "Belt", "Gloves/Bracelet"];

    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);

    /// <summary>
    /// Parses the lore ("Loadout 2\nHelmet: Fig Cap\n..."). Slots missing in the lore are missing in
    /// <see cref="Pieces"/>; other lines (Pet, HOTM, ...) are ignored. Null for a lore without any worn slot.
    /// </summary>
    public static LoadoutLore? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        string? name = null;
        var pieces = new Dictionary<string, string?>();
        foreach (var raw in ColorCodes.Replace(description, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                name ??= line;
                continue;
            }
            var slot = Array.Find(Slots, s => s.Equals(line[..colon].Trim(), StringComparison.OrdinalIgnoreCase));
            if (slot == null)
                continue;
            var value = line[(colon + 1)..].Trim();
            pieces[slot] = value.Length == 0 || value.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : value;
        }
        return pieces.Count == 0 ? null : new LoadoutLore(name, pieces);
    }
}
