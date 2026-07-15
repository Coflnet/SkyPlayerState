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
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Tab)
        {
            HandleTab(args);
        }
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.CHAT)
        {
            // stash messages
            foreach (var uploadedLine in args.msg.ChatBatch)
            {
                if (uploadedLine.StartsWith("You caught"))
                    await HandleShardCatch(args, uploadedLine);
                if (uploadedLine.StartsWith("Added items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.StartsWith("Removed items:"))
                    await HandleSackNotification(args, uploadedLine);
                if (uploadedLine.Contains("Chameleon (0."))
                    args.currentState.ItemsCollectedRecently["SHARD_CHAMELEON"] = args.currentState.ItemsCollectedRecently.GetValueOrDefault("SHARD_CHAMELEON", 0) + 1;
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
            args.currentState.ItemsCollectedRecently[MithrilPowderTag] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(MithrilPowderTag, 0) + diff;
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

    private async Task HandleShardCatch(UpdateArgs args, string uploadedLine)
    {
        // eg "You caught a Verdant Shard!" "You caught x2 Birries Shards!" "LOOT SHARE You received a Chill Shard for assisting Oden."
        var match = Regex.Match(uploadedLine, @"You (caught|received) (a|x\d) (.*) Shards?(!| for)");
        if (!match.Success)
        {
            Logger.LogDebug("Failed to match shard catch: {line}", uploadedLine);
            return;
        }
        var shardName = match.Groups[3].Value.Trim();
        var count = match.Groups[2].Value.StartsWith("x") ? int.Parse(match.Groups[2].Value.Substring(1)) : 1;
        // The mob display name does not always match its shard tag stem (e.g. "Lotusfish" -> LOTUS_FISH,
        // "Cinderbat" -> CINDER_BAT, "Bogged" -> SEA_ARCHER). Prefer the canonical map, fall back to the
        // naive derivation so unknown/new shards are still recorded rather than dropped.
        var tag = Constants.ShardNames.TryGetValue(shardName, out var mapped)
            ? "SHARD_" + mapped.ToUpperInvariant()
            : "SHARD_" + shardName.ToUpperInvariant().Replace(" ", "_");
        args.currentState.ItemsCollectedRecently[tag] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(tag, 0) + count;
    }

    private async Task HandleSackNotification(UpdateArgs args, string uploadedLine)
    {
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
                    args.currentState.ItemsCollectedRecently[tag] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(tag, 0) + count;
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

    static void HandleInventory(UpdateArgs args)
    {
        var previousInventory = args.currentState.RecentViews.Reverse().Skip(1).FirstOrDefault();
        if (previousInventory == null)
            return;
        Dictionary<string, int?> mapOfItems = GetLookupItemCount(previousInventory);
        try
        {
            if (previousInventory.Name != null && (!StorageListener.IsNotStorage(previousInventory)
                || IsBazaarOrderCreate(previousInventory) || IsBazaarWindow(previousInventory)
                || previousInventory.Name == "Create BIN Auction"))
            {
                // if the previous inventory is a storage, we don't want to track items collected
                args.GetService<ILogger<CollectionListener>>().LogDebug("Skipping item collection tracking for storage chest {chestName} for player {playerId}", previousInventory.Name, args.currentState.PlayerId);
                return;
            }
        }
        catch (System.Exception e)
        {
            args.GetService<ILogger<CollectionListener>>().LogError(e, "Failed to handle inventory for player {PlayerId} {previousInventory}", args.currentState.PlayerId, JsonConvert.SerializeObject(previousInventory));
        }
        var currentInventory = GetLookupItemCount(args.msg.Chest);
        foreach (var item in currentInventory)
        {
            if (item.Value == null)
                continue;
            if (mapOfItems.TryGetValue(item.Key, out var previousCount))
            {
                if (previousCount == null)
                    continue;
                var diff = item.Value - previousCount;
                if (diff != 0)
                {
                    args.currentState.ItemsCollectedRecently[item.Key] = args.currentState.ItemsCollectedRecently.GetValueOrDefault(item.Key, 0) + (int)diff;
                }
            }
        }
        static Dictionary<string, int?> GetLookupItemCount(Models.ChestView? previousInventory)
        {
            // skip more than the 4 lines above and maybe 1 offhand slot
            var accessibleInventory = previousInventory.Items.Skip(previousInventory.Items.Count - 36 / 9 * 9).Take(36).ToList();
            var mapOfItems = accessibleInventory
                .Where(i => i.Tag != null && i.ItemName != null)
                .GroupBy(i => i.Tag)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
            return mapOfItems;
        }
    }

    private static bool IsBazaarWindow(Models.ChestView previousInventory)
    {
        // maybe also check if previous Item was actually present
        return previousInventory.Name.EndsWith("Bazaar Orders")
            || previousInventory.Name.Contains('➜'); // for insta sells
    }

    private static bool IsBazaarOrderCreate(Models.ChestView previousInventory)
    {
        return previousInventory.Name.Contains("Confirm");
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
                    cleanPrices[item.ProductId] = (int)item.SellPrice;
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
        var server = args.msg.Scoreboard?.FirstOrDefault(s => s.StartsWith(currentDate) || s.StartsWith(yesterdayDate))?.Split(' ')[1];
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
        var previousLocation = args.currentState.ExtractedInfo.CurrentLocation;
        if (previousLocation != null && previousLocation != currentLocation
            // if the same location is used, attempt to store it for people staying in same location
            || args.currentState.ExtractedInfo.LastLocationChange < DateTime.UtcNow.AddMinutes(-5))
        {
            await StoreLocationProfit(args, previousLocation);
        }
        args.currentState.ExtractedInfo.CurrentLocation = currentLocation;
        await ClassifyLive(args);
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
            var classification = args.GetService<Tasks.TaskClassifier>().Classify(
                state.ExtractedInfo.CurrentLocation, state.ItemsCollectedRecently, minutes, claim, cachedCleanPrices);
            var previous = state.ExtractedInfo.CurrentTask;
            state.ExtractedInfo.CurrentTask = classification?.TaskName;
            if (classification?.TaskName != previous)
                state.ExtractedInfo.CurrentTaskSince = DateTime.UtcNow;
            if (classification != null && state.McInfo.Uuid != default)
                await args.GetService<Tasks.TaskActivityService>().MarkDoing(classification.TaskName, state.McInfo.Uuid.ToString("N"));
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
        Dictionary<string, double> cleanPrices = null;
        if (collected.Count > 0)
        {
            cleanPrices = await GetCleanPrices(args);

            profit = (long)collected.Select(c =>
            {
                var price = cleanPrices.GetValueOrDefault(c.Key);
                return price * c.Value;
            }).Sum();
            var period = new TrackedProfitService.Period()
            {
                EndTime = now,
                StartTime = args.currentState.ExtractedInfo.LastLocationChange,
                Location = previousLocation,
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
            args.SendDebugMessage("You collected a total of " + profit + " coins worth of items in " + previousLocation + " " + string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}")));
            Logger.LogInformation("Profit summary for {playerId} at {location}: {profit} coins from {items}", args.currentState.PlayerId, previousLocation, profit, string.Join(", ", collected.Select(c => $"{c.Value}x {c.Key}")));
        }
        // fold task attribution per session (spanning locations), not per location fragment
        await AccumulateSession(args, previousLocation, collected, cleanPrices, now);
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
            var classification = args.GetService<Tasks.TaskClassifier>().Classify(
                period.Location, period.ItemsCollected,
                (period.EndTime - period.StartTime).TotalMinutes, null, cleanPrices);
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
