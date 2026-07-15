using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Helpers for reading the SkyBlock sidebar scoreboard.
/// </summary>
public static class ScoreboardParser
{
    // "Purse: 40,859,741" or "Piggy: 40,859,741 (+1,234)" (the piggy bank replaces the
    // purse label while it holds coins; the trailing "(+interest)" must be ignored).
    private static readonly Regex PurseRegex = new(@"(?:Purse|Piggy):\s*([\d,]+)", RegexOptions.Compiled);
    // "Bits: 10,920"
    private static readonly Regex BitsRegex = new(@"Bits:\s*([\d,]+)", RegexOptions.Compiled);

    /// <summary>
    /// Coins in the players purse (or piggy bank), or null when no purse line is present.
    /// </summary>
    public static long? ParsePurse(IEnumerable<string> scoreboard) => ParseLabeledAmount(scoreboard, PurseRegex);

    /// <summary>
    /// Bits shown on the scoreboard, or null when no bits line is present.
    /// </summary>
    public static long? ParseBits(IEnumerable<string> scoreboard) => ParseLabeledAmount(scoreboard, BitsRegex);

    private static long? ParseLabeledAmount(IEnumerable<string> scoreboard, Regex regex)
    {
        if (scoreboard == null)
            return null;
        foreach (var rawLine in scoreboard)
        {
            if (rawLine == null)
                continue;
            var line = Regex.Replace(rawLine, "§.", string.Empty);
            var match = regex.Match(line);
            if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var amount))
                return amount;
        }
        return null;
    }

    // The current area is marked on the scoreboard by a leading glyph, e.g. " ⏣ Lotus Atoll".
    // Newer Hypixel clients render that marker with a private-use font glyph (U+E067) instead
    // of the benzene ring ⏣. Both must be accepted or area detection silently breaks
    // (no location => no profit tracking, no task classification).
    public const char AreaGlyphLegacy = '⏣';
    public const char AreaGlyphPrivateUse = '';

    /// <summary>
    /// True when the line is an area marker line (" &lt;glyph&gt; &lt;Area Name&gt;").
    /// </summary>
    public static bool IsAreaLine(string line) =>
        line != null && line.Length > 2 && line[0] == ' '
        && (line[1] == AreaGlyphLegacy || line[1] == AreaGlyphPrivateUse)
        && line[2] == ' ';

    /// <summary>
    /// The current area name from a scoreboard, or null when no area line is present.
    /// </summary>
    public static string ExtractArea(IEnumerable<string> scoreboard) =>
        scoreboard?.FirstOrDefault(IsAreaLine)?.Substring(3).Trim();
}
