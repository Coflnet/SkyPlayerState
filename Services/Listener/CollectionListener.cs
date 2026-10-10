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
            var collectedBefore = new Dictionary<string, int>(args.currentState.ItemsCollectedRecently);
            ArmBazaarProductGuard(args);
            HandleInventory(args);
            HandleCroesusChest(args);
            await StartNewPeriodAfterLargeChange(args, collectedBefore);
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
                if (ParseKuudraEnd(uploadedLine) == true)
                    await HandleKuudraEnd(args);
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
        var craftIngredients = args.currentState.ExtractedInfo.LastCraftIngredients;
        var recentCraft = craftIngredients is { Count: > 0 } && IsRecentCraft(args);
        var craftSkipped = new List<(long Count, string Name)>();
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
                    // supercrafting takes the ingredients from the sacks: a conversion, not a loss
                    if (count < 0 && recentCraft && craftIngredients!.Contains(tag))
                    {
                        craftSkipped.Add((count, itemName));
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
        if (craftSkipped.Sum(c => Math.Abs(c.Count)) >= IgnoredSackLogMinimumCount)
            Logger.LogInformation("Ignored sack change after craft for {playerId}: {summary}", args.currentState.PlayerId, SummarizeSackChanges(craftSkipped));
    }

    private static string SummarizeSackChanges(List<(long Count, string Name)> changes)
        => string.Join(", ", changes.Take(5).Select(c => $"{(c.Count >= 0 ? "+" : "")}{c.Count} {c.Name}"));

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
        Logger.LogInformation("Ignored sack change after transfer view for {playerId}: {summary}", args.currentState.PlayerId, SummarizeSackChanges(changes));
    }

    /// <summary>
    /// Cap for <see cref="Models.StateObject.KnownItemUuids"/> - see <see cref="RegisterKnownItemUuids"/>.
    /// </summary>
    private const int MaxKnownItemUuids = 1024;

    /// <summary>Coin value of a single inventory change above which the period ends right after it.</summary>
    private const long NewPeriodChangeValue = 10_000_000;

    /// <summary>
    /// Coin value moved by one inventory update: every tag's count change times its price, as absolute
    /// values so a gain and a loss in the same update do not cancel out. Priced like
    /// <see cref="ComputeProfit"/>.
    /// </summary>
    internal static long InventoryChangeValue(Dictionary<string, int> before, Dictionary<string, int> after, Dictionary<string, double> cleanPrices)
    {
        var value = 0d;
        foreach (var tag in before.Keys.Union(after.Keys))
        {
            var change = (long)after.GetValueOrDefault(tag) - before.GetValueOrDefault(tag);
            if (change == 0)
                continue;
            var price = Tasks.PseudoItems.TryGetCoinValue(tag, out var pseudoValue) ? pseudoValue : cleanPrices.GetValueOrDefault(tag);
            value += Math.Abs(change * price);
        }
        return (long)value;
    }

    /// <summary>
    /// Ends the current period right after an inventory update that moved more than
    /// <see cref="NewPeriodChangeValue"/> coins, so the change can be checked in a period of its own
    /// instead of in whatever else piles up until the next scoreboard flush (production 2026-10-02:
    /// one -21B period with 213 item types for a player who had not sent a scoreboard for a long time).
    /// The flush comes after the change: costs (chest cost, purchase) are booked before the items show
    /// up in the inventory and stay in the same period.
    /// While a crafting view is open the period does not end: the input leaves the inventory before the
    /// output arrives, and a view uploaded in between shows only one side (production 2026-10-02: a
    /// -25M period "-320x GRIFFIN_FEATHER, -512x SOUL_STRING, 1x BRAIDED_GRIFFIN_FEATHER" followed by a
    /// +30M period "-160x GRIFFIN_FEATHER, 2x BRAIDED_GRIFFIN_FEATHER", both "Craft Item -> Craft Item").
    /// The period then ends with the first view after the crafting view, which shows what went in and
    /// what came out.
    /// </summary>
    private async Task StartNewPeriodAfterLargeChange(UpdateArgs args, Dictionary<string, int> collectedBefore)
    {
        var collected = args.currentState.ItemsCollectedRecently;
        var info = args.currentState.ExtractedInfo;
        var location = info.CurrentLocation;
        // CurrentLocation defaults to "Unknown" (absorbed by the hidden UnknownLocationTask), so this only guards against an explicit null
        if (location == null)
            return;
        var changed = collected.Count != collectedBefore.Count || collected.Except(collectedBefore).Any();
        var value = changed ? InventoryChangeValue(collectedBefore, collected, await GetCleanPrices(args)) : 0;
        var isLarge = value > NewPeriodChangeValue;
        if (IsCraftingView(args.msg.Chest))
        {
            info.PeriodSplitPending |= isLarge;
            return;
        }
        if (!isLarge && !info.PeriodSplitPending)
            return;
        var previousView = args.currentState.RecentViews.Reverse().Skip(1).FirstOrDefault()?.Name ?? "<inventory>";
        var currentView = args.msg.Chest?.Name ?? "<inventory>";
        if (isLarge)
            Logger.LogInformation("Inventory change worth {value} coins for {playerId} between views {previousView} -> {currentView}, starting a new period",
                value, args.currentState.PlayerId, previousView, currentView);
        else
            Logger.LogInformation("Crafting with a large inventory change ended for {playerId} between views {previousView} -> {currentView}, starting a new period",
                args.currentState.PlayerId, previousView, currentView);
        await StoreLocationProfit(args, location);
    }

    /// <summary>
    /// The crafting table ("Craft Item"), its quick crafting list and the recipe views (<see cref="TryGetRecipe"/>):
    /// views in which items are converted while the view stays open.
    /// </summary>
    internal static bool IsCraftingView(Models.ChestView? view)
    {
        return view?.Name is "Craft Item" or "Quick Crafting" || TryGetRecipe(view, out _, out _);
    }

    private static void LogIgnoredCraft(UpdateArgs args, int count, string tag, Models.ChestView recipeView)
    {
        args.GetService<ILogger<CollectionListener>>().LogInformation("Ignored crafted {count}x {tag} for {playerId} after recipe view {view}",
            count, tag, args.currentState.PlayerId, recipeView.Name);
    }

    /// <summary>Absolute plain-count diff at which <see cref="HandleInventory"/> logs a "Bulk inventory change".</summary>
    private const int BulkChangeLogThreshold = 64;

    /// <summary>
    /// Whether <paramref name="view"/> is a SkyBlock recipe view (the GUI with the "Supercraft" button),
    /// and its result and ingredient tags. Also what <see cref="RecipeUpdate.ReadRecipe"/> records recipes from:
    /// at least 90 items, "Supercraft" in slot 32, ingredients in slots 10-12, 19-21 and 28-30,
    /// result in slot 25. A view without a result tag is not a recipe. The button name is compared
    /// without color codes: the mod for current Minecraft versions uploads "Supercraft", older ones
    /// "§aSupercraft" (production 2026-10-02: matching only the latter recognised no recipe view).
    /// Some uploads of a recipe view have no button (production 2026-10-03, between two supercrafts:
    /// 64x HEAVY_GABAGOOL booked as 22.6M coins after each of them), so the "Crafting Table" icon in
    /// slot 23 ("Craft this recipe by using a crafting table or Supercraft.") counts as well.
    /// </summary>
    internal static bool TryGetRecipe(Models.ChestView? view, out string? resultTag, out HashSet<string> ingredientTags)
    {
        resultTag = null;
        ingredientTags = new HashSet<string>();
        var items = view?.Items;
        if (items == null || items.Count < 9 * 10)
            return false;
        var hasCraftingIcon = items[23] is { Tag: null } icon && StripFormatting(icon.ItemName) == "Crafting Table"
            && StripFormatting(icon.Description).Contains("Craft this recipe");
        if (StripFormatting(items[32]?.ItemName) != "Supercraft" && !hasCraftingIcon)
            return false;
        resultTag = items[25]?.Tag;
        if (resultTag == null)
            return false;
        foreach (var slot in new[] { 10, 11, 12, 19, 20, 21, 28, 29, 30 })
            if (items[slot]?.Tag is string tag)
                ingredientTags.Add(tag);
        return true;
    }

    /// <summary>
    /// Whether the latest recent view is a recipe view, or a craft was seen within
    /// <see cref="SackNotificationTransferWindow"/>; then the sack ingredient losses are a craft, not a loss.
    /// </summary>
    private static bool IsRecentCraft(UpdateArgs args)
    {
        if (TryGetRecipe(args.currentState.RecentViews.LastOrDefault(), out _, out _))
            return true;
        var lastCraftViewAt = args.currentState.ExtractedInfo.LastCraftViewAt;
        return lastCraftViewAt != default && args.msg.ReceivedAt - lastCraftViewAt <= SackNotificationTransferWindow;
    }

    /// <summary>
    /// Crafts at the crafting table: after the "Craft Item" view the ingredients are gone and the output
    /// shows up, which the diff would book as loot (production 2026-10-03: +1 BRAIDED_GRIFFIN_FEATHER
    /// as 25.7M coins of Diana, six times a night). Empty without the <see cref="CraftRecipeCache"/> service.
    /// </summary>
    private static (Dictionary<string, int> Output, Dictionary<string, int> Consumed) DetectTableCraft(UpdateArgs args,
        Models.ChestView previousInventory, Dictionary<string, List<Models.Item>> previousItems, Dictionary<string, List<Models.Item>> currentItems)
    {
        if (previousInventory.Name is not ("Craft Item" or "Quick Crafting"))
            return (new(), new());
        CraftRecipeCache? cache;
        try
        {
            cache = args.GetService<CraftRecipeCache>();
        }
        catch (System.Exception)
        {
            return (new(), new());
        }
        if (cache == null)
            return (new(), new());
        static Dictionary<string, int> Counts(Dictionary<string, List<Models.Item>> items)
            => items.ToDictionary(i => i.Key, i => i.Value.Sum(item => item.Count ?? 0));
        return CraftDetection.Detect(Counts(previousItems), Counts(currentItems), cache.Get);
    }

    static void HandleInventory(UpdateArgs args)
    {
        var previousInventory = args.currentState.RecentViews.Reverse().Skip(1).FirstOrDefault();
        // before any early return, so every exit path records it
        if (IsTransferView(args.msg.Chest) || IsTransferView(previousInventory))
            args.currentState.ExtractedInfo.LastTransferViewAt = args.msg.ReceivedAt;
        var currentIsRecipe = TryGetRecipe(args.msg.Chest, out _, out var currentIngredients);
        var previousIsRecipe = TryGetRecipe(previousInventory, out var craftedTag, out var craftIngredients);
        if (currentIsRecipe || previousIsRecipe)
        {
            // both recipe views alternate in production (Turbo Gourd <-> Enchanted Turbo Gourd) and the sack
            // message of the first arrives while the second is open, so remember the union
            args.currentState.ExtractedInfo.LastCraftViewAt = args.msg.ReceivedAt;
            args.currentState.ExtractedInfo.LastCraftIngredients = currentIngredients.Union(craftIngredients).ToList();
        }
        if (IsSafariInventorySwap(args))
        {
            RegisterKnownItemUuids(args.currentState, args.msg.Chest);
            return;
        }
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
        var (craftedOutput, craftConsumed) = DetectTableCraft(args, previousInventory, mapOfItems, currentInventory);
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
        var previousTags = GetItemsForSwapGuards(previousInventory).Where(i => i.Tag != null).Select(CountTag).ToHashSet();
        // (b) every tag in any other recent view (the current view is the last entry)
        var olderViewTags = new HashSet<string>();
        foreach (var view in args.currentState.RecentViews.Take(Math.Max(0, args.currentState.RecentViews.Count - 1)))
        {
            foreach (var item in GetItemsForSwapGuards(view))
                if (item.Tag != null)
                    olderViewTags.Add(CountTag(item));
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
                if (newCount != 0 && craftedOutput.TryGetValue(tag, out var craftedUuids))
                {
                    var ignored = Math.Min(newCount, craftedUuids);
                    LogIgnoredCraft(args, ignored, tag, previousInventory);
                    newCount -= ignored;
                }
                if (newCount != 0 && previousIsRecipe && tag == craftedTag)
                {
                    LogIgnoredCraft(args, newCount, tag, previousInventory);
                    continue;
                }
                if (newCount != 0)
                    Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, Tasks.PurchaseDiscount.AdjustIncrease(
                        args.currentState.ExtractedInfo, tag, newCount, args.msg.ReceivedAt));
                continue;
            }
            var previousCount = previousItems.Sum(i => (long)(i.Count ?? 0));
            var currentCount = currentItems.Sum(i => (long)(i.Count ?? 0));
            var diff = currentCount - previousCount;
            // crafting table: the output is no loot and the ingredients that stay partly are no loss
            if (diff > 0 && craftedOutput.TryGetValue(tag, out var craftedAmount))
            {
                var ignored = (int)Math.Min(diff, craftedAmount);
                LogIgnoredCraft(args, ignored, tag, previousInventory);
                diff -= ignored;
            }
            else if (diff < 0 && craftConsumed.TryGetValue(tag, out var consumedAmount))
                diff = Math.Min(0, diff + consumedAmount);
            // items shift-clicked into a storage/transfer view that is already the current view were
            // moved, not consumed (production: -64 GRIFFIN_FEATHER after an Ender Chest upload). In a
            // transfer view (sack, bazaar, auction, shop, trade, Hunting Box) they do not show up in the
            // container part, but leaving the inventory there is a deposit or sale all the same
            // (production: -115 MELON_BLOCK between "Emissary Sisko" and "Sack of Sacks", whose sack
            // message is ignored as a transfer - booking only the inventory side was a phantom loss).
            if (diff < 0 && (currentIsTransferView || movedIntoCurrentContainer.Contains(tag)))
                continue;
            // a shard is never consumed from the inventory: it leaves by being added to the Hunting Box
            // (right click), fused, sold or traded (production: -64 SHARD_APEX_DRAGON between two plain
            // inventory views). Gains stay - Kuudra chest shards arrive in the inventory.
            if (diff < 0 && tag.StartsWith("SHARD_", StringComparison.Ordinal))
                continue;
            // crafting through a recipe view converts items the player already had: the output is not
            // loot and the ingredients are not a loss (production: +15 ENCHANTED_COMPOST booked as 30M
            // coins after "Compost Bundle"; inputs left as -32x GLOOMGOURD etc in another period)
            if (previousIsRecipe && diff > 0 && tag == craftedTag)
            {
                LogIgnoredCraft(args, (int)diff, tag, previousInventory);
                continue;
            }
            if (previousIsRecipe && diff < 0 && craftIngredients.Contains(tag))
                continue;
            if (diff == 0)
                continue;
            // placed eyes are booked by BookPlacedSummoningEyes, only in the Dragon's Nest; anywhere else the
            // eyes were sold or stashed
            if (diff < 0 && tag == SummoningEyeTag)
                continue;
            // Captured shards go to the Hunting Box and are counted from chat; only a few arrive in the
            // inventory as loot (Kuudra chests). 64 or more at once come back from the bazaar, a trade or
            // the stash through a view that was not uploaded (production 2026-10-03, a shard flipper:
            // +699x SHARD_HIDEONWALL and +468x SHARD_HIDEONFLOOR of a cancelled sell order booked as 160M
            // coins of Critter Safari loot, where a run gives about 7). The log line stays to find the
            // view pairs behind it.
            if (diff >= BulkChangeLogThreshold && tag.StartsWith("SHARD_", StringComparison.Ordinal))
            {
                args.GetService<ILogger<CollectionListener>>().LogInformation(
                    "Bulk inventory change for {playerId}: {count}x {tag} between views {previousView} -> {currentView}",
                    args.currentState.PlayerId, diff, tag, previousInventory.Name ?? "<inventory>", args.msg.Chest.Name ?? "<inventory>");
                continue;
            }
            Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, tag, diff > 0
                ? Tasks.PurchaseDiscount.AdjustIncrease(args.currentState.ExtractedInfo, tag, (int)Math.Min(diff, int.MaxValue), args.msg.ReceivedAt)
                : diff);
        }
        BookPlacedSummoningEyes(args, previousInventory, mapOfItems, currentInventory);
        RegisterKnownItemUuids(args.currentState, args.msg.Chest);

        static Dictionary<string, List<Models.Item>> GetLookupItemsByTag(Models.ChestView? previousInventory)
        {
            var accessibleInventory = previousInventory.Items.Skip(AccessibleInventoryStart(previousInventory.Items)).Take(36).ToList();
            return accessibleInventory
                .Where(i => i.Tag != null && i.ItemName != null)
                .GroupBy(CountTag)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }

    internal const string SummoningEyeTag = "SUMMONING_EYE";
    private const string DragonZone = "Dragon's Nest";

    /// <summary>
    /// An eye placed on the dragon altar leaves the inventory, and when it was the last one the tag is gone from
    /// the current view, which the loop in <see cref="HandleInventory"/> never visits. Books the drop as a negative
    /// <see cref="SummoningEyeTag"/> count - the cost of the Ender Dragon fight - only in the Dragon's Nest and
    /// not around storage/transfer views or a stale location, where eyes that disappear were stashed or sold.
    /// </summary>
    private static void BookPlacedSummoningEyes(UpdateArgs args, Models.ChestView previousInventory,
        Dictionary<string, List<Models.Item>> previousItems, Dictionary<string, List<Models.Item>> currentItems)
    {
        var info = args.currentState.ExtractedInfo;
        if (Tasks.SkyblockZones.Canonical(info.CurrentLocation) != DragonZone)
            return;
        var current = args.msg.Chest;
        if (IsTransferView(current) || (current.Name != null && !StorageListener.IsNotStorage(current)) || IsLocationStale(info, DateTime.UtcNow))
            return;
        long Count(Dictionary<string, List<Models.Item>> items) =>
            items.TryGetValue(SummoningEyeTag, out var list) ? list.Sum(i => (long)(i.Count ?? 0)) : 0;
        var placed = Count(previousItems) - Count(currentItems);
        if (placed <= 0)
            return;
        Tasks.ItemCountMath.Add(args.currentState.ItemsCollectedRecently, SummoningEyeTag, -(int)Math.Min(placed, int.MaxValue));
        args.GetService<ILogger<CollectionListener>>().LogInformation(
            "Placed {count} Summoning Eye(s) for {playerId}", placed, args.currentState.PlayerId);
    }

    /// <summary>
    /// The Critter Safari shows another (production: an empty) inventory and the real one returns on exit, which
    /// the wholesale swap check misses because it needs a non-empty current inventory: the whole inventory was
    /// booked as gains at "Critter Safari Entrance" after each run. Uploads inside the safari are never loot (its
    /// loot comes from chat), and the first one after leaving only re-baselines. Same effect as the Rift swap.
    /// </summary>
    private static bool IsSafariInventorySwap(UpdateArgs args)
    {
        var info = args.currentState.ExtractedInfo;
        if (Tasks.SkyblockZones.Canonical(info.CurrentLocation) == "Critter Safari")
        {
            info.SafariInventorySeen = true;
            return true;
        }
        if (!info.SafariInventorySeen)
            return false;
        info.SafariInventorySeen = false;
        args.GetService<ILogger<CollectionListener>>().LogInformation(
            "Inventory after leaving the Critter Safari for {playerId} at {location} is a swap, not booking it",
            args.currentState.PlayerId, info.CurrentLocation);
        return true;
    }

    /// <summary>
    /// A bazaar product page (see <see cref="MenuSamplerListener.ClassifyByContent"/>) names the item about to be bought: the product
    /// item sits in its menu part (slot 13, the first tagged item otherwise). <see cref="Tasks.PurchaseDiscount"/> keeps increases of
    /// it out of the drops for a short while.
    /// </summary>
    private static void ArmBazaarProductGuard(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest?.Items == null || MenuSamplerListener.ClassifyByContent(chest) != MenuSamplerListener.BazaarProductName)
            return;
        var menu = chest.Items.Take(AccessibleInventoryStart(chest.Items)).ToList();
        var product = (menu.Count > 13 && menu[13]?.Tag != null ? menu[13] : null) ?? menu.FirstOrDefault(i => i?.Tag != null);
        if (product?.Tag != null)
            Tasks.PurchaseDiscount.ArmProductGuard(args.currentState.ExtractedInfo, product.Tag, args.msg.ReceivedAt);
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
        foreach (var item in view.Items.Take(AccessibleInventoryStart(view.Items)))
            if (item.Tag != null)
                tags.Add(CountTag(item));
        return tags;
    }

    /// <summary>
    /// The tag an inventory item is counted (and priced) under: a book with exactly one enchantment is the bazaar
    /// product <c>ENCHANTMENT_&lt;ENCHANT&gt;_&lt;LEVEL&gt;</c> ("ultimate_wise" level 2 -> ENCHANTMENT_ULTIMATE_WISE_2), the same
    /// mapping Coflnet.Sky.Core applies to auctions. Books with none or several enchantments (and every other item)
    /// keep their tag, <c>ENCHANTED_BOOK</c> has no price of its own.
    /// </summary>
    internal static string CountTag(Models.Item item)
    {
        if (item.Tag == "ENCHANTED_BOOK" && item.Enchantments is { Count: 1 })
        {
            var (enchant, level) = item.Enchantments.First();
            if (level > 0 && !string.IsNullOrWhiteSpace(enchant))
                return $"ENCHANTMENT_{enchant.ToUpperInvariant()}_{level}";
        }
        return item.Tag;
    }

    /// <summary>Slots of a bare inventory upload from Minecraft 1.21: 5 crafting, 4 armor, 36 inventory, 1 offhand.</summary>
    private const int BareInventoryWithOffhandSlots = 46;

    /// <summary>
    /// Index of the first of the 36 accessible inventory slots in a view's items. They are the last 36
    /// slots of every chest view and of the 1.8 bare inventory, but the 1.21 bare inventory ("Crafting")
    /// has the offhand slot after them. Taking the last 36 there dropped the top left inventory slot,
    /// so a stack in it vanished in every bare inventory view and came back as a gain in the next chest
    /// view (production 2026-10-02: 12x "+64 SHARD_FLAMING_SPIDER" between "Crafting" and the bazaar).
    /// </summary>
    internal static int AccessibleInventoryStart(IReadOnlyCollection<Models.Item> items)
    {
        var trailing = items.Count == BareInventoryWithOffhandSlots ? 1 : 0;
        return Math.Max(0, items.Count - 36 - trailing);
    }

    /// <summary>
    /// Identities of the distinct items in the accessible inventory part (same 36-slot slicing as
    /// the local GetLookupItemsByTag in <see cref="HandleInventory"/>): the uuid hash for non-stackable
    /// items, otherwise the tag. SKYBLOCK_MENU (in every inventory) and tagless items are ignored.
    /// </summary>
    internal static HashSet<string> GetAccessibleIdentities(Models.ChestView view)
    {
        var accessible = view.Items.Skip(AccessibleInventoryStart(view.Items)).Take(36);
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
    /// (name starts with "You    "), sacks, NPC shops, the Hunting Box (production: 64 shards
    /// withdrawn from it counted as a gain, the bazaar sale afterwards was skipped), the Fusion
    /// Box, the Gemstone Grinder (gems move between the player's gear and the inventory; production
    /// 2026-10-03: 683M coins of removed gems booked as loot in 11 hours) and the "Storage" overview
    /// (the backpack opened from it is not always uploaded; production 2026-10-03: +150x SHARD_FUNGLOOM
    /// and +160x GRIFFIN_FEATHER between "Storage" and the next storage page). First-time counting is
    /// skipped around them.
    /// </summary>
    private static bool IsSwapView(Models.ChestView view)
    {
        var name = view?.Name;
        if (name == null)
            return false;
        return name.StartsWith("You    ") || name.Contains("Sack") || name.Contains("Shop") || name.Contains("Trades")
            || name.Contains("Hunting Box") || name.Contains("Fusion Box") || name is "Gemstone Grinder" or "Storage";
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
        var previousServer = args.currentState.ExtractedInfo.CurrentServer;
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
        // Players requeue straight from one dungeon/Kuudra run into the next without leaving the zone: inside an
        // instanced zone a new server id (a tick without one never counts) with the same location is the end
        // of one run and the start of the next.
        var newInstance = IsNewInstanceInSameZone(previousLocation, currentLocation, previousServer, server);
        if (!zoneChanged && !newInstance)
        {
            // Reconfirm we're still in the same zone as of this scoreboard update - bumped on
            // every tick (not just a flush) so a live classification in between flushes always
            // sees a fresh CurrentLocationSeenAt (see TaskClassifier.Classify's tab-fallback gate).
            args.currentState.ExtractedInfo.CurrentLocationSeenAt = now;
        }
        if (zoneChanged || newInstance
            // if the same location is used, attempt to store it for people staying in same location
            || args.currentState.ExtractedInfo.LastLocationChange < now.AddMinutes(-5))
        {
            // Flush/classify the OLD fragment using the OLD zone's CurrentLocationSince/SeenAt
            // (for the same-zone path these were just bumped above; for a real zone change they
            // still describe the zone that just ended - only updated below, AFTER this flush).
            await StoreLocationProfit(args, previousLocation);
        }
        if (zoneChanged || newInstance)
            await RecordFinishedRun(args, previousLocation);
        args.currentState.ExtractedInfo.CurrentLocation = currentLocation;
        if (zoneChanged || newInstance)
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
    /// True when the player stays in the same dungeon floor / Kuudra tier zone but the server id changed, i.e. one run
    /// ended and the next was queued directly. Missing server ids never count as a change.
    /// </summary>
    internal static bool IsNewInstanceInSameZone(string previousLocation, string currentLocation, string previousServer, string server)
    {
        if (previousLocation == null || previousLocation != currentLocation
            || string.IsNullOrEmpty(previousServer) || string.IsNullOrEmpty(server) || previousServer == server)
            return false;
        return Tasks.DungeonRewardAttribution.IsFloorZone(currentLocation) || Tasks.KuudraRewardAttribution.IsTierZone(currentLocation);
    }

    /// <summary>
    /// Records the length of a finished dungeon floor run: from entering the floor zone to leaving it (last scoreboard
    /// tick inside, so a gap in updates is not counted). Uses CurrentLocationSince, which only moves on a real zone change,
    /// so the 5 minute same-zone period flush never splits a run. Must be called before CurrentLocationSince is reset.
    /// Implausible values are dropped by the recorder (<see cref="RunLengthBounds.Dungeon"/>).
    /// </summary>
    private async Task RecordFinishedRun(UpdateArgs args, string previousLocation)
    {
        var info = args.currentState.ExtractedInfo;
        var key = RunLengthKeys.ForDungeonZone(previousLocation);
        var bounds = RunLengthBounds.Dungeon;
        bool completed;
        if (key != null)
            completed = IsCompletedRun(info);
        else if ((key = RunLengthKeys.ForKuudraZone(previousLocation)) != null)
        {
            bounds = RunLengthBounds.Kuudra;
            completed = IsCompletedKuudraRun(info);
        }
        else
            return;
        var length = info.CurrentLocationSeenAt - info.CurrentLocationSince;
        // only completed runs: abandoned and failed ones (p25 of F7 was 135 s) would drag the median down. The
        // completion line can still arrive just after the zone change, so the stay is kept for a moment.
        if (!completed)
        {
            RunLengthCredit.Remember(info, key, length, DateTime.UtcNow);
            return;
        }
        info.UnrecordedRunKey = null;
        try
        {
            await args.GetService<IRunLengthRecorder>().Record(key, length, bounds);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Could not record run length {key}", key);
        }
    }

    /// <summary>The results header of the dungeon (<see cref="Models.ExtractedInfo.LastDungeonRunCompletedAt"/>) was seen during the stay that is ending.</summary>
    internal static bool IsCompletedRun(Models.ExtractedInfo info)
        => info.LastDungeonRunCompletedAt != default && info.LastDungeonRunCompletedAt >= info.CurrentLocationSince;

    /// <summary>"KUUDRA DOWN!" (<see cref="Models.ExtractedInfo.LastKuudraRunCompletedAt"/>) was seen during the stay that is ending.</summary>
    internal static bool IsCompletedKuudraRun(Models.ExtractedInfo info)
        => info.LastKuudraRunCompletedAt != default && info.LastKuudraRunCompletedAt >= info.CurrentLocationSince;

    /// <summary>
    /// Kuudra's end-of-run banner (centered, colour coded): "KUUDRA DOWN!" for a win, "DEFEAT" for a loss.
    /// Returns null for any other line, true for a win, false for a defeat.
    /// </summary>
    internal static bool? ParseKuudraEnd(string? chatLine)
    {
        if (string.IsNullOrWhiteSpace(chatLine))
            return null;
        var line = StripFormatting(chatLine).Trim();
        if (line == "KUUDRA DOWN!")
            return true;
        return line == "DEFEAT" ? false : null;
    }

    private async Task HandleKuudraEnd(UpdateArgs args)
    {
        var info = args.currentState.ExtractedInfo;
        var now = DateTime.UtcNow;
        var inTier = Tasks.KuudraRewardAttribution.IsTierZone(info.CurrentLocation);
        // a win while still in the tier zone is picked up when the zone is left; one that arrives after the
        // scoreboard moved on credits the stay that just ended
        if (inTier && RunLengthCredit.BelongsToPreviousRun(info, "kuudra:", now))
            await RunLengthCredit.TryCredit(args, "kuudra:", now, Logger);
        else if (inTier)
            info.LastKuudraRunCompletedAt = now;
        else
            await RunLengthCredit.TryCredit(args, "kuudra:", now, Logger);
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

    private static readonly Regex CroesusKuudraTitleRegex = new(@"^Kuudra - (Basic|Hot|Burning|Fiery|Infernal)$", RegexOptions.Compiled);
    private static readonly string[] KuudraTierNames = ["Basic", "Hot", "Burning", "Fiery", "Infernal"];

    /// <summary>
    /// Parses a Croesus Kuudra chest title ("Kuudra - Basic".."Kuudra - Infernal", Basic = T1 .. Infernal = T5) into the
    /// tier zone string in <see cref="Models.ExtractedInfo.LastKuudraTier"/> format ("Kuudra's Hollow (T5)"), or null.
    /// </summary>
    internal static string TryParseCroesusKuudraTier(string chestName)
    {
        if (string.IsNullOrEmpty(chestName))
            return null;
        var match = CroesusKuudraTitleRegex.Match(Tasks.SkyblockZones.Canonical(chestName));
        if (!match.Success)
            return null;
        return Tasks.SkyblockZones.Canonical(Tasks.KuudraRewardAttribution.TierZone(Array.IndexOf(KuudraTierNames, match.Groups[1].Value) + 1));
    }

    private static void HandleCroesusKuudraChest(UpdateArgs args)
    {
        var tier = TryParseCroesusKuudraTier(args.msg.Chest?.Name);
        if (tier == null)
            return;
        var info = args.currentState.ExtractedInfo;
        var stamp = DateTime.UtcNow;
        // like the floor reading: the claim period is the one the player is in right now, so stamp it with that
        // period's start (KuudraRewardAttribution.ResolveLocation measures the delay from there)
        if (Tasks.SkyblockZones.Canonical(info.CurrentLocation) == "Dungeon Hub"
            && info.LastLocationChange != default && info.LastLocationChange < stamp)
            stamp = info.LastLocationChange;
        info.LastKuudraTier = tier;
        info.LastKuudraTierAt = stamp;
    }

    private static void HandleCroesusChest(UpdateArgs args)
    {
        HandleCroesusKuudraChest(args);
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

    /// <summary>The default of <see cref="Models.ExtractedInfo.CurrentLocation"/>, absorbed by the hidden UnknownLocationTask.</summary>
    internal const string UnknownLocation = "Unknown";
    /// <summary>
    /// The scoreboard ends a period at least every 5 minutes; the longest real ones (dungeon runs) take
    /// about 30 minutes.
    /// </summary>
    internal static readonly TimeSpan StaleLocationAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the scoreboard zone can no longer say where the pending items were collected: the period
    /// runs for more than <see cref="StaleLocationAfter"/>, or no scoreboard confirmed the zone for that
    /// long (players who upload inventories but no scoreboard, whose periods only end through
    /// <see cref="StartNewPeriodAfterLargeChange"/>). Production 2026-10-03: 0.8% of the periods lasted
    /// more than an hour, up to 35 days, and held more than half of all booked value - the catch-up of
    /// everything that changed since the player was last seen, booked to the zone of back then
    /// (12.6B "Melon Farming" at "The Garden", 6.3B "Diamond Mining" at "Glacite Tunnels").
    /// </summary>
    internal static bool IsLocationStale(Models.ExtractedInfo info, DateTime now)
    {
        return now - info.LastLocationChange > StaleLocationAfter || now - info.CurrentLocationSeenAt > StaleLocationAfter;
    }

    /// <summary>
    /// End of the period being flushed at <paramref name="now"/>. Normally now; when the last scoreboard
    /// update that confirmed the zone is more than <see cref="StaleLocationAfter"/> ago the player was
    /// offline in between (the flush only happens at the next login), and the period ends at that last
    /// confirmation instead of spanning the offline gap (production 2026-10: "Unknown" chest-claim periods
    /// of 16 hours that made "Kuudra Chest Claims" show 1708 tracked hours).
    /// </summary>
    internal static DateTime PeriodEnd(Models.ExtractedInfo info, DateTime now)
    {
        if (now - info.CurrentLocationSeenAt <= StaleLocationAfter)
            return now;
        var end = info.CurrentLocationSeenAt;
        return end < info.LastLocationChange ? info.LastLocationChange : end;
    }

    private async Task StoreLocationProfit(UpdateArgs args, string previousLocation)
    {
        var now = DateTime.UtcNow;
        var periodEnd = PeriodEnd(args.currentState.ExtractedInfo, now);
        var profit = 0L;
        var collected = args.currentState.ItemsCollectedRecently;
        // an item that came and went again (FISH_BAIT:0) is no collection: it must neither store a period
        // nor make an empty Kuudra run look like one with loot (its time is handed to the claim period)
        foreach (var zero in collected.Where(c => c.Value == 0).Select(c => c.Key).ToList())
            collected.Remove(zero);
        var info = args.currentState.ExtractedInfo;
        var periodStart = info.LastLocationChange;
        if (IsLocationStale(info, now) && previousLocation != UnknownLocation)
        {
            if (collected.Count > 0)
                Logger.LogInformation("Location {location} of {playerId} is stale (period since {periodStart}, last scoreboard {seenAt}), storing {count} item types at Unknown",
                    previousLocation, args.currentState.PlayerId, periodStart, info.CurrentLocationSeenAt, collected.Count);
            previousLocation = UnknownLocation;
        }
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
        string lateRewardTask = null;
        TrackedProfitService.Period lateRewardPeriod = null;
        if (collected.Count > 0)
        {
            cleanPrices = await GetCleanPrices(args);

            profit = ComputeProfit(collected, cleanPrices);
            var period = new TrackedProfitService.Period()
            {
                EndTime = periodEnd,
                StartTime = periodStart - claimShift,
                Location = resolvedLocation,
                PlayerUuid = args.currentState.McInfo.Uuid.ToString("N"),
                Server = args.currentState.ExtractedInfo.CurrentServer,
                ItemsCollected = new Dictionary<string, int>(args.currentState.ItemsCollectedRecently),
                Profit = profit
            };
            ClassifyPeriod(args, period, cleanPrices);
            lateRewardTask = AttributeLateReward(args, period);
            lateRewardPeriod = lateRewardTask == null ? null : period;
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
        await AccumulateSession(args, resolvedLocation, collected, cleanPrices, now, periodEnd, lateRewardPeriod);
        args.currentState.ItemsCollectedRecently.Clear();
        args.currentState.ExtractedInfo.LastLocationChange = now;
        args.currentState.ExtractedInfo.PeriodSplitPending = false;
    }

    /// <summary>
    /// Merge the just-flushed location fragment into the running task session and fold
    /// the session when a boundary is crossed. Runs on every flush (including empty idle
    /// ticks) so a task spanning multiple locations accumulates into one session instead
    /// of being chopped below the classifier's minimum window. Failures only lose the
    /// contribution, never the raw period.
    /// </summary>
    private async Task AccumulateSession(UpdateArgs args, string previousLocation,
        Dictionary<string, int> collected, Dictionary<string, double> cleanPrices, DateTime now, DateTime periodEnd,
        TrackedProfitService.Period lateRewardPeriod = null)
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
            if (lateRewardPeriod != null && state.ExtractedInfo.CurrentSession == null)
            {
                // a reward-only hand-in (Galatea coupons) after the work that earned it, whose session already
                // ended: value for the earning task, no session of its own and none of its hand-in seconds
                PeriodClassifiedCounter.WithLabels(lateRewardPeriod.DetectedTask).Inc();
                await args.GetService<Tasks.TaskPeriodFolder>().FoldLateReward(lateRewardPeriod, state, cleanPrices ?? await GetCleanPrices(args));
                return;
            }
            var fragment = new TrackedProfitService.Period()
            {
                EndTime = periodEnd,
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

    internal static Tasks.ProfitTask FindIslandEarner(Tasks.TaskRegistry registry, TrackedProfitService.Period period)
    {
        // a hand-in is not work, and a period nothing was collected in cannot reach here
        return registry.Tasks.OfType<Tasks.IslandTask>()
            .Where(t => t.LateRewardDeclaration != null && !t.IsLateRewardOnly(period) && t.CoversLocation(period.Location))
            .OrderBy(t => t.LocationCount) // the most specific tracker (fishing) before the wider one (diving)
            .FirstOrDefault();
    }

    /// <summary>
    /// Attributes a flushed period to a task. Failures only lose the attribution,
    /// never the period itself.
    /// </summary>
    /// <summary>
    /// Late reward bookkeeping for a just classified period (see <see cref="Tasks.MethodTask.LateReward"/>):
    /// remembers the last period of a task that declares one, and credits a reward-only period (Galatea
    /// contest coupons) following it to that task by setting its DetectedTask. Returns the credited task's
    /// name when it was reattributed, otherwise null.
    /// </summary>
    private string AttributeLateReward(UpdateArgs args, TrackedProfitService.Period period)
    {
        try
        {
            var info = args.currentState.ExtractedInfo;
            var registry = args.GetService<Tasks.TaskRegistry>();
            var classified = period.DetectedTask == null ? null : registry.GetByName(period.DetectedTask);
            // island trackers (Galatea fishing/diving) are never a period's DetectedTask: the work is theirs by location
            var workedFor = classified?.LateRewardDeclaration != null ? classified : FindIslandEarner(registry, period);
            if (workedFor != null)
            {
                if (!workedFor.IsAttributedLateReward(period))
                {
                    info.LastLateRewardTask = workedFor.RegistryName;
                    info.LastLateRewardTaskAt = period.EndTime;
                    info.LastLateRewardTaskLocation = period.Location;
                }
                return null;
            }
            var earner = info.LastLateRewardTask == null ? null : registry.GetByName(info.LastLateRewardTask);
            var credited = Tasks.LateRewardAttribution.Resolve(earner, info.LastLateRewardTaskAt, info.LastLateRewardTaskLocation,
                period.Location, period.ItemsCollected, period.StartTime);
            if (credited == null)
                return null;
            period.DetectedTask = credited.RegistryName;
            Logger.LogInformation("Attributed late reward of {playerId} at {location} to {task}", args.currentState.PlayerId, period.Location, period.DetectedTask);
            return period.DetectedTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to attribute late reward at {location}", period.Location);
            return null;
        }
    }

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
                info.CurrentIsland, info.CurrentIslandAt, info.CurrentLocationSince, info.CurrentLocationSeenAt,
                allowShortInstanceWindow: true, allowShortItemMatch: true);
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
