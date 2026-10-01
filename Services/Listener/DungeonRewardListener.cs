using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// A Catacombs reward chest (titled "Wood".."Bedrock", older/in-dungeon ones "Wood Chest"..) or a Kuudra "Paid Chest"/"Free Chest",
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
    /// <summary>True when the chest lore says "Can't open another chest!" - the player already opened
    /// one this run and has no key, so nothing can be charged for this view.</summary>
    public bool CannotOpen { get; set; }
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
/// The dungeon format is verified from production: the view title is the bare tier ("Wood", "Gold",
/// "Diamond", "Emerald", "Obsidian", "Bedrock"; "&lt;Tier&gt; Chest" is also accepted), the cost line is
/// "FREE" or e.g. "6,000,000 Coins", and a chest the player cannot open ends with
/// "Can't open another chest!" instead of "Click to open!". The chat lines handled by
/// <see cref="DungeonRewardListener"/> ("The Catacombs - Floor VII", "Team Score: 305 (S+)",
/// "OBSIDIAN CHEST REWARDS") are verified from the SkyHanni, Odin and Skyblocker sources.
/// </summary>
public static class DungeonRewardChestParser
{
    private static readonly Regex FormatCodeRegex = new("§.", RegexOptions.Compiled);
    private static readonly Regex CoinsRegex = new(@"^([\d,]+) Coins$", RegexOptions.Compiled);

    private static readonly HashSet<string> BareDungeonTiers = new(StringComparer.Ordinal)
    {
        "Wood", "Gold", "Diamond", "Emerald", "Obsidian", "Bedrock"
    };

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

    // internal (not private) so DungeonRewardListener's discovery logging below can reuse the same
    // formatting-code stripping instead of duplicating the regex.
    internal static string Strip(string value) =>
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
        if (string.IsNullOrEmpty(name))
            return false;
        var strippedName = Strip(name);
        var isBareTier = BareDungeonTiers.Contains(strippedName);
        if (!isBareTier && !name.EndsWith(" Chest", StringComparison.Ordinal))
            return false;
        var openChestItem = chest.Items?.FirstOrDefault(i => Strip(i.ItemName) == "Open Reward Chest");
        if (openChestItem == null)
            return false;

        // normalize bare tier titles to the "<Tier> Chest" key used by the cost table
        var chestType = isBareTier ? strippedName + " Chest" : strippedName;
        var stripped = Strip(openChestItem.Description ?? string.Empty);
        var lines = stripped.Split('\n');
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
            IsDungeonChest = isDungeonChest,
            CannotOpen = stripped.Contains("Can't open another chest", StringComparison.OrdinalIgnoreCase)
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
    private static readonly TimeSpan DiscoveryLogThrottle = TimeSpan.FromMinutes(10);
    private const string KuudraPaidChest = "Paid Chest";
    private const string KuudraFreeChest = "Free Chest";
    private const int DiscoveryLogMaxItems = 12;

    /// <summary>
    /// Reward/cost bookkeeping only - it also runs on CHAT now, so a bug here must never abort the
    /// whole update (and with it the purchase/bazaar evidence other listeners recorded in it).
    /// </summary>
    public override bool Optional => true;

    /// <summary>
    /// Throttle state for <see cref="LogDungeonAreaContainerDiscovery"/> - at most one log line per
    /// distinct container title per player per <see cref="DiscoveryLogThrottle"/>. Deliberately kept
    /// in memory only (not on <see cref="Models.ExtractedInfo"/>/persisted state): it exists purely
    /// to bound log volume, resetting it on a pod restart is an acceptable tradeoff for never having
    /// to touch the persisted schema for it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string PlayerId, string Title), DateTime> discoveryLogThrottle = new();

