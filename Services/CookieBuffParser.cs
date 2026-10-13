using System;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Parses the "Cookie Buff" section of the tab list footer.
/// </summary>
public static class CookieBuffParser
{
    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"(\d+)\s*([dhms])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns false when the footer has no Cookie Buff section. Otherwise active tells whether a cookie
    /// is running and remaining how long it still lasts (zero when not active).
    /// </summary>
    public static bool TryParse(string? footerText, out bool active, out TimeSpan remaining)
    {
        active = false;
        remaining = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(footerText))
            return false;

        var lines = ColorCodes.Replace(footerText, string.Empty).Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().StartsWith("Cookie Buff", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return false;

        var total = TimeSpan.Zero;
        var found = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                break;
            if (line.Contains("Not active", StringComparison.OrdinalIgnoreCase))
                return true;
            // next heading, e.g. "Active Effects" / "Upgrades" - not a duration line
            var matches = Token.Matches(line);
            if (matches.Count == 0)
            {
                if (found)
                    break;
                // unknown wording before any duration: treat as a heading boundary only if it looks like one
                if (line.EndsWith(':') || line.StartsWith("Upgrades", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Active Effects", StringComparison.OrdinalIgnoreCase))
                    break;
                continue;
            }
            foreach (Match m in matches)
            {
                var n = double.Parse(m.Groups[1].Value);
                total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
                {
                    'd' => TimeSpan.FromDays(n),
                    'h' => TimeSpan.FromHours(n),
                    'm' => TimeSpan.FromMinutes(n),
                    _ => TimeSpan.FromSeconds(n)
                };
            }
            found = true;
        }
        if (!found)
            return false;
        active = total > TimeSpan.Zero;
        remaining = total;
        return true;
    }
}
