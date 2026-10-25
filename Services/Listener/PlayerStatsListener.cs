using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Reads the player stats (Mining Speed, Mining Spread, Combat Wisdom, ...) from the item descriptions of the
/// "Stats &amp; Equipment" menu: every item named "... Stats" lists " Stat Name 123" lines. Used by the "stat:" task
/// stat factors to bucket players.
/// </summary>
public class PlayerStatsListener : UpdateListener
{
    /// <summary>Statistics are optional bookkeeping, a failure must not stop the state save.</summary>
    public override bool Optional => true;

    /// <summary>A stored stat older than this is replaced instead of max-merged.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    // after colors and glyphs are stripped: "Mining Speed 885", "Ore Fortune 106.35", "Gemstone Spread 2.5", "Pristine 0"
    private static readonly Regex StatLine = new(@"^([A-Za-z][A-Za-z' ]*?)\s+(-?\d[\d,]*(?:\.\d+)?)%?$", RegexOptions.Compiled);
    private static readonly Regex Glyphs = new(@"[^ -~]", RegexOptions.Compiled);

    public override Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest?.Name == null || chest.Items == null
            || MenuTitleNormalizer.StripColors(chest.Name).Trim() != "Stats & Equipment")
            return Task.CompletedTask;
        var stats = ParseStats(chest.Items);
        if (stats.Count == 0)
            return Task.CompletedTask;
        Merge(args.currentState.ExtractedInfo, stats, args.msg.ReceivedAt == default ? DateTime.UtcNow : args.msg.ReceivedAt);
        return Task.CompletedTask;
    }

    /// <summary>All "Stat Name number" lines of the items whose name ends with " Stats".</summary>
    public static Dictionary<string, double> ParseStats(IEnumerable<Item> items)
    {
        var result = new Dictionary<string, double>();
        foreach (var item in items)
        {
            if (item?.ItemName == null || item.Description == null)
                continue;
            var name = Clean(item.ItemName);
            if (!name.EndsWith(" Stats", StringComparison.Ordinal))
                continue;
            foreach (var raw in item.Description.Split('\n'))
            {
                var match = StatLine.Match(Clean(raw));
                if (match.Success && double.TryParse(match.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    result[match.Groups[1].Value.Trim()] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// Keeps the higher of the stored and the new value per stat (the stats depend on the held tool and on temporary
    /// buffs), unless the stored value was set more than <see cref="StaleAfter"/> ago, then the new one replaces it.
    /// </summary>
    public static void Merge(ExtractedInfo info, Dictionary<string, double> read, DateTime now)
    {
        info.PlayerStats ??= new();
        info.PlayerStatSetAt ??= new();
        foreach (var (name, value) in read)
        {
            var has = info.PlayerStats.TryGetValue(name, out var stored);
            var setAt = info.PlayerStatSetAt.GetValueOrDefault(name);
            if (!has || value > stored || now - setAt > StaleAfter)
            {
                info.PlayerStats[name] = value;
                info.PlayerStatSetAt[name] = now;
            }
        }
        info.PlayerStatsAt = now;
    }

    private static string Clean(string text) => Glyphs.Replace(MenuTitleNormalizer.StripColors(text), " ").Trim();
}