    private static readonly TimeSpan DuplicateHeaderWindow = TimeSpan.FromSeconds(60);
    private static readonly Regex FloorHeaderRegex = new(@"^(Master Mode )?The Catacombs - (?:Floor ([IVX]+)|(Entrance))$", RegexOptions.Compiled);
    private static readonly Regex TeamScoreRegex = new(@"^Team Score: (\d+) \(([A-Z+]+)\)(?: \(NEW RECORD!\))?$", RegexOptions.Compiled);
    private static readonly Regex EssenceLineRegex = new(@"^(\w+) Essence x(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ChestRewardsRegex = new(@"^(WOOD|GOLD|DIAMOND|EMERALD|OBSIDIAN|BEDROCK) CHEST REWARDS$", RegexOptions.Compiled);

    /// <summary>In-memory only (like <see cref="discoveryLogThrottle"/>): last counted run header per player, for the 60s duplicate window.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> lastRunHeader = new();

    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.CHAT)
            HandleChat(args);
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
            HandleInventory(args);
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            HandleScoreboard(args);
        return Task.CompletedTask;
    }

    private void HandleChat(UpdateArgs args)
    {
        foreach (var raw in args.msg.ChatBatch ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var line = DungeonRewardChestParser.Strip(raw).Trim();
            var header = FloorHeaderRegex.Match(line);
            if (header.Success)
            {
                HandleFloorHeader(args, header);
                continue;
            }
            var score = TeamScoreRegex.Match(line);
            if (score.Success)
            {
                Logger.LogDebug("Dungeon team score {score} ({rank}) for {playerId} on {floor}",
                    score.Groups[1].Value, score.Groups[2].Value, args.msg.PlayerId, args.currentState.ExtractedInfo.LastDungeonFloor);
                continue;
            }
            var chestLine = ChestRewardsRegex.Match(line);
            if (chestLine.Success)
                HandleChestRewardsLine(args, chestLine.Groups[1].Value);
        }
    }

    private void HandleFloorHeader(UpdateArgs args, Match header)
    {
        string zone;
        if (header.Groups[3].Success)
            zone = "The Catacombs (E)";
        else
        {
            int number;
            try { number = Coflnet.Sky.Core.Roman.From(header.Groups[2].Value); }
            catch (Exception) { return; }
            if (number < 1 || number > 7)
                return;
            zone = $"The Catacombs ({(header.Groups[1].Success ? "M" : "F")}{number})";
        }
        var info = args.currentState.ExtractedInfo;
        var now = DateTime.UtcNow;
        var key = args.msg.PlayerId ?? args.currentState.PlayerId ?? string.Empty;
        if (lastRunHeader.TryGetValue(key, out var last) && now - last < DuplicateHeaderWindow)
        {
            Logger.LogDebug("Ignoring duplicate dungeon run header for {playerId}", key);
            return;
        }
        lastRunHeader[key] = now;
        info.LastDungeonFloor = zone;
        info.LastDungeonFloorAt = now;
        Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, Tasks.PseudoItems.DUNGEON_RUN, 1);
        Logger.LogDebug("Dungeon run completed for {playerId} on {floor}", key, zone);
    }

    private void HandleChestRewardsLine(UpdateArgs args, string tier)
    {
        var info = args.currentState.ExtractedInfo;
        var pending = info.PendingDungeonChestCharge;
        var chestType = char.ToUpperInvariant(tier[0]) + tier[1..].ToLowerInvariant() + " Chest";
        if (pending == null || pending.ChestType != chestType)
        {
            Logger.LogDebug("{tier} chest opened for {playerId} but no matching pending charge", tier, args.msg.PlayerId);
            return;
        }
        if (DateTime.UtcNow - pending.SeenAt > PendingExpiry)
        {
            info.PendingDungeonChestCharge = null;
            return;
        }
        Charge(args, pending);
    }

    /// <summary>
    /// "Wither Essence x54" lines of a chest's Contents as ESSENCE_WITHER=54. Essence goes to the
    /// essence storage, never the inventory, so inventory diffs never see it (production 2026-10-01:
    /// a run's steady essence income was never counted).
    /// </summary>
    internal static Dictionary<string, int> ParseEssences(IEnumerable<string> contents)
    {
        var result = new Dictionary<string, int>();
        foreach (var line in contents ?? [])
        {
            var match = EssenceLineRegex.Match(line.Trim());
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var amount))
                continue;
            var tag = "ESSENCE_" + match.Groups[1].Value.ToUpperInvariant();
            result[tag] = result.GetValueOrDefault(tag) + amount;
        }
        return result;
    }

    private void Charge(UpdateArgs args, PendingDungeonChestCharge pending)
    {
        var collected = args.currentState.ItemsCollectedRecently;
        // a free chest (cost 0) only credits its essences - no cost entry/log line
        if (pending.CostCoins > 0)
        {
            var cost = (int)Math.Min(pending.CostCoins, int.MaxValue);
            Tasks.ItemCountMath.Add(collected, Tasks.PseudoItems.DUNGEON_CHEST_COST, -(long)cost);
            Logger.LogInformation("Dungeon chest cost {cost} charged to {playerId} for {chest} (floor {floor})",
                pending.CostCoins, args.currentState.PlayerId, pending.ChestType, pending.Floor);
        }
        foreach (var (tag, amount) in pending.Essences ?? [])
            Tasks.ItemCountMath.Add(collected, tag, amount);
        args.currentState.ExtractedInfo.PendingDungeonChestCharge = null;
    }

    private void HandleInventory(UpdateArgs args)
    {
        LogDungeonAreaContainerDiscovery(args);

        var info = args.currentState.ExtractedInfo;
        if (!DungeonRewardChestParser.TryParse(args.msg.Chest, info.LastDungeonFloor, out var chest))
            return;

        if (!chest.IsDungeonChest)
        {
            // Kuudra chests ("Paid Chest"/"Free Chest") reuse the same "Open Reward Chest"
            // button/format as real dungeon reward chests but are an entirely different reward
            // system - in a 6h production sample ALL 894 "Dungeon reward chest ... seen" log lines
            // were actually Kuudra chests, 92 of them mislabelled with a stale Catacombs floor from
            // ExtractedInfo.LastDungeonFloor because the player had been in a dungeon earlier. Never
            // log them as a dungeon chest and never attribute a floor to them; Debug only.
            Logger.LogDebug("Kuudra reward chest {chest} seen for {playerId}, contents {contents}",
                chest.ChestType, args.msg.PlayerId, string.Join(", ", chest.Contents));
            return;
        }

        Logger.LogInformation(
            "Dungeon reward chest {chest} seen for {playerId} floor {floor} cost {cost} contents {contents}",
            chest.ChestType, args.msg.PlayerId, info.LastDungeonFloor, chest.CostCoins, string.Join(", ", chest.Contents));

        if (chest.CannotOpen)
        {
            Logger.LogDebug("Dungeon reward chest {chest} for {playerId} cannot be opened, not charging", chest.ChestType, args.msg.PlayerId);
            return;
        }
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
            Essences = ParseEssences(chest.Contents),
            Floor = Tasks.DungeonRewardAttribution.FloorOf(info.LastDungeonFloor) ?? info.LastDungeonFloor
        };
    }

    /// <summary>
    /// Debug discovery log of every container title the player opens while on a Catacombs floor or at
    /// the Dungeon Hub (other than storage or Kuudra chests). The real format is now verified
    /// (reward chests are titled "Wood".."Bedrock" with an "Open Reward Chest" button, see
    /// <see cref="DungeonRewardChestParser"/>), so this only remains as a Debug aid - throttled to at
    /// most once per distinct title per player per <see cref="DiscoveryLogThrottle"/>.
    /// </summary>
    private void LogDungeonAreaContainerDiscovery(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        var title = chest?.Name;
        if (string.IsNullOrEmpty(title) || chest.Items == null)
            return;
        if (title == KuudraPaidChest || title == KuudraFreeChest)
            return; // Kuudra, not a dungeon chest - excluded, see HandleInventory
        if (!StorageListener.IsNotStorage(chest))
            return; // the player's own storage, not a dungeon reward/menu container

        var location = args.currentState.ExtractedInfo.CurrentLocation;
        if (!Tasks.DungeonRewardAttribution.IsFloorZone(location) && Tasks.SkyblockZones.Canonical(location) != "Dungeon Hub")
            return;

        var playerId = args.msg.PlayerId ?? args.currentState.PlayerId;
        var throttleKey = (playerId, title);
        var now = DateTime.UtcNow;
        if (discoveryLogThrottle.TryGetValue(throttleKey, out var last) && now - last < DiscoveryLogThrottle)
            return;
        discoveryLogThrottle[throttleKey] = now;

        var itemDescriptions = new List<string>();
        foreach (var item in chest.Items)
        {
            if (itemDescriptions.Count >= DiscoveryLogMaxItems)
                break;
            var name = DungeonRewardChestParser.Strip(item?.ItemName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (name.Contains("Reward", StringComparison.Ordinal) || name.Contains("Open", StringComparison.Ordinal))
            {
                var lore = DungeonRewardChestParser.Strip(item.Description ?? string.Empty).Replace("\n", " | ");
                itemDescriptions.Add(string.IsNullOrEmpty(lore) ? name : $"{name} [{lore}]");
            }
            else
            {
                itemDescriptions.Add(name);
            }
        }
        Logger.LogDebug("Dungeon area container {title} for {playerId} at {location}: {items}",
            title, playerId, location, string.Join(", ", itemDescriptions));
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
        if (pending.CostCoins <= 0)
            return; // free chest: only the "<TIER> CHEST REWARDS" chat line confirms it (a purse drop would be meaningless)
        if (currentPurse is null or <= 0)
            return; // purse unknown - wait for a later valid reading, or expiry
        if (pending.PurseAtOpen - currentPurse.Value < pending.CostCoins)
            return; // purse has not dropped enough (yet) to confirm the purchase

        Charge(args, pending);
    }
}
