using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class CollectionListener : UpdateListener
{
    private const string MithrilPowderTag = "MITHRIL_POWDER";
    private Dictionary<string, string> NametoTagLookup;
    // Clean item prices are global (identical for every player), so fetching the
    // full sniper + bazaar price lists per location change was the main processing
    // bottleneck. Cache the merged lookup and refresh it at most once per interval.
    private Dictionary<string, double> cachedCleanPrices;
    private DateTime cleanPricesFetchedAt = DateTime.MinValue;
    private DateTime cleanPricesRetryAfter = DateTime.MinValue;
    private readonly System.Threading.SemaphoreSlim cleanPricesLock = new(1, 1);
    private static readonly TimeSpan CleanPricesCacheDuration = TimeSpan.FromMinutes(2);
    // when a refresh fails, wait this long before hitting the (failing) dependency again
    // instead of retrying on every single scoreboard message
    private static readonly TimeSpan CleanPricesFailureBackoff = TimeSpan.FromSeconds(15);
    private static readonly Dictionary<string, double> EmptyCleanPrices = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> lastLiveClassification = new();
    private static readonly Prometheus.Counter PeriodClassifiedCounter = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_classified_total", "Periods attributed to a task by the classifier", "task");
    private static readonly Prometheus.Counter PeriodUnclassifiedCounter = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_task_unclassified_total", "Periods the classifier could not attribute to any task");
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
        {
            await HandleScoreboard(args);
            return;
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
        {
            HandleInventory(args);
            HandleCroesusChest(args);
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Tab)
        {
            HandleTab(args);
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.CHAT)
        {
            // stash messages
            var chatBatch = args.msg.ChatBatch ?? [];
            var history = GetHistoryBeforeBatch(args.currentState.ChatHistory, chatBatch);
            for (var i = 0; i < chatBatch.Count; i++)
            {
                var uploadedLine = chatBatch[i];
                if (TryParseShardGain(uploadedLine, out var shardTag, out var shardCount, Logger))
                    TrackItem(args, shardTag, shardCount);
                if (TryParseNpcShardTrade(history.Concat(chatBatch.Take(i)), uploadedLine,
                    out shardTag, out var paymentTag))
                {
                    TrackItem(args, shardTag, 1);
                    TrackItem(args, paymentTag, -1);
                }
                if (TryParseSafariShardRewards(uploadedLine, out var shardRewards))
                    ReconcileSafariShards(args, shardRewards);
                if (uploadedLine.StartsWith("Added items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.StartsWith("Removed items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.Contains("Chameleon (0."))
                    Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, "SHARD_CHAMELEON", 1);
            }
        }
    }

    private static void HandleTab(UpdateArgs args)
    {
        var mithrilPowder = ParseMithrilPowderFromTab(args.msg.Tab);
        if (!mithrilPowder.HasValue)
            return;

        TrackMithrilPowder(args, mithrilPowder.Value);
    }

    internal static int? ParseMithrilPowderFromTab(IEnumerable<string>? tab)
    {
        if (tab == null)
            return null;

        foreach (var rawLine in tab)
        {
            var line = StripFormatting(rawLine);
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("Mithril Powder", StringComparison.OrdinalIgnoreCase))
                continue;

            var labelMatch = Regex.Match(line, @"Mithril Powder\s*:\s*([\d,]+)", RegexOptions.IgnoreCase);
            if (labelMatch.Success && TryParseNumber(labelMatch.Groups[1].Value, out var byLabel))
                return byLabel;

            var reverseMatch = Regex.Match(line, @"([\d,]+)\s*Mithril Powder", RegexOptions.IgnoreCase);
            if (reverseMatch.Success && TryParseNumber(reverseMatch.Groups[1].Value, out var byReverse))
                return byReverse;
        }

        return null;
    }

    internal static void TrackMithrilPowder(UpdateArgs args, int currentMithrilPowder)
    {
        if (currentMithrilPowder < 0)
            return;

        var previousMithrilPowder = args.currentState.ExtractedInfo.MithrilPowder;
        if (previousMithrilPowder > 0 && currentMithrilPowder > previousMithrilPowder)
        {
            var diff = currentMithrilPowder - previousMithrilPowder;
            Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, MithrilPowderTag, diff);
        }

        args.currentState.ExtractedInfo.MithrilPowder = currentMithrilPowder;
    }

    private static bool TryParseNumber(string value, out int parsed)
    {
        return int.TryParse(value.Replace(",", ""), out parsed);
    }

    private static string StripFormatting(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return Regex.Replace(value, "§.", string.Empty);
    }

    /// <summary>
    /// A derived (unmapped - not found in <see cref="Constants.ShardNames"/>) fallback tag is only
    /// trustworthy when the raw shard name is actually just a mob name. Production zone "Critter
    /// Safari" showed lines like "You caught a Bluebird and gained 2x Bluebird Shards!" - the count
    /// written as "2x" (digits-then-x) rather than "x2" fell through the primary "gained" regex
    /// (which only recognized "x2"), so the fallback "caught/received" regex's lazy match swallowed
    /// the rest of the sentence up to the real "Shards!", producing a shard name of "Bluebird and
    /// gained 2x Bluebird" and a bogus tag "SHARD_BLUEBIRD_AND_GAINED_2X_BLUEBIRD". The digits-then-x
    /// order is now parsed directly (see below), but this guard stays as defense in depth against
    /// any other sentence fragment leaking into an unmapped shard name in the future.
    /// </summary>
    private static readonly Regex SuspiciousShardNameFragment = new(@"\band\b|\d+x\b|\bx\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool TryParseShardGain(string uploadedLine, out string tag, out int count, ILogger logger = null)
    {
        // New captures say "caught ... and gained a/x2/2x ... Shard"; older catches and loot share
        // say "You caught/received a ... Shard" directly. Hypixel writes the gained count both as
        // "x2" (seen originally) and "2x" (seen in Critter Safari catch lines) - accept both orders.
        var line = StripFormatting(uploadedLine);
        var match = Regex.Match(line, @"\bgained (a|an|x[\d,]+|[\d,]+x) (.+?) Shards?!", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(line, @"\bYou (?:caught|received) (a|an|x[\d,]+|[\d,]+x) (.+?) Shards?(?:!| for\b)",
                RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            tag = string.Empty;
            count = 0;
            return false;
        }
        var shardName = match.Groups[2].Value.Trim();
        count = ParseShardAmount(match.Groups[1].Value);
        tag = GetShardTag(shardName);
        if (!Constants.ShardNames.ContainsKey(shardName) && SuspiciousShardNameFragment.IsMatch(shardName))
        {
            // The derived tag carries sentence-fragment leftovers (an "and"/leftover count token) -
            // almost certainly a regex matching too much of the surrounding sentence. Reject rather
            // than store a garbage per-mob tag; log so it stays discoverable which line caused it.
            logger?.LogInformation(
                "Rejecting suspicious derived shard tag {tag} for unmapped name {shardName} from line: {line}",
                tag, shardName, uploadedLine);
            tag = string.Empty;
            count = 0;
            return false;
        }
        return true;
    }

    private static int ParseShardAmount(string amount)
    {
        if (amount.StartsWith("x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(amount[1..].Replace(",", ""));
        if (amount.EndsWith("x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(amount[..^1].Replace(",", ""));
        return 1;
    }

    internal static bool TryParseNpcShardTrade(IEnumerable<string> previousLines, string uploadedLine,
        out string shardTag, out string paymentTag)
    {
        shardTag = string.Empty;
        paymentTag = string.Empty;
        var completion = Regex.Match(StripFormatting(uploadedLine),
            @"\bYou have been given (?:a|an) (.+?)!$", RegexOptions.IgnoreCase);
        if (!completion.Success)
            return false;

        var shardName = completion.Groups[1].Value.Trim();
        var context = previousLines.Select(StripFormatting).TakeLast(20).ToList();
        for (var i = context.Count - 1; i >= 0; i--)
        {
            var payment = Regex.Match(context[i],
                @"\bin exchange for(?:, say,)? (?:a|an) (.+?)[!?.]*$", RegexOptions.IgnoreCase);
            if (!payment.Success)
                continue;
            if (!context.Take(i).Reverse().Any(line =>
                line.Contains(shardName + " Shard", StringComparison.OrdinalIgnoreCase)))
                return false;

            shardTag = GetShardTag(shardName);
            paymentTag = GetItemTag(payment.Groups[1].Value);
            return true;
        }
        return false;
    }

    private static IEnumerable<string> GetHistoryBeforeBatch(Queue<Models.ChatMessage> history,
        List<string> chatBatch)
    {
        var saved = history.ToList();
        if (saved.Count >= chatBatch.Count && saved.TakeLast(chatBatch.Count).Select(m => m.Content).SequenceEqual(chatBatch))
            saved.RemoveRange(saved.Count - chatBatch.Count, chatBatch.Count);
        return saved.Select(m => m.Content);
    }

    private static string GetShardTag(string shardName)
    {
        // The mob display name does not always match its shard tag stem (e.g. "Lotusfish" -> LOTUS_FISH,
        // "Cinderbat" -> CINDER_BAT, "Bogged" -> SEA_ARCHER). Prefer the canonical map, fall back to the
        // naive derivation so unknown/new shards are still recorded rather than dropped.
        return Constants.ShardNames.TryGetValue(shardName, out var mapped)
            ? "SHARD_" + mapped.ToUpperInvariant()
            : "SHARD_" + GetItemTag(shardName);
    }

    internal static bool TryParseSafariShardRewards(string message, out Dictionary<string, int> rewards)
    {
        rewards = new Dictionary<string, int>();
        var lines = StripFormatting(message).Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return false;
        var totalMatch = Regex.Match(lines[0], @"^SAFARI_SHARD_REWARDS ([\d,]+)$");
        if (!totalMatch.Success || !TryParseNumber(totalMatch.Groups[1].Value, out var expectedTotal))
            return false;

        foreach (var line in lines.Skip(1))
        {
            var reward = Regex.Match(line, @"^(.+?) x([\d,]+)$", RegexOptions.IgnoreCase);
            if (!reward.Success || !TryParseNumber(reward.Groups[2].Value, out var count))
                continue;
            var tag = GetShardTag(reward.Groups[1].Value.Trim());
            rewards[tag] = rewards.GetValueOrDefault(tag) + count;
        }
        return rewards.Count > 0 && rewards.Values.Sum(v => (long)v) == expectedTotal;
    }

    private void ReconcileSafariShards(UpdateArgs args, Dictionary<string, int> rewards)
    {
        // The summary can arrive after the player left: the Safari period was then already flushed with
        // the individually tracked catches, and reconciling now would book the whole reward a second
        // time at the next location (production: Safari shard sets at "Bazaar Alley"/"The Forge").
        // CurrentLocation is the last scoreboard zone, i.e. where the pending items were collected.
        var location = args.currentState.ExtractedInfo.CurrentLocation;
        if (Tasks.SkyblockZones.Canonical(location)?.StartsWith("Critter Safari", StringComparison.Ordinal) != true)
        {
            Logger.LogInformation("Safari reward summary for {player} arrived after leaving the Safari (now at {location}), not reconciling",
                args.currentState.PlayerId, location);
            return;
        }
        var collected = args.currentState.ItemsCollectedRecently;
        var previousTotal = collected.Where(item => item.Key.StartsWith("SHARD_", StringComparison.Ordinal))
            .Sum(item => (long)item.Value);
        foreach (var tag in collected.Keys.Where(tag => tag.StartsWith("SHARD_", StringComparison.Ordinal)).ToList())
            collected.Remove(tag);
        foreach (var reward in rewards)
            collected[reward.Key] = reward.Value;
        Logger.LogInformation("Reconciled Safari shards for {player}: {previous} tracked, {summary} in reward summary",
            args.currentState.PlayerId, previousTotal, rewards.Values.Sum(v => (long)v));
    }

    private static string GetItemTag(string itemName) =>
        Regex.Replace(itemName.Trim().ToUpperInvariant(), @"[^A-Z0-9]+", "_").Trim('_');

    private static void TrackItem(UpdateArgs args, string tag, int count)
    {
        Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, count);
    }

    /// <summary>
    /// Coin value of one flushed location fragment's collected items, routing pseudo tags
    /// (Tasks.PseudoItems - bazaar/auction purchase evidence, dungeon chest cost) through their
    /// fixed coin value instead of the market price lookup: an EVIDENCE tag (spending coins to buy
    /// something) is never profit (0/unit, regardless of how many "coins" were collected), and a
    /// COST tag's already-negative count is worth exactly 1 coin/unit so it reduces profit by
    /// exactly what was spent. Exposed (internal, static) so it is unit testable without the full
    /// UpdateArgs/service plumbing StoreLocationProfit needs.
    /// </summary>
    internal static long ComputeProfit(Dictionary<string, int> collected, Dictionary<string, double> cleanPrices)
    {
        return (long)collected.Select(c =>
        {
            var price = Tasks.PseudoItems.TryGetCoinValue(c.Key, out var pseudoValue)
                ? pseudoValue
                : cleanPrices.GetValueOrDefault(c.Key);
            return price * c.Value;
        }).Sum();
    }

    /// <summary>
    /// Window after the last transfer view (see <see cref="IsTransferView"/>) in which "[Sacks]" chat
    /// deltas are treated as a transfer or sale/purchase instead of loot. Hypixel batches the sack
    /// messages for up to 30 seconds, so the message can arrive that long after the view closed.
    /// </summary>
    internal static readonly TimeSpan SackNotificationTransferWindow = TimeSpan.FromSeconds(35);

    /// <summary>Only ignored sack notifications moving at least this many items in total are logged.</summary>
    private const long IgnoredSackLogMinimumCount = 1000;

    private async Task HandleSackNotification(UpdateArgs args, string uploadedLine)
    {
        var lastTransferViewAt = args.currentState.ExtractedInfo.LastTransferViewAt;
        // default(DateTime) means no transfer view was ever seen - never "recent"
        if (lastTransferViewAt != default && args.msg.ReceivedAt - lastTransferViewAt <= SackNotificationTransferWindow)
        {
            LogIgnoredSackChange(args, uploadedLine);
            return;
        }
        if (NametoTagLookup == null)
        {
            var itemApi = args.GetService<Items.Client.Api.IItemsApi>();
            var names = await itemApi.ItemNamesGetAsync();
            NametoTagLookup = names.Where(g => g.Name != null).GroupBy(g => g.Name).Select(g => g.First()).ToDictionary(n => n.Name, n => n.Tag);
        }
        var lines = uploadedLine.Split('\n').Skip(1).Reverse().Skip(2).ToList();
        foreach (var item in lines)
        {
            // @" \+([\d,]+) ([^(]+) "
            var match = Regex.Match(item, @" ([+-]?[\d,]+) ([^(]+) ");
            if (match.Success)
            {
                var itemName = match.Groups[2].Value.Trim();
                if (int.TryParse(match.Groups[1].Value.Replace(",", ""), out var count))
                {
                    var tag = NametoTagLookup.GetValueOrDefault(itemName);
                    if (tag == null)
                    {
                        Logger.LogDebug("Item not found in lookup: {itemName}", itemName);
                        continue;
                    }
                    Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, count);
                    Logger.LogDebug("Item collected from stash: {itemName} x{count} for player {playerId}", itemName, count, args.currentState.PlayerId);
                }
                else
                {
                    Logger.LogWarning("Failed to parse item count from chat: {value}", match.Groups[1].Value);
                }
            }
            else
            {
                Logger.LogDebug("Failed to match item from chat: {item}", item);
            }
        }
    }

    private void LogIgnoredSackChange(UpdateArgs args, string uploadedLine)
    {
        var changes = new List<(long Count, string Name)>();
        foreach (var item in uploadedLine.Split('\n').Skip(1).Reverse().Skip(2))
        {
            var match = Regex.Match(item, @" ([+-]?[\d,]+) ([^(]+) ");
            if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var count))
                changes.Add((count, match.Groups[2].Value.Trim()));
        }
        if (changes.Sum(c => Math.Abs(c.Count)) < IgnoredSackLogMinimumCount)
            return;
        var summary = string.Join(", ", changes.Take(5).Select(c => $"{(c.Count >= 0 ? "+" : "")}{c.Count} {c.Name}"));
        Logger.LogInformation("Ignored sack change after transfer view for {playerId}: {summary}", args.currentState.PlayerId, summary);
    }

    /// <summary>
    /// Cap for <see cref="Models.StateObject.KnownItemUuids"/> - see <see cref="RegisterKnownItemUuids"/>.
    /// </summary>
    private const int MaxKnownItemUuids = 1024;

    /// <summary>Absolute plain-count diff at which <see cref="HandleInventory"/> logs a "Bulk inventory change".</summary>
    private const int BulkChangeLogThreshold = 64;

    static void HandleInventory(UpdateArgs args)
    {
        var previousInventory = args.currentState.RecentViews.Reverse().Skip(1).FirstOrDefault();
        // before any early return, so every exit path records it
        if (IsTransferView(args.msg.Chest) || IsTransferView(previousInventory))
            args.currentState.ExtractedInfo.LastTransferViewAt = args.msg.ReceivedAt;
        if (previousInventory == null)
        {
            RegisterKnownItemUuids(args.currentState, args.msg.Chest);
            return;
        }
        Dictionary<string, List<Models.Item>> mapOfItems = GetLookupItemsByTag(previousInventory);
        try
        {
            if (previousInventory.Name != null && (!StorageListener.IsNotStorage(previousInventory)
                || IsBazaarWindow(previousInventory) || IsAuctionView(previousInventory) || IsSwapView(previousInventory)))
            {
                // if the previous inventory is a storage, bazaar, auction or swap view (trade, sack, NPC shop:
                // items move between it and the inventory, no loot), we don't want to track items collected -
                // but the known-uuid set must still learn this view's items regardless (see
                // RegisterKnownItemUuids's docs), so register before returning.
                args.GetService<ILogger<CollectionListener>>().LogDebug("Skipping item collection tracking for storage chest {chestName} for player {playerId}", previousInventory.Name, args.currentState.PlayerId);
                RegisterKnownItemUuids(args.currentState, args.msg.Chest);
                return;
            }
        }
        catch (System.Exception e)
        {
            args.GetService<ILogger<CollectionListener>>().LogError(e, "Failed to handle inventory for player {PlayerId} {previousInventory}", args.currentState.PlayerId, JsonConvert.SerializeObject(previousInventory));
        }
        var currentInventory = GetLookupItemsByTag(args.msg.Chest);
        var previousIdentities = GetAccessibleIdentities(previousInventory);
        var currentIdentities = GetAccessibleIdentities(args.msg.Chest);
        if (IsWholesaleInventorySwap(previousIdentities, currentIdentities, out var keptIdentities))
        {
            args.GetService<ILogger<CollectionListener>>().LogInformation(
                "Inventory swap detected for {playerId} at {location}: {previousCount} -> {currentCount} distinct items, {overlap} kept",
                args.currentState.PlayerId, args.currentState.ExtractedInfo.CurrentLocation,
                previousIdentities.Count, currentIdentities.Count, keptIdentities);
            RegisterKnownItemUuids(args.currentState, args.msg.Chest);
            return;
        }
        // Snapshot of uuids already known BEFORE this view's own items are registered below - a diff
        // must be judged against what was known before this view arrived, never against itself (see
        // RegisterKnownItemUuids, called last in this method).
        var known = args.currentState.KnownItemUuids;
        var knownUuids = known is { Count: > 0 } ? new HashSet<long>(known) : null;
        // First-time-drop guards (see the loop): never when the current view is a trade window, NPC
        // shop, sack or auction house (items there are swapped/bought, not dropped). A swap view as previous view already
        // returned above.
        var allowFirstTimeCounting = !IsSwapView(args.msg.Chest) && !IsAuctionView(args.msg.Chest);
        var previousIsRewardChest = IsRewardChestView(previousInventory);
        var movedIntoCurrentContainer = GetCurrentContainerTags(args.msg.Chest);
        var currentIsTransferView = IsTransferView(args.msg.Chest);
        // (a) every tag anywhere in the previous view, including its container part
        // (a reward chest's container part is ignored, see GetItemsForSwapGuards)
        var previousTags = GetItemsForSwapGuards(previousInventory).Where(i => i.Tag != null).Select(i => i.Tag).ToHashSet();
        // (b) every tag in any other recent view (the current view is the last entry)
        var olderViewTags = new HashSet<string>();
        foreach (var view in args.currentState.RecentViews.Take(Math.Max(0, args.currentState.RecentViews.Count - 1)))
        {
            foreach (var item in GetItemsForSwapGuards(view))
                if (item.Tag != null)
                    olderViewTags.Add(item.Tag);
        }
        foreach (var (tag, currentItems) in currentInventory)
        {
            if (!mapOfItems.TryGetValue(tag, out var previousItems))
            {
                // A tag new to the accessible inventory is a first-time drop (its full count, or only
                // never-seen uuids via the uuid branch below) - but only when it cannot be a mere swap
                // from somewhere else, see the guards below. Tags present in both views keep the
                // plain diff logic.
                if (!allowFirstTimeCounting || previousTags.Contains(tag) || olderViewTags.Contains(tag))
                    continue;
                previousItems = new List<Models.Item>();
            }
            if (currentItems.Any(i => TryGetItemUuidHash(i, out _)) || previousItems.Any(i => TryGetItemUuidHash(i, out _)))
            {
                // Non-stackable item (carries a uuid): dedupe by uuid instead of raw count. A
                // positive change only counts uuids genuinely never seen before - neither in the
                // previous view nor in the known set - so re-equipping/moving gear the player
                // already had (its count merely flipping between e.g. 1 and 2) is never counted as
                // "collected". A uuid item leaving produces no negative entry (moving to storage or
                // selling it is not a consumed resource) - handled implicitly, since only currentItems
                // ever contribute below.
                var previousUuids = previousItems
                    .Select(i => TryGetItemUuidHash(i, out var hash) ? (long?)hash : null)
                    .Where(hash => hash.HasValue).Select(hash => hash!.Value).ToHashSet();
                var newCount = 0;
                foreach (var item in currentItems)
                {
                    if (!TryGetItemUuidHash(item, out var hash))
                        continue;
                    if (previousUuids.Contains(hash) || (knownUuids != null && knownUuids.Contains(hash)))
                        continue;
                    // a real drop is created when it drops - gear that is much older was only moved
                    // here (auction house, trade, wardrobe, owned before we first saw the player).
                    // Reward chest loot is exempt: a Croesus chest may be claimed long after the run
                    // and it is unverified whether its items are created at claim or at run time.
                    if (!previousIsRewardChest && TryGetItemCreationTime(item, out var createdUtc)
                        && args.msg.ReceivedAt - createdUtc > MaxDropItemAge)
                        continue;
                    newCount++;
                }
                if (newCount != 0)
                    Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, newCount);
                continue;
            }
            var previousCount = previousItems.Sum(i => (long)(i.Count ?? 0));
            var currentCount = currentItems.Sum(i => (long)(i.Count ?? 0));
            var diff = currentCount - previousCount;
            // items shift-clicked into a storage/transfer view that is already the current view were
            // moved, not consumed (production: -64 GRIFFIN_FEATHER after an Ender Chest upload). In a
            // transfer view (sack, bazaar, auction, shop, trade, Hunting Box) they do not show up in the
            // container part, but leaving the inventory there is a deposit or sale all the same
            // (production: -115 MELON_BLOCK between "Emissary Sisko" and "Sack of Sacks", whose sack
            // message is ignored as a transfer - booking only the inventory side was a phantom loss).
            if (diff < 0 && (currentIsTransferView || movedIntoCurrentContainer.Contains(tag)))
                continue;
            if (diff == 0)
                continue;
            Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, diff);
            // TEMPORARY diagnostic: learn which view pairs still produce phantom shard gains/losses
            // (e.g. bazaar GUI titles IsBazaarWindow does not know yet). Shards only: captured shards go
            // to the Hunting Box and are counted from chat, so a bulk change in the inventory is suspect,
            // while for every other item it is ordinary play (10k lines/hour when it was not restricted).
            if (Math.Abs(diff) >= BulkChangeLogThreshold && tag.StartsWith("SHARD_", StringComparison.Ordinal))
                args.GetService<ILogger<CollectionListener>>().LogInformation(
                    "Bulk inventory change for {playerId}: {count}x {tag} between views {previousView} -> {currentView}",
                    args.currentState.PlayerId, diff, tag, previousInventory.Name ?? "<inventory>", args.msg.Chest.Name ?? "<inventory>");
        }
        RegisterKnownItemUuids(args.currentState, args.msg.Chest);

        static Dictionary<string, List<Models.Item>> GetLookupItemsByTag(Models.ChestView? previousInventory)
        {
            // skip more than the 4 lines above and maybe 1 offhand slot
            var accessibleInventory = previousInventory.Items.Skip(previousInventory.Items.Count - 36 / 9 * 9).Take(36).ToList();
            return accessibleInventory
                .Where(i => i.Tag != null && i.ItemName != null)
                .GroupBy(i => i.Tag)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }

    /// <summary>
    /// When <paramref name="view"/> is a storage or transfer view (the same set the previous-view early
    /// return in <see cref="HandleInventory"/> skips), the tags in its container part (everything before
    /// the last 36 accessible inventory slots): a negative diff of such a tag means the player moved the
    /// items into the container, not that they were consumed. Empty for every other view.
    /// </summary>
    internal static HashSet<string> GetCurrentContainerTags(Models.ChestView view)
    {
        var tags = new HashSet<string>();
        if (view?.Name == null || view.Items == null)
            return tags;
        if (StorageListener.IsNotStorage(view) && !IsBazaarWindow(view) && !IsAuctionView(view) && !IsSwapView(view))
            return tags;
        foreach (var item in view.Items.Take(Math.Max(0, view.Items.Count - 36)))
            if (item.Tag != null)
                tags.Add(item.Tag);
        return tags;
    }

    /// <summary>
    /// Identities of the distinct items in the accessible inventory part (same 36-slot slicing as
    /// the local GetLookupItemsByTag in <see cref="HandleInventory"/>): the uuid hash for non-stackable
    /// items, otherwise the tag. SKYBLOCK_MENU (in every inventory) and tagless items are ignored.
    /// </summary>
    internal static HashSet<string> GetAccessibleIdentities(Models.ChestView view)
    {
        var accessible = view.Items.Skip(view.Items.Count - 36 / 9 * 9).Take(36);
        var identities = new HashSet<string>();
        foreach (var item in accessible)
        {
            if (item.Tag == null || item.Tag == "SKYBLOCK_MENU")
                continue;
            identities.Add(TryGetItemUuidHash(item, out var hash) ? "uuid:" + hash : "tag:" + item.Tag);
        }
        return identities;
    }

    /// <summary>
    /// True when the player's whole accessible inventory was replaced in one update: at least 4
    /// distinct items before, at most 10% of them still present, and a non-empty current inventory.
    /// This happens on Rift entry/exit (the Rift has its own separate inventory that replaces the
    /// normal one and vice versa) and on a profile switch. In normal play 4+ distinct items never
    /// all vanish in a single update without a container/trade view, which the earlier guards in
    /// <see cref="HandleInventory"/> already handle - so such an update is a swap, not loot.
    /// </summary>
    internal static bool IsWholesaleInventorySwap(HashSet<string> previous, HashSet<string> current, out int overlap)
    {
        overlap = previous.Count(current.Contains);
        return previous.Count >= 4 && overlap * 10 <= previous.Count && current.Count > 0;
    }

    /// <summary>
    /// True for a dungeon ("Wood".."Bedrock") or Kuudra ("Paid Chest"/"Free Chest") reward chest view -
    /// every one has the "Open Reward Chest" button <see cref="DungeonRewardChestParser"/> keys on.
    /// </summary>
    internal static bool IsRewardChestView(Models.ChestView? view) =>
        view != null && DungeonRewardChestParser.TryParse(view, null, out _);

    /// <summary>
    /// The items of <paramref name="view"/> that count as "already had this" evidence for the
    /// first-time-drop swap guards and the known-uuid set. A reward chest's container part previews
    /// the loot it is about to hand out (production 2026-10-01: Necron's Handle, Shadow Fury, ... were
    /// bought for 100M coins but never counted), so only its last 36 accessible-inventory slots (the
    /// same slicing as GetLookupItemsByTag in <see cref="HandleInventory"/>) are used there; every
    /// other view is used whole.
    /// </summary>
    private static IEnumerable<Models.Item> GetItemsForSwapGuards(Models.ChestView? view)
    {
        var items = view?.Items;
        if (items == null)
            return [];
        if (!IsRewardChestView(view))
            return items;
        return items.Skip(Math.Max(0, items.Count - 36));
    }

    /// <summary>
    /// Adds every item's uuid (if any - see <see cref="TryGetItemUuidHash"/>) from a just-seen
    /// inventory/container view into the player's bounded <see cref="Models.StateObject.KnownItemUuids"/>,
    /// oldest-first eviction once <see cref="MaxKnownItemUuids"/> is exceeded. Called for every view
    /// the service sees - the player's own inventory (see <see cref="HandleInventory"/>, on every
    /// exit path, not just the counting one) AND storage containers (see
    /// <see cref="StorageListener.Process"/>) - so gear the player already owns is never later
    /// miscounted as "new" collected revenue just because one particular pairing skipped the count
    /// diff (storage chests, Bazaar menus, ...).
    /// </summary>
    internal static void RegisterKnownItemUuids(Models.StateObject state, Models.ChestView? view)
    {
        if (view?.Items == null || state == null)
            return;
        // a reward chest's container part only previews its loot - registering it would make the real
        // drop look already known, see GetItemsForSwapGuards
        var items = GetItemsForSwapGuards(view);
        var known = state.KnownItemUuids ??= new Queue<long>();
        HashSet<long> seen = null;
        foreach (var item in items)
        {
            if (!TryGetItemUuidHash(item, out var hash))
                continue;
            seen ??= new HashSet<long>(known);
            if (!seen.Add(hash))
                continue; // already known, or already added earlier from this same view
            known.Enqueue(hash);
            if (known.Count > MaxKnownItemUuids)
                known.Dequeue();
        }
    }

    /// <summary>
    /// Non-stackable SkyBlock items carry a unique id in <c>ExtraAttributes["uuid"]</c>; stackable
    /// resources never do. Returns a stable 64-bit hash of it (FNV-1a - deterministic across
    /// processes/restarts/pods, unlike <see cref="string.GetHashCode()"/>, which is randomized per
    /// process and would make the persisted known set meaningless) so <see cref="Models.StateObject.KnownItemUuids"/>
    /// can store it compactly instead of the full uuid string.
    /// </summary>
    internal static bool TryGetItemUuidHash(Models.Item item, out long hash)
    {
        hash = 0;
        if (item?.ExtraAttributes == null || !item.ExtraAttributes.TryGetValue("uuid", out var raw))
            return false;
        var uuid = raw?.ToString();
        if (string.IsNullOrEmpty(uuid))
            return false;
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var fnv = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(uuid))
        {
            fnv ^= b;
            fnv *= prime;
        }
        hash = unchecked((long)fnv);
        return true;
    }

    /// <summary>
    /// Items whose creation timestamp is older than this relative to the inventory update are the
    /// player's pre-existing gear, not a drop (reward chest items are created when the chest is opened,
    /// crafted/forged items when claimed).
    /// </summary>
    internal static readonly TimeSpan MaxDropItemAge = TimeSpan.FromHours(1);

    private const string LegacyItemTimestampFormat = "M/d/yy h:mm tt";

    /// <summary>
    /// Reads the creation time from <c>ExtraAttributes["timestamp"]</c>: unix milliseconds (values
    /// below 100000000000 are treated as unix seconds) as a number or numeric string, or the legacy
    /// Hypixel string format <c>M/d/yy h:mm tt</c>. The raw value may be any deserializer output
    /// (long, double, string, JValue, JsonElement). Returns false when missing or unparseable.
    /// </summary>
    internal static bool TryGetItemCreationTime(Models.Item item, out DateTime createdUtc)
    {
        createdUtc = default;
        if (item?.ExtraAttributes == null || !item.ExtraAttributes.TryGetValue("timestamp", out var raw) || raw == null)
            return false;
        if (raw is Newtonsoft.Json.Linq.JValue jValue)
            raw = jValue.Value;
        if (raw is System.Text.Json.JsonElement element)
        {
            raw = element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => element.GetDouble(),
                System.Text.Json.JsonValueKind.String => element.GetString(),
                _ => null
            };
        }
        double number;
        switch (raw)
        {
            case null:
                return false;
            case string text:
                text = text.Trim();
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                {
                    if (!DateTime.TryParseExact(text, LegacyItemTimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out createdUtc))
                        return false;
                    return true;
                }
                break;
            case IConvertible convertible when raw is not bool and not char and not DateTime:
                try { number = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception) { return false; }
                break;
            default:
                return false;
        }
        if (double.IsNaN(number) || double.IsInfinity(number) || number <= 0)
            return false;
        try
        {
            var millis = number < 100000000000d ? number * 1000 : number;
            createdUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)millis).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Views where items move between inventory, sacks, bazaar and NPCs instead of being looted:
    /// <see cref="IsSwapView"/> (trade, sack, shop), bazaar and auction house windows. Sack chat deltas right after
    /// one are transfers/sales - see <see cref="HandleSackNotification"/>.
    /// </summary>
    internal static bool IsTransferView(Models.ChestView? view)
    {
        if (view?.Name == null)
            return false;
        return IsSwapView(view) || IsBazaarWindow(view) || IsAuctionView(view);
    }

    /// <summary>
    /// Views whose items are swapped with the inventory rather than dropped into it: trade windows
    /// (name starts with "You    "), sacks, NPC shops and the Hunting Box (production: 64 shards
    /// withdrawn from it counted as a gain, the bazaar sale afterwards was skipped). First-time
    /// counting is skipped around them.
    /// </summary>
    private static bool IsSwapView(Models.ChestView view)
    {
        var name = view?.Name;
        if (name == null)
            return false;
        return name.StartsWith("You    ") || name.Contains("Sack") || name.Contains("Shop") || name.Contains("Trades")
            || name.Contains("Hunting Box");
    }

    /// <summary>
    /// Every bazaar GUI whose items/refunds move between inventory and orders. The sub-menu titles
    /// ("Order options", the amount/price prompts) come from knowledge of the Hypixel GUI - an
    /// unrecognised one shows up as a "Bulk inventory change" log line in <see cref="HandleInventory"/>.
    /// </summary>
    private static bool IsBazaarWindow(Models.ChestView view)
    {
        // maybe also check if previous Item was actually present
        var name = view.Name;
        return name.EndsWith("Bazaar Orders")
            || name.Contains('➜') // for insta sells
            || name.Contains("Confirm") // order create / confirm sell offer
            || name is "Order options" or "How many do you want?" or "How much do you want to pay?" or "At what price are you selling?"
            || name.StartsWith("Bazaar");
    }

    /// <summary>
    /// Auction House GUIs ("Auction View", "BIN Auction View", "Auction House", "Auctions Browser",
    /// "Manage Auctions", "Create Auction", "Create BIN Auction", "Your Bids"): items arriving around
    /// them are purchases/returns, never loot.
    /// </summary>
    private static bool IsAuctionView(Models.ChestView view)
    {
        var name = view?.Name;
        return name != null && (name.Contains("Auction") || name == "Your Bids");
    }

    /// <summary>
    /// Returns the merged clean price lookup (bazaar sell price overlaid with auction prices),
    /// refreshing the cached copy at most once per <see cref="CleanPricesCacheDuration"/>.
    /// </summary>
    private async Task<Dictionary<string, double>> GetCleanPrices(UpdateArgs args)
    {
        if (cachedCleanPrices != null && DateTime.UtcNow - cleanPricesFetchedAt < CleanPricesCacheDuration)
            return cachedCleanPrices;
        await cleanPricesLock.WaitAsync();
        try
        {
            // double check now that we hold the lock so only one caller refreshes
            if (cachedCleanPrices != null && DateTime.UtcNow - cleanPricesFetchedAt < CleanPricesCacheDuration)
                return cachedCleanPrices;
            // a recent refresh failed; serve stale/empty rather than hammering a failing dependency
            // (and rather than throwing, which would fail the entire state update and trigger backoff)
            if (DateTime.UtcNow < cleanPricesRetryAfter)
                return cachedCleanPrices ?? EmptyCleanPrices;
            try
            {
                var cleanPrices = new Dictionary<string, double>();
                var ahPrices = await args.GetService<ISniperApi>().ApiSniperPricesCleanGetAsync();
                var bazaarPrices = await args.GetService<IBazaarApi>().GetAllPricesAsync();
                foreach (var item in bazaarPrices)
                {
                    // keep the full precision: an (int) cast floored fractional prices and, worse,
                    // dropped every sub-1-coin bazaar item to 0 so it valued as unpriced and fell
                    // out of the period total. Matches TaskPriceService, which uses the double.
                    cleanPrices[item.ProductId] = (double)item.SellPrice;
                }
                foreach (var item in ahPrices)
                {
                    if (item.Value > 0)
                        cleanPrices[item.Key] = item.Value;
                }
                cachedCleanPrices = cleanPrices;
                cleanPricesFetchedAt = DateTime.UtcNow;
                return cachedCleanPrices;
            }
            catch (Exception e)
            {
                // Degrade gracefully: a failing price service must not fail location-profit tracking
                // for every player. Serve the last known prices (or nothing) and back off.
                cleanPricesRetryAfter = DateTime.UtcNow + CleanPricesFailureBackoff;
                Logger.LogError(e, "failed to refresh clean prices, serving {count} stale entries until {retryAfter:O}",
                    cachedCleanPrices?.Count ?? 0, cleanPricesRetryAfter);
                return cachedCleanPrices ?? EmptyCleanPrices;
            }
        }
        finally
        {
            cleanPricesLock.Release();
        }
    }

    private async Task HandleScoreboard(UpdateArgs args)
    {
        // 07/14/15
        var currentDate = DateTime.UtcNow.ToString("MM/dd/yy");
        var yesterdayDate = DateTime.UtcNow.AddDays(-1).ToString("MM/dd/yy");
        var server = args.msg.Scoreboard?.FirstOrDefault(s => s.StartsWith(currentDate) || s.StartsWith(yesterdayDate))?.Split(' ').ElementAtOrDefault(1);
        if (server != null)
        {
            args.currentState.ExtractedInfo.CurrentServer = server;
        }
        // parse currencies before the area early-return below so the purse/bits stay current
        // even on scoreboards without an area line (e.g. hub, island)
        var purse = ScoreboardParser.ParsePurse(args.msg.Scoreboard);
        if (purse.HasValue)
            args.currentState.ExtractedInfo.Purse = purse.Value;
        var bits = ScoreboardParser.ParseBits(args.msg.Scoreboard);
        if (bits.HasValue)
            args.currentState.ExtractedInfo.Bits = bits.Value;
        var currentLocation = ScoreboardParser.ExtractArea(args.msg.Scoreboard);
        if (currentLocation == null)
        {
            return;
        }
        // In the Rift several zone names collide with overworld ones (e.g. "Wizard Tower"), and an
        // unregistered Rift zone must never be mistaken for an overworld one - so only a name that is
        // explicitly registered as a Rift zone is used, everything else becomes the literal "The Rift".
        if (ScoreboardParser.IsRiftScoreboard(args.msg.Scoreboard)
            && Tasks.SkyblockZones.IslandOf(currentLocation) != "Rift")
        {
            currentLocation = "The Rift";
        }
        var now = DateTime.UtcNow;
        var previousLocation = args.currentState.ExtractedInfo.CurrentLocation;
        var zoneChanged = previousLocation != null && previousLocation != currentLocation;
        if (!zoneChanged)
        {
            // Reconfirm we're still in the same zone as of this scoreboard update - bumped on
            // every tick (not just a flush) so a live classification in between flushes always
            // sees a fresh CurrentLocationSeenAt (see TaskClassifier.Classify's tab-fallback gate).
            args.currentState.ExtractedInfo.CurrentLocationSeenAt = now;
        }
        if (zoneChanged
            // if the same location is used, attempt to store it for people staying in same location
            || args.currentState.ExtractedInfo.LastLocationChange < now.AddMinutes(-5))
        {
            // Flush/classify the OLD fragment using the OLD zone's CurrentLocationSince/SeenAt
            // (for the same-zone path these were just bumped above; for a real zone change they
            // still describe the zone that just ended - only updated below, AFTER this flush).
            await StoreLocationProfit(args, previousLocation);
        }
        args.currentState.ExtractedInfo.CurrentLocation = currentLocation;
        if (zoneChanged)
        {
            args.currentState.ExtractedInfo.CurrentLocationSince = now;
            args.currentState.ExtractedInfo.CurrentLocationSeenAt = now;
        }
        // Track the last confirmed Catacombs floor zone (Entrance excluded - no reward chests there)
        // so a later "Dungeon Hub" reward-claim period can be folded back into the run it belongs to
        // - see Tasks.DungeonRewardAttribution/StoreLocationProfit. Bumped on every tick (not just a
        // zone change) so LastDungeonFloorAt ends up as the time the player was last seen inside.
        if (Tasks.DungeonRewardAttribution.IsFloorZone(currentLocation))
        {
            args.currentState.ExtractedInfo.LastDungeonFloor = currentLocation;
            args.currentState.ExtractedInfo.LastDungeonFloorAt = now;
        }
        if (Tasks.KuudraRewardAttribution.IsTierZone(currentLocation))
        {
            args.currentState.ExtractedInfo.LastKuudraTier = Tasks.SkyblockZones.Canonical(currentLocation);
            args.currentState.ExtractedInfo.LastKuudraTierAt = now;
        }
        await ClassifyLive(args);
    }

    /// <summary>
    /// A chest view whose title names a Catacombs floor (Croesus' reward-claim menu, e.g. "Catacombs
    /// - Floor VII"/"Master Catacombs - Floor V", possibly truncated by the 32 char inventory
    /// title limit) names the floor whose chests are being claimed. This OVERRIDES the zone-sequence
    /// tracking in <see cref="HandleScoreboard"/> - a Croesus claim can be for an older run than the
    /// player's last actual floor visit - so it always wins regardless of what
    /// <see cref="Models.ExtractedInfo.LastDungeonFloor"/> currently holds.
    /// </summary>
    private static readonly Regex CroesusFloorTitleRegex = new(@"^(Master Mode |Master )?(The )?Catacombs - Floor ([IVX]+)", RegexOptions.Compiled);
    private static readonly Dictionary<string, int> RomanFloorNumerals = new(StringComparer.Ordinal)
    {
        ["I"] = 1, ["II"] = 2, ["III"] = 3, ["IV"] = 4, ["V"] = 5, ["VI"] = 6, ["VII"] = 7
    };

    /// <summary>
    /// Parses a Croesus reward-claim chest title into the floor zone string
    /// (<see cref="Models.ExtractedInfo.LastDungeonFloor"/> format, e.g. "The Catacombs (M3)"), or
    /// null when <paramref name="chestName"/> is not a Croesus floor title (or its roman numeral was
    /// truncated past a recognizable I-VII value - tolerant parsing means never guessing wrong,
    /// not forcing a match). Note the 32 char inventory title limit makes "Master Mode Catacombs -
    /// Floor III" (33 chars) truncate to exactly "Master Mode Catacombs - Floor II" - an irreducible
    /// ambiguity in the source data (M1/M2 fit within 32 chars and are unaffected), not something
    /// this parser can resolve; it is accepted as a known limitation rather than guessed around.
    /// </summary>
    private static readonly HashSet<string> CroesusMenuMarkers = new(StringComparer.Ordinal)
    {
        "Chest Modifiers", "Wood", "Gold", "Diamond", "Emerald", "Obsidian", "Bedrock"
    };

    /// <summary>
    /// Like <see cref="TryParseCroesusFloor(string)"/> but also requires the view to look like the
    /// Croesus floor menu (an item named "Chest Modifiers" or a bare chest tier). Guards against
    /// "The Catacombs - Floor I" style best-scores lists that share the title pattern.
    /// </summary>
    internal static string TryParseCroesusFloor(Models.ChestView chest)
    {
        if (chest?.Items == null
            || !chest.Items.Any(i => CroesusMenuMarkers.Contains(DungeonRewardChestParser.Strip(i?.ItemName))))
            return null;
        return TryParseCroesusFloor(chest.Name);
    }

    internal static string TryParseCroesusFloor(string chestName)
    {
        if (string.IsNullOrEmpty(chestName))
            return null;
        var clean = Tasks.SkyblockZones.Canonical(chestName);
        var match = CroesusFloorTitleRegex.Match(clean);
        if (!match.Success || !RomanFloorNumerals.TryGetValue(match.Groups[3].Value, out var floorNumber))
            return null;
        var isMaster = match.Groups[1].Success;
        return $"The Catacombs ({(isMaster ? "M" : "F")}{floorNumber})";
    }

    private static void HandleCroesusChest(UpdateArgs args)
    {
        var floor = TryParseCroesusFloor(args.msg.Chest);
        if (floor == null)
            return;
        // Production has no evidence this ever fires - Information (not Debug) so the next log
        // analysis can confirm whether Croesus floor menus are actually being recognised at all.
        args.GetService<ILogger<CollectionListener>>().LogInformation(
            "Croesus menu {title} for {playerId} resolved to {floor}", args.msg.Chest?.Name, args.currentState.PlayerId, floor);
        var info = args.currentState.ExtractedInfo;
        var stamp = DateTime.UtcNow;
        // DungeonRewardAttribution.ResolveLocation rejects a floor reading stamped after the period
        // start (right for scoreboard floor readings: a Dungeon Hub period before entering a floor
        // must not be attributed to it). A Croesus reading however always lands mid-period (enter the
        // hub, walk to Croesus, open the menu) and describes the claim period the player is in right
        // now, so stamp it with that period's start instead (production 2026-10-01: 5 Croesus claim
        // periods stayed "Dungeon Hub").
        if (Tasks.SkyblockZones.Canonical(info.CurrentLocation) == "Dungeon Hub"
            && info.LastLocationChange != default && info.LastLocationChange < stamp)
            stamp = info.LastLocationChange;
        info.LastDungeonFloor = floor;
        info.LastDungeonFloorAt = stamp;
    }

    /// <summary>
    /// Continuously attribute what the player is currently doing to a task based on the
    /// rolling collection window, throttled per player. Feeds the live doer counts.
    /// </summary>
    private async Task ClassifyLive(UpdateArgs args)
    {
        var state = args.currentState;
        var playerId = state.PlayerId;
        if (playerId == null || lastLiveClassification.TryGetValue(playerId, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(60))
            return;
        lastLiveClassification[playerId] = DateTime.UtcNow;
        try
        {
            if (args.GetService<Microsoft.Extensions.Configuration.IConfiguration>()["TASKS:CLASSIFY"] == "false")
                return;
            var now = DateTime.UtcNow;
            // measure the window from the session start (spans locations) so live doer
            // attribution of multi-location tasks is not reset on every area change
            var sessionStart = state.ExtractedInfo.CurrentSession?.StartTime;
            var windowStart = sessionStart is { } s && s != default ? s : state.ExtractedInfo.LastLocationChange;
            var minutes = (now - windowStart).TotalMinutes;
            // an active claim (not older than 30 min) biases the classifier
            var claim = GetActiveClaim(state, now);
            // stale prices (or none on startup) only weaken tie breaking, not matching
            var classifier = args.GetService<Tasks.TaskClassifier>();
            var classification = classifier.Classify(
                state.ExtractedInfo.CurrentLocation, state.ItemsCollectedRecently, minutes, claim, cachedCleanPrices,
                state.ExtractedInfo.CurrentIsland, state.ExtractedInfo.CurrentIslandAt,
                state.ExtractedInfo.CurrentLocationSince, state.ExtractedInfo.CurrentLocationSeenAt);
            var previous = state.ExtractedInfo.CurrentTask;
            state.ExtractedInfo.CurrentTask = classification?.TaskName;
            if (classification?.TaskName != previous)
                state.ExtractedInfo.CurrentTaskSince = DateTime.UtcNow;
            if (classification != null && state.McInfo.Uuid != default)
            {
                var activityService = args.GetService<Tasks.TaskActivityService>();
                var playerUuid = state.McInfo.Uuid.ToString("N");
                await activityService.MarkDoing(classification.TaskName, playerUuid);
                // derived tasks (e.g. "Sludge Mining (Gem Mixture)") share the primary's detection
                // and never get classified themselves, but players doing the primary count as
                // "doing this now" for them too - see TaskClassifier.GetDerivedTaskNames.
                foreach (var derivedName in classifier.GetDerivedTaskNames(classification.TaskName))
                    await activityService.MarkDoing(derivedName, playerUuid);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed live task classification for {playerId}", playerId);
        }
    }

    private async Task StoreLocationProfit(UpdateArgs args, string previousLocation)
    {
        var now = DateTime.UtcNow;
        var profit = 0L;
        var collected = args.currentState.ItemsCollectedRecently;
        var info = args.currentState.ExtractedInfo;
        var periodStart = info.LastLocationChange;
        // A "Dungeon Hub" period shortly after the player's last confirmed Catacombs floor is really
        // the run's own reward-claim, not hub activity - fold it back into the floor so its loot
        // (essence, fragments, ...) counts toward that floor's task/session instead of going
        // unclassified. See Tasks.DungeonRewardAttribution for the resolution rule.
        var resolvedLocation = Tasks.DungeonRewardAttribution.ResolveLocation(
            previousLocation, info.LastDungeonFloor, info.LastDungeonFloorAt, periodStart);
        if (resolvedLocation != previousLocation)
            Logger.LogInformation("Attributed Dungeon Hub reward claim of {playerId} to {floor}", args.currentState.PlayerId, resolvedLocation);
        // Kuudra claims (chest loot at the Dungeon Hub/Forgotten Skull) and key purchases (Scarleton)
        // take precedence over the Catacombs resolution - see Tasks.KuudraRewardAttribution.
        var kuudraLocation = Tasks.KuudraRewardAttribution.ResolveLocation(
            previousLocation, collected, info.LastKuudraTier, info.LastKuudraTierAt, periodStart);
        if (kuudraLocation != previousLocation)
        {
            resolvedLocation = kuudraLocation;
            Logger.LogInformation("Attributed Kuudra reward claim of {playerId} to {zone}", args.currentState.PlayerId, resolvedLocation);
        }
        // The run itself collects nothing (its tier-zone periods are never stored) and the claim period
        // lasts seconds: remember the empty run time and hand it to the claim period's start.
        var claimedToTier = collected.Count > 0 && kuudraLocation != previousLocation
            && Tasks.KuudraRewardAttribution.IsClaimLocation(previousLocation)
            && Tasks.KuudraRewardAttribution.HasClaimLoot(collected);
        info.KuudraPendingRunSeconds = Tasks.KuudraRewardAttribution.UpdatePendingRun(info.KuudraPendingRunSeconds,
            info.LastKuudraTierAt, now, collected.Count == 0 && Tasks.KuudraRewardAttribution.IsTierZone(previousLocation),
            now - periodStart, claimedToTier, out var claimShift);
        Dictionary<string, double> cleanPrices = null;
        if (collected.Count > 0)
        {
            cleanPrices = await GetCleanPrices(args);

            profit = ComputeProfit(collected, cleanPrices);
            var period = new TrackedProfitService.Period()
            {
                EndTime = now,
                StartTime = periodStart - claimShift,
                Location = resolvedLocation,
                PlayerUuid = args.currentState.McInfo.Uuid.ToString("N"),
                Server = args.currentState.ExtractedInfo.CurrentServer,
                ItemsCollected = new Dictionary<string, int>(args.currentState.ItemsCollectedRecently),
                Profit = profit
            };
            ClassifyPeriod(args, period, cleanPrices);
            await args.GetService<TrackedProfitService>().AddPeriod(period);
            try
            {
                await args.GetService<MethodAggregateService>().RecordPeriod(period);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to record method aggregate");
            }
            UnlockCollectionAchievements(args, period.ItemsCollected);
            args.SendDebugMessage("You collected a total of " + profit + " coins worth of items in " + resolvedLocation + " " + string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}")));
            Logger.LogInformation("Profit summary for {playerId} at {location}: {profit} coins from {items}", args.currentState.PlayerId, resolvedLocation, profit, string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}")));
        }
        // fold task attribution per session (spanning locations), not per location fragment
        await AccumulateSession(args, resolvedLocation, collected, cleanPrices, now);
        args.currentState.ItemsCollectedRecently.Clear();
        args.currentState.ExtractedInfo.LastLocationChange = now;
    }

    /// <summary>
    /// Merge the just-flushed location fragment into the running task session and fold
    /// the session when a boundary is crossed. Runs on every flush (including empty idle
    /// ticks) so a task spanning multiple locations accumulates into one session instead
    /// of being chopped below the classifier's minimum window. Failures only lose the
    /// contribution, never the raw period.
    /// </summary>
    private async Task AccumulateSession(UpdateArgs args, string previousLocation,
        Dictionary<string, int> collected, Dictionary<string, double> cleanPrices, DateTime now)
    {
        try
        {
            var config = args.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            if (config["TASKS:AGGREGATE"] == "false" || config["TASKS:CLASSIFY"] == "false")
                return;
            var state = args.currentState;
            if (state.McInfo.Uuid == default)
                return;
            var playerUuid = state.McInfo.Uuid.ToString("N");
            var fragment = new TrackedProfitService.Period()
            {
                EndTime = now,
                StartTime = state.ExtractedInfo.LastLocationChange,
                Location = previousLocation ?? state.ExtractedInfo.CurrentLocation,
                PlayerUuid = playerUuid,
                Server = state.ExtractedInfo.CurrentServer,
                ItemsCollected = new Dictionary<string, int>(collected)
            };
            var claim = GetActiveClaim(state, now);
            var flush = args.GetService<Tasks.TaskSessionService>()
                .Accumulate(state.ExtractedInfo, playerUuid, fragment, claim, cleanPrices ?? cachedCleanPrices, now);
            if (flush == null)
                return;
            if (flush.DetectedTask == null)
            {
                PeriodUnclassifiedCounter.Inc();
                return;
            }
            PeriodClassifiedCounter.WithLabels(flush.DetectedTask).Inc();
            var prices = cleanPrices ?? await GetCleanPrices(args);
            await args.GetService<Tasks.TaskPeriodFolder>().Fold(flush, state, prices);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to accumulate/fold task session for {playerId}", args.currentState.PlayerId);
        }
    }

    /// <summary>Returns the player's claimed task if it is set and not expired, clearing it otherwise.</summary>
    private static string GetActiveClaim(Models.StateObject state, DateTime now)
    {
        var claim = state.ExtractedInfo.ClaimedTask;
        if (claim != null && now - state.ExtractedInfo.ClaimedAt > TimeSpan.FromMinutes(30))
        {
            state.ExtractedInfo.ClaimedTask = null;
            return null;
        }
        return claim;
    }

    /// <summary>
    /// Attributes a flushed period to a task. Failures only lose the attribution,
    /// never the period itself.
    /// </summary>
    private void ClassifyPeriod(UpdateArgs args, TrackedProfitService.Period period, Dictionary<string, double> cleanPrices)
    {
        try
        {
            if (args.GetService<Microsoft.Extensions.Configuration.IConfiguration>()["TASKS:CLASSIFY"] == "false")
                return;
            // raw per-fragment attribution for the locationperiods.detectedtask column only;
            // the counters and the fold operate on the accumulated session (AccumulateSession).
            var info = args.currentState.ExtractedInfo;
            var classification = args.GetService<Tasks.TaskClassifier>().Classify(
                period.Location, period.ItemsCollected,
                (period.EndTime - period.StartTime).TotalMinutes, null, cleanPrices,
                info.CurrentIsland, info.CurrentIslandAt, info.CurrentLocationSince, info.CurrentLocationSeenAt);
            period.DetectedTask = classification?.TaskName;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to classify period at {location}", period.Location);
        }
    }

    /// <summary>
    /// Unlocks the collection related achievements for a just recorded <see cref="TrackedProfitService.Period"/>.
    /// </summary>
    internal static void UnlockCollectionAchievements(UpdateArgs args, Dictionary<string, int> itemsCollected)
    {
        try
        {
            const int farmerThreshold = 20_000;
            const int collectorDistinctKindsThreshold = 50;
            var achievements = args.GetService<IAchievementService>();
            if (itemsCollected.Values.Any(count => count > farmerThreshold))
                achievements.Unlock(args.currentState, Models.Achievement.Farmer);
            if (itemsCollected.Count >= collectorDistinctKindsThreshold)
                achievements.Unlock(args.currentState, Models.Achievement.Collector);
        }
        catch (Exception e)
        {
            // never let achievement bookkeeping break profit tracking
            args.GetService<ILogger<CollectionListener>>().LogError(e, "Error unlocking collection achievements");
        }
    }
}
