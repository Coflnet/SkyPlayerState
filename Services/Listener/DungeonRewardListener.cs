using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// A Catacombs reward chest ("Wood Chest".."Bedrock Chest") or a Kuudra "Paid Chest"/"Free Chest",
/// parsed from its "Open Reward Chest" button - see <see cref="DungeonRewardChestParser"/>.
/// </summary>
public class RewardChest
{
    public string ChestType { get; set; }
    public long CostCoins { get; set; }
    public List<string> Contents { get; set; } = [];
    /// <summary>True for a Catacombs chest tier ("Wood Chest".."Bedrock Chest"); false for anything
    /// else that also carries an "Open Reward Chest" button (currently only Kuudra's "Paid
    /// Chest"/"Free Chest") - those must never be charged, see <see cref="DungeonRewardListener"/>.</summary>
    public bool IsDungeonChest { get; set; }
}

/// <summary>
/// Parses a reward chest GUI's "Open Reward Chest" item into <see cref="RewardChest"/>. Pure/static
/// so it is trivially unit testable. The real payload format (verified from a production Kuudra
/// chest - the dungeon one reuses the same button) is:
/// <code>
/// Contents
/// Wither Spectre Shard
/// ...
///
/// Cost
/// Burning Kuudra Key
///
/// Click to open!
/// </code>
/// The dungeon title/cost wording itself ("Wood Chest".."Bedrock Chest", "2,000,000 Coins"/"FREE")
/// is NOT yet confirmed against production (no sample in the logs at the time this was written) -
/// see <see cref="DungeonRewardListener"/>'s "Dungeon reward chest ... seen" log line, which exists
/// specifically to confirm it from Loki after rollout.
/// </summary>
public static class DungeonRewardChestParser
{
    private static readonly Regex FormatCodeRegex = new("§.", RegexOptions.Compiled);
    private static readonly Regex CoinsRegex = new(@"^([\d,]+) Coins$", RegexOptions.Compiled);

    private static readonly HashSet<string> DungeonChestTiers = new(StringComparer.Ordinal)
    {
        "Wood Chest", "Gold Chest", "Diamond Chest", "Emerald Chest", "Obsidian Chest", "Bedrock Chest"
    };

    /// <summary>
    /// Wiki fallback cost table (coins), used when the chest's own lore carries no parseable "X
    /// Coins"/"FREE" line - keyed by floor tier bucket ("F1"/"F2"/"F3+", the last one also covering
    /// every Master floor and an unknown/unresolved floor) and chest tier. Wood is always free;
    /// Bedrock (F5+/Master only) is a flat 2,000,000 regardless of bucket.
    /// </summary>
    private static readonly Dictionary<(string Bucket, string ChestType), long> FallbackCosts = new()
    {
        [("F1", "Gold Chest")] = 25_000,
        [("F1", "Diamond Chest")] = 50_000,
        [("F1", "Emerald Chest")] = 100_000,
        [("F1", "Obsidian Chest")] = 250_000,
        [("F2", "Gold Chest")] = 50_000,
        [("F2", "Diamond Chest")] = 100_000,
        [("F2", "Emerald Chest")] = 250_000,
        [("F2", "Obsidian Chest")] = 500_000,
        [("F3+", "Gold Chest")] = 100_000,
        [("F3+", "Diamond Chest")] = 250_000,
        [("F3+", "Emerald Chest")] = 500_000,
        [("F3+", "Obsidian Chest")] = 1_000_000,
    };

    private static string Strip(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : FormatCodeRegex.Replace(value, string.Empty);

    /// <summary>
    /// Attempts to parse <paramref name="chest"/> as a reward chest GUI (dungeon or Kuudra).
    /// </summary>
    /// <param name="chest">The opened chest view.</param>
    /// <param name="lastDungeonFloor">
    /// <see cref="ExtractedInfo.LastDungeonFloor"/> (a floor ZONE string, e.g. "The Catacombs (F1)"),
    /// used only for the fallback cost table when the chest's own lore has no parseable coin line -
    /// see <see cref="Tasks.DungeonRewardAttribution.FloorOf"/>. May be null (falls back to the F3+
    /// bucket, same as an unrecognized floor).
    /// </param>
    public static bool TryParse(ChestView chest, string lastDungeonFloor, out RewardChest info)
    {
        info = null;
        var name = chest?.Name;
        if (string.IsNullOrEmpty(name) || !name.EndsWith(" Chest", StringComparison.Ordinal))
            return false;
        var openChestItem = chest.Items?.FirstOrDefault(i => Strip(i.ItemName) == "Open Reward Chest");
        if (openChestItem == null)
            return false;

        var chestType = Strip(name);
        var lines = Strip(openChestItem.Description ?? string.Empty).Split('\n');
        var contents = ExtractSection(lines, "Contents");
        var costLines = ExtractSection(lines, "Cost");

        long? coins = null;
        foreach (var line in costLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("FREE", StringComparison.OrdinalIgnoreCase))
            {
                coins = 0;
                break;
            }
            var match = CoinsRegex.Match(trimmed);
            if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var parsed))
            {
                coins = parsed;
                break;
            }
            // anything else (a key item line, e.g. "Burning Kuudra Key"/"Dungeon Chest Key") is
            // ignored - the key's cost shows up as its own negative item delta separately.
        }

        var isDungeonChest = DungeonChestTiers.Contains(chestType);
        if (!coins.HasValue)
            coins = isDungeonChest ? FallbackCost(chestType, lastDungeonFloor) : 0;

        info = new RewardChest
        {
            ChestType = chestType,
            CostCoins = coins.Value,
            Contents = contents,
            IsDungeonChest = isDungeonChest
        };
        return true;
    }

    private static long FallbackCost(string chestType, string lastDungeonFloor)
    {
        if (chestType == "Wood Chest")
            return 0;
        if (chestType == "Bedrock Chest")
            return 2_000_000;
        var floor = Tasks.DungeonRewardAttribution.FloorOf(lastDungeonFloor);
        var bucket = floor switch
        {
            "F1" => "F1",
            "F2" => "F2",
            _ => "F3+", // F3-F7, every Master floor, and an unknown/unresolved floor
        };
        return FallbackCosts.GetValueOrDefault((bucket, chestType), 0);
    }

    private static List<string> ExtractSection(string[] lines, string header)
    {
        var result = new List<string>();
        var idx = Array.IndexOf(lines, header);
        if (idx < 0)
            return result;
        for (var i = idx + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                break;
            result.Add(line);
        }
        return result;
    }
}

