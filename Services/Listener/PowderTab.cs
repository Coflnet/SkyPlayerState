using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Reads the powder amounts from the tab list and keeps the last seen amount per powder on <see cref="ExtractedInfo"/>.
/// The current widget is a "Powders:" header with short lines (" Mithril: 1,234,567", " Gemstone: 123,456",
/// " Glacite: 12,345"); older uploads used "Mithril Powder: 1,234". Tab line order is not guaranteed, so the
/// short lines count only when the "Powders:" header is anywhere in the same upload.
/// </summary>
public static class PowderTab
{
    public const string MithrilTag = "MITHRIL_POWDER";
    public const string GemstoneTag = "GEMSTONE_POWDER";
    public const string GlaciteTag = "GLACITE_POWDER";

    private const string Names = "Mithril|Gemstone|Glacite";
    // a whole number only: "1,234,567" or "1234", never a part of "1.2M" or "1.234.567"
    private const string Amount = @"(?<![\d.,])(?<amount>\d{1,3}(?:,\d{3})+|\d+)(?![\d.,]|\s?\d)(?!\s*[KkMmBb]\b)";
    private static readonly Regex WidgetHeader = new(@"^\s*Powders:\s*$", RegexOptions.Compiled);
    private static readonly Regex WidgetLine = new(@$"^\s*(?<name>{Names}):\s*{Amount}\s*$", RegexOptions.Compiled);
    private static readonly Regex LabelLine = new(@$"(?<name>{Names}) Powder\s*:\s*{Amount}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReverseLine = new(@$"{Amount}\s*(?<name>{Names}) Powder", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Powder tag to amount for every powder the upload shows; header/footer entries are ignored.</summary>
    public static Dictionary<string, int> Parse(IEnumerable<string?>? tab)
    {
        var amounts = new Dictionary<string, int>();
        if (tab == null)
            return amounts;
        var lines = tab.Where(IsWidgetEntry).Select(l => Regex.Replace(l!, "§.", string.Empty)).ToList();
        var hasWidget = lines.Any(l => WidgetHeader.IsMatch(l));
        foreach (var line in lines)
            AddFirst(amounts, ParseLine(line, hasWidget));
        return amounts;
    }

    public static int Get(ExtractedInfo info, string tag) => tag switch
    {
        MithrilTag => info.MithrilPowder,
        GemstoneTag => info.GemstonePowder,
        GlaciteTag => info.GlacitePowder,
        _ => 0
    };

    public static void Set(ExtractedInfo info, string tag, int amount)
    {
        if (tag == MithrilTag)
            info.MithrilPowder = amount;
        else if (tag == GemstoneTag)
            info.GemstonePowder = amount;
        else if (tag == GlaciteTag)
            info.GlacitePowder = amount;
    }

    /// <summary>
    /// Makes the baselines belong to <paramref name="profile"/> (null = unknown). Baselines read on another profile,
    /// or with the profile unknown on exactly one side, are reset, so the reading only sets a fresh baseline.
    /// </summary>
    public static void ClaimBaselines(ExtractedInfo info, string? profile)
    {
        if (SameProfile(info.PowderProfile, profile))
            return;
        ResetBaselines(info, profile);
    }

    /// <summary>Forgets all three baselines (0 = never seen) and their reading time.</summary>
    public static void ResetBaselines(ExtractedInfo info, string? profile)
    {
        info.MithrilPowder = 0;
        info.GemstonePowder = 0;
        info.GlacitePowder = 0;
        info.PowderProfile = profile;
        info.PowderSeenAt = default;
    }

    private static bool SameProfile(string? baseline, string? reading)
    {
        if (string.IsNullOrEmpty(baseline) || string.IsNullOrEmpty(reading))
            return string.IsNullOrEmpty(baseline) && string.IsNullOrEmpty(reading);
        return string.Equals(baseline, reading, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWidgetEntry(string? line)
        => !string.IsNullOrWhiteSpace(line)
            && !line.StartsWith(TabListUpdate.HeaderPrefix)
            && !line.StartsWith(TabListUpdate.FooterPrefix);

    private static (string Tag, int Amount)? ParseLine(string line, bool hasWidget)
    {
        if (hasWidget && TryMatch(WidgetLine, line) is { } shortForm)
            return shortForm;
        return TryMatch(LabelLine, line) ?? TryMatch(ReverseLine, line);
    }

    private static (string Tag, int Amount)? TryMatch(Regex regex, string line)
    {
        var match = regex.Match(line);
        if (!match.Success || !TryParseAmount(match.Groups["amount"].Value, out var amount))
            return null;
        return (TagOf(match.Groups["name"].Value), amount);
    }

    private static bool TryParseAmount(string value, out int amount)
        => int.TryParse(value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out amount);

    private static string TagOf(string name) => name.ToUpperInvariant() + "_POWDER";

    private static void AddFirst(Dictionary<string, int> amounts, (string Tag, int Amount)? reading)
    {
        if (reading is { } r)
            amounts.TryAdd(r.Tag, r.Amount);
    }
}
