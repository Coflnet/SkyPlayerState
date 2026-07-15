using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Helpers for reading the SkyBlock sidebar scoreboard.
/// </summary>
public static class ScoreboardParser
{
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