/// <summary>
/// Accounts for the coins spent opening a dungeon reward chest - a real cost <see
/// cref="TaskPeriodFolder"/>/<see cref="MethodTask"/> must subtract from profit, not just loot to
/// add. The chest click itself is never visible to us, only the GUI it opens and (separately) later
/// purse readings, so a chest with a non-zero cost is remembered as a <see
/// cref="PendingDungeonChestCharge"/> and only actually charged once a later purse reading confirms
/// the coins were really spent (see <see cref="EvaluatePending"/>) - see the class docs on <see
/// cref="Tasks.PseudoItems"/>/<see cref="Tasks.DungeonRewardAttribution"/> for how the resulting
/// <see cref="Tasks.PseudoItems.DUNGEON_CHEST_COST"/> tag reaches profit/task accounting.
/// </summary>
public class DungeonRewardListener : UpdateListener
{
    private static readonly TimeSpan PendingExpiry = TimeSpan.FromMinutes(2);

    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
            HandleInventory(args);
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            HandleScoreboard(args);
        return Task.CompletedTask;
    }

    private void HandleInventory(UpdateArgs args)
    {
        var info = args.currentState.ExtractedInfo;
        if (!DungeonRewardChestParser.TryParse(args.msg.Chest, info.LastDungeonFloor, out var chest))
            return;

        Logger.LogInformation(
            "Dungeon reward chest {chest} seen for {playerId} floor {floor} cost {cost} contents {contents}",
            chest.ChestType, args.msg.PlayerId, info.LastDungeonFloor, chest.CostCoins, string.Join(", ", chest.Contents));

        if (!chest.IsDungeonChest || chest.CostCoins <= 0)
            return; // Kuudra chests (and free dungeon chests) are never charged

        var now = DateTime.UtcNow;
        if (info.PendingDungeonChestCharge != null)
            // a new chest arrived while one was still pending - settle the old one against the
            // latest known purse before it is replaced, rather than silently dropping it
            EvaluatePending(args, info.Purse > 0 ? info.Purse : null, now);

        info.PendingDungeonChestCharge = new PendingDungeonChestCharge
        {
            ChestType = chest.ChestType,
            CostCoins = chest.CostCoins,
            // the post-evaluation purse is the correct baseline for the new pending charge, whether
            // or not a previous one was just settled above
            PurseAtOpen = info.Purse,
            SeenAt = now,
            Floor = Tasks.DungeonRewardAttribution.FloorOf(info.LastDungeonFloor) ?? info.LastDungeonFloor
        };
    }

    private void HandleScoreboard(UpdateArgs args)
    {
        if (args.currentState.ExtractedInfo.PendingDungeonChestCharge == null)
            return;
        // parsed independently of CollectionListener.HandleScoreboard (which also reads Purse from
        // the same scoreboard) so this never depends on handler registration order.
        var purse = ScoreboardParser.ParsePurse(args.msg.Scoreboard);
        EvaluatePending(args, purse, DateTime.UtcNow);
    }

    /// <summary>
    /// Confirms or drops the pending charge: expires it after <see cref="PendingExpiry"/>, otherwise
    /// charges it once <paramref name="currentPurse"/> is a known reading (0/negative = unknown - see
    /// <see cref="ExtractedInfo.Purse"/> - never charge off an unknown reading, it would look like an
    /// arbitrarily large "drop") that fell by at least the chest's cost since it was opened.
    /// </summary>
    private void EvaluatePending(UpdateArgs args, long? currentPurse, DateTime now)
    {
        var info = args.currentState.ExtractedInfo;
        var pending = info.PendingDungeonChestCharge;
        if (pending == null)
            return;
        if (now - pending.SeenAt > PendingExpiry)
        {
            info.PendingDungeonChestCharge = null;
            return;
        }
        if (currentPurse is null or <= 0)
            return; // purse unknown - wait for a later valid reading, or expiry
        if (pending.PurseAtOpen - currentPurse.Value < pending.CostCoins)
            return; // purse has not dropped enough (yet) to confirm the purchase

        var collected = args.currentState.ItemsCollectedRecently;
        var cost = (int)Math.Min(pending.CostCoins, int.MaxValue);
        collected[Tasks.PseudoItems.DUNGEON_CHEST_COST] = collected.GetValueOrDefault(Tasks.PseudoItems.DUNGEON_CHEST_COST, 0) - cost;
        Logger.LogInformation("Dungeon chest cost {cost} charged to {playerId} for {chest} (floor {floor})",
            pending.CostCoins, args.currentState.PlayerId, pending.ChestType, pending.Floor);
        info.PendingDungeonChestCharge = null;
    }
}
