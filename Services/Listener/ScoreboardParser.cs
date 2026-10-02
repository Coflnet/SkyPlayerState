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

    // In the Rift dimension the area marker is the Cyrillic letter ф (U+0444), e.g. " ф Wyld Woods"
    // (community mods match `[⏣ф]`). Taken from community knowledge, not our own logs - hence the
    // "Motes:" based fallback in ExtractArea for an unknown future Rift glyph.
    public const char AreaGlyphRift = 'ф';

    private static bool IsShapedLikeAreaLine(string line) =>
        line != null && line.Length > 2 && line[0] == ' ' && line[2] == ' ';

    /// <summary>
    /// True when the line is an area marker line (" &lt;glyph&gt; &lt;Area Name&gt;").
    /// </summary>
    public static bool IsAreaLine(string line) =>
        IsShapedLikeAreaLine(line)
        && (line[1] == AreaGlyphLegacy || line[1] == AreaGlyphPrivateUse || line[1] == AreaGlyphRift);

    private static List<string> StripCodes(IEnumerable<string> scoreboard) =>
        (scoreboard ?? Enumerable.Empty<string>()).Where(l => l != null)
            .Select(l => Regex.Replace(l, "§.", string.Empty)).ToList();

    /// <summary>
    /// True for a Rift scoreboard: it has a "Motes:" line (replaces the purse in the Rift) or an
    /// area line using the Rift glyph ф.
    /// </summary>
    public static bool IsRiftScoreboard(IEnumerable<string> scoreboard)
    {
        var lines = StripCodes(scoreboard);
        return lines.Any(l => l.Contains("Motes:"))
            || lines.Any(l => IsShapedLikeAreaLine(l) && l[1] == AreaGlyphRift);
    }

    /// <summary>
    /// The current area name from a scoreboard, or null when no area line is present.
    /// Hypixel shows the literal area name "None" at some spots (confirmed in production logs,
    /// player Ekwav, on several islands) rather than omitting the area line entirely; that is
    /// treated the same as no area line so it does not trigger a location change or get stored
    /// as its own period (see CollectionListener.HandleScoreboard).
    /// When the scoreboard has a "Motes:" line (Rift) but no known glyph matches, the first line shaped
    /// " &lt;one symbol char&gt; &lt;name&gt;" is taken, so a future Rift glyph swap does not break detection.
    /// Without "Motes:" other sidebar rows of that shape are never mistaken for an area.
    /// </summary>
    public static string ExtractArea(IEnumerable<string> scoreboard)
    {
        var list = scoreboard?.ToList();
        var area = list?.FirstOrDefault(IsAreaLine)?.Substring(3).Trim();
        if (area == null && list != null && StripCodes(list).Any(l => l.Contains("Motes:")))
        {
            area = list.FirstOrDefault(l => IsShapedLikeAreaLine(l)
                && !char.IsLetterOrDigit(l[1]) && !char.IsWhiteSpace(l[1]))?.Substring(3).Trim();
        }
        return area == "None" ? null : area;
    }
}
