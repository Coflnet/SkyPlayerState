using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public class TabListUpdate : UpdateListener
{
    // "Area: Crystal Hollows" - the tab list header reports the ISLAND (unlike the scoreboard
    // sidebar, which reports the finer sub-zone, see ScoreboardParser.ExtractArea). Present for
    // most islands, though not all, and formatting-code prefixed like every other tab line.
    // Confirmed exception (production logs, player Ekwav): Galatea reports its sub-region
    // ("Torrhus Canyon", "Moonglade Marsh") instead of "Galatea" - see SkyblockZones.NormalizeTabArea.
    private static readonly Regex AreaRegex = new(@"Area:\s*(.+)$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Tab != null)
        {
            args.currentState.LastTab = args.msg.Tab;
            var island = ExtractIsland(args.msg.Tab);
            if (island != null)
            {
                args.currentState.ExtractedInfo.CurrentIsland = Tasks.SkyblockZones.NormalizeTabArea(island);
                // The tab list is sent far less often than the scoreboard, so CurrentIsland can go
                // stale relative to the player's current zone - stamp when it was received so the
                // classifier can tell whether it is still fresh enough to trust (see
                // TaskClassifier.Classify / ExtractedInfo.CurrentIslandAt).
                args.currentState.ExtractedInfo.CurrentIslandAt = DateTime.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The current island from the tab list's "Area: &lt;Island&gt;" line, or null when absent.
    /// </summary>
    internal static string? ExtractIsland(IEnumerable<string> tab)
    {
        foreach (var rawLine in tab)
        {
            if (rawLine == null)
                continue;
            var line = Regex.Replace(rawLine, "§.", string.Empty).Trim();
            var match = AreaRegex.Match(line);
            if (!match.Success)
                continue;
            var island = match.Groups[1].Value.Trim();
            if (island.Length > 0)
                return island;
        }
        return null;
    }
}