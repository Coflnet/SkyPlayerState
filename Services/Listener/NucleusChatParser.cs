using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>A Crystal Nucleus chat event, see <see cref="NucleusChatParser"/>.</summary>
public enum NucleusChatEvent
{
    None,
    /// <summary>A crystal was found somewhere in the Crystal Hollows ("✦ CRYSTAL FOUND (2/5)").</summary>
    CrystalFound,
    /// <summary>A crystal was placed at the nucleus ("✦ You placed the Jade Crystal!").</summary>
    CrystalPlaced,
    /// <summary>The fifth crystal was placed and the run's loot bundle granted ("CRYSTAL NUCLEUS LOOT BUNDLE").</summary>
    BundleClaimed
}

/// <summary>
/// Parses the Crystal Nucleus chat lines. The mod sends lines with the color codes already stripped (older clients may not),
/// the centered banners keep their leading spaces, so lines are stripped and trimmed first. Sources: SkyHanni
/// (SkyHanniStudios/SkyHanni, features/chat/CrystalNucleusChatFilter.kt and features/mining/crystalhollows/CrystalNucleusApi.kt),
/// SkyblockAPI (api/profile/hotm/CrystalAPI.kt), and a real chat log of a full run (SkyHanni chat log 2025-06-02 in
/// github.com/Sebbebas/1.8.9-forge1.8.9-11.15.1.2318-1.8.9, config/skyhanni/logs): first crystal 21:06:43, bundle 21:13:12.
/// </summary>
public static partial class NucleusChatParser
{
    /// <summary>
    /// The count line of the purple "CRYSTAL FOUND" banner; the crystal's name follows on its own line. Real line:
    /// "§f                       §r§5§l✦ CRYSTAL FOUND §r§7(1§r§7/5§r§7)" (SkyHanni pattern crystal.collected.count).
    /// </summary>
    [GeneratedRegex(@"^(?:✦ )?CRYSTAL FOUND \((?<count>[1-5])/5\)$")]
    private static partial Regex CrystalFoundRegex();

    /// <summary>Real line: "§5§l✦ §r§dYou placed the §r§bSapphire Crystal§r§d!" (SkyHanni crystal.placed, SkyblockAPI crystalPlaced).</summary>
    [GeneratedRegex(@"^(?:✦ )?You placed the (?<crystal>[A-Za-z]+) Crystal!$")]
    private static partial Regex CrystalPlacedRegex();

    /// <summary>
    /// Header of the reward list printed when the fifth crystal is placed ("Pick it up near the Nucleus Vault!" follows the
    /// list). Real line: "  §r§5§lCRYSTAL NUCLEUS LOOT BUNDLE " (SkyHanni mining.crystalnucleus loot.start, SkyblockAPI lootStart).
    /// </summary>
    [GeneratedRegex(@"^CRYSTAL NUCLEUS LOOT BUNDLE$")]
    private static partial Regex BundleRegex();

    [GeneratedRegex("§.")]
    private static partial Regex FormatCodeRegex();

    /// <summary>The event a chat line stands for; <paramref name="crystalsFound"/> is the n of "(n/5)" for a found crystal, else 0.</summary>
    public static NucleusChatEvent Parse(string? raw, out int crystalsFound)
    {
        crystalsFound = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return NucleusChatEvent.None;
        var line = FormatCodeRegex().Replace(raw, string.Empty).Trim();
        var found = CrystalFoundRegex().Match(line);
        if (found.Success)
        {
            crystalsFound = int.Parse(found.Groups["count"].Value);
            return NucleusChatEvent.CrystalFound;
        }
        if (CrystalPlacedRegex().IsMatch(line))
            return NucleusChatEvent.CrystalPlaced;
        return BundleRegex().IsMatch(line) ? NucleusChatEvent.BundleClaimed : NucleusChatEvent.None;
    }
}
