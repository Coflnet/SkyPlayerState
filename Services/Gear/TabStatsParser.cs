using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>What one tab list upload shows of the player's stats, pests and pet. Missing values are absent, never 0.</summary>
/// <param name="PetName">the pet's display name from the Pet widget ("Mooshroom Cow"); <see cref="GearPet.Type"/> of <paramref name="Pet"/> is derived from it</param>
public record TabStatsReading(Dictionary<string, double> Stats, int? PestsAlive, GearPet? Pet, string? PetName = null);

/// <summary>
/// Parses the tab list widgets by content (line order is not guaranteed): Stats widget lines " Farming Fortune: GLYPH1,234"
/// matched against a whitelist of rate relevant stat names (SkyHanni SkyblockStat display names; the private use glyph
/// is optional and unchecked, it changes with resource pack updates), the Garden crop line " Wheat Fortune: GLYPH321"
/// (only for the task's own crop: it shows the crop broken last), the Pests widget " Alive: 3" and the Pet widget
/// " [Lvl 100] Elephant" (no rarity: it is only in the text color the mod does not upload). Anything else, player
/// names included, is ignored.
/// </summary>
public static class TabStatsParser
{
    public const string PestsAliveStat = "Pests Alive";
    /// <summary>1 when 4 or more pests are alive (Farming Fortune is reduced then), 0 otherwise.</summary>
    public const string PestsPenaltyStat = "Pests 4+";

    public static readonly IReadOnlySet<string> StatNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Mining Speed", "Mining Spread", "Gemstone Spread", "Pristine", "Mining Fortune", "Ore Fortune", "Block Fortune",
        "Dwarven Metal Fortune", "Gemstone Fortune", "Breaking Power",
        "Farming Fortune", "Bonus Pest Chance", "Overbloom",
        "Foraging Fortune", "Fig Fortune", "Mangrove Fortune", "Helix Fortune", "Sweep", "Timber",
        "Fishing Speed", "Sea Creature Chance", "Double Hook Chance", "Trophy Chance", "Treasure Chance",
        "Hunting Fortune", "Magic Find", "Pet Luck", "Speed"
    };

    private static readonly Regex StatLine = new(
        @"^\s*(?<name>[A-Za-z' ]+?):\s*[^\d\s+\-.,]{0,2}\s*(?<value>[+-]?\d[\d,]*(?:\.\d+)?)%?(?:\s.*)?$", RegexOptions.Compiled);
    private static readonly Regex AliveLine = new(@"^\s*Alive:\s*(?<count>\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex PetLine = new(
        @"^\s*\[Lvl (?<level>[\d,]+)\] (?:\[\d+✦\] )?(?<pet>[A-Za-z' -]+?)(?: ✦)?\s*$", RegexOptions.Compiled);

    public static TabStatsReading Parse(IEnumerable<string?> lines, string? task)
    {
        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        int? pests = null;
        GearPet? pet = null;
        string? petName = null;
        foreach (var raw in lines)
        {
            if (raw == null || raw.StartsWith(TabListUpdate.HeaderPrefix) || raw.StartsWith(TabListUpdate.FooterPrefix))
                continue;
            var line = MenuTitleNormalizer.StripColors(raw);
            pests ??= ParseAlive(line);
            if (pet == null)
                (pet, petName) = ParsePet(line);
            AddStat(stats, line, task);
        }
        return new TabStatsReading(stats, pests, pet, petName);
    }

    /// <summary>Only the whitelisted stats, crop fortunes only for the task's crop (menu reads use the same rule).</summary>
    public static Dictionary<string, double> RateStats(IReadOnlyDictionary<string, double> stats, string? task)
        => stats.Where(s => IsRateStat(s.Key, task)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

    public static bool IsRateStat(string name, string? task)
    {
        if (StatNames.Contains(name))
            return true;
        return FarmingCrops.CropFortuneStats.Contains(name) && FarmingCrops.ForTask(task)?.FortuneStat == name;
    }

    private static void AddStat(Dictionary<string, double> stats, string line, string? task)
    {
        var match = StatLine.Match(line);
        if (!match.Success)
            return;
        var name = match.Groups["name"].Value.Trim();
        if (stats.ContainsKey(name) || !IsRateStat(name, task))
            return;
        if (double.TryParse(match.Groups["value"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            stats[name] = value;
    }

    private static int? ParseAlive(string line)
    {
        var match = AliveLine.Match(line);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : null;
    }

    private static (GearPet? Pet, string? Name) ParsePet(string line)
    {
        var match = PetLine.Match(line);
        if (!match.Success || !int.TryParse(match.Groups["level"].Value.Replace(",", ""), out var level))
            return (null, null);
        var name = match.Groups["pet"].Value.Trim();
        var type = name.ToUpperInvariant().Replace(' ', '_');
        return type.Length == 0 ? (null, null) : (new GearPet { Type = type, Level = level }, name);
    }

    /// <summary>The reading as one sample: the stats plus the pest count and penalty flag when the Pests widget was shown.</summary>
    public static Dictionary<string, double> SampleValues(TabStatsReading reading)
    {
        var values = new Dictionary<string, double>(reading.Stats, StringComparer.Ordinal);
        if (reading.PestsAlive is { } alive)
        {
            values[PestsAliveStat] = alive;
            values[PestsPenaltyStat] = alive >= 4 ? 1 : 0;
        }
        return values;
    }
}
