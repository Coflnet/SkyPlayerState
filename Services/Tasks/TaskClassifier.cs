using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

// DetectionSignature lives in DetectionSignature.cs, since MethodTask.GetDetectionSignature()
// constructs it.

/// <summary>
/// Result of classifying a collection window to a task.
/// </summary>
public record Classification(string TaskName, bool ItemMatched, string Category);

/// <summary>
/// Attributes what a player is doing to a task based on the area they are in
/// and the items they collected. Applies the exact matching rules of
/// MethodTask.FindMatchingPeriods plus deterministic tie breaking.
/// </summary>
public class TaskClassifier
{
    /// <summary>Shortest window <c>allowShortItemMatch</c> classifies; shorter ones are zone transits whose items say little about where they came from.</summary>
    public const double ShortItemMatchMinMinutes = 0.5;

    private readonly List<DetectionSignature> signatures;
    private readonly Dictionary<string, List<string>> derivedByPrimary;

    public TaskClassifier(TaskRegistry registry)
    {
        var allSignatures = registry.MethodTasks.Select(t => t.GetDetectionSignature()).ToList();
        signatures = allSignatures
            // derived tasks (e.g. "Sludge Mining (Gem Mixture)") share their primary's detection
            // and never compete for classification themselves - see GetDerivedTaskNames.
            .Where(s => s.DerivedFrom == null)
            // a task with neither locations nor detection items (e.g. passive trap tasks)
            // provides no evidence to match on and would classify as anything anywhere
            .Where(s => s.Locations.Count > 0 || s.DetectionItems.Count > 0)
            .ToList();
        derivedByPrimary = allSignatures
            .Where(s => s.DerivedFrom != null)
            .GroupBy(s => s.DerivedFrom, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.MethodName).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Task names that share/derive their detection from <paramref name="primaryTaskName"/> (see
    /// <see cref="DetectionSignature.DerivedFrom"/>). These never win classification themselves but
    /// should be treated as "also being done" whenever the primary is classified.
    /// </summary>
    public IReadOnlyList<string> GetDerivedTaskNames(string primaryTaskName) =>
        primaryTaskName != null && derivedByPrimary.TryGetValue(primaryTaskName, out var names)
            ? names
            : [];

    /// <summary>
    /// Classify a collection window.
    /// Returns null when the signal is too weak (less than 3 minutes, or a
    /// location-only match with fewer items collected than that task's own
    /// <see cref="DetectionSignature.MinLocationOnlyItems"/>, 5 by default).
    /// </summary>
    /// <param name="allowShortInstanceWindow">
    /// Classify windows under 3 minutes too, but only for dedicated-instance signatures
    /// (<see cref="DetectionSignature.MinLocationOnlyItems"/> &lt;= 1). Used for the raw per period column, where
    /// the seconds long chest claim of a run would otherwise be left without a task.
    /// </param>
    /// <param name="allowShortItemMatch">
    /// Per period column only: a window under 3 minutes (but at least <see cref="ShortItemMatchMinMinutes"/>) may also be
    /// attributed to a signature that is not a dedicated instance, when the window holds one of its DetectionItems and at
    /// least its <see cref="DetectionSignature.MinLocationOnlyItems"/> items in total. Players of an activity that flips between
    /// zones (Blaze Slayer between Smoldering Tomb and The Wasteland) end up with many 1-3 minute periods, which the 3 minute
    /// rule left without a task even though they hold the boss exclusive drops.
    /// </param>
    /// <param name="location">the area name the items were collected in</param>
    /// <param name="itemsCollected">tag to count of items collected in the window</param>
    /// <param name="minutes">length of the window in minutes</param>
    /// <param name="claimedTask">task the player manually claimed, wins any tie it matches</param>
    /// <param name="prices">coin value lookup for tie breaking, may be null</param>
    /// <param name="currentIsland">
    /// island reported by the tab list (see TabListUpdate), if known. Resolves Locations matches
    /// for zones the static SkyblockZones map leaves unmapped/ambiguous (e.g. "Dragon's Lair") -
    /// but never overrides a zone the map already resolves to some island (even one that doesn't
    /// match this signature), and only when it is not stale - see <paramref name="currentIslandAt"/>.
    /// </param>
    /// <param name="currentIslandAt">
    /// When <paramref name="currentIsland"/> was last set (<c>ExtractedInfo.CurrentIslandAt</c>).
    /// The tab list is sent far less often than the scoreboard, so a stale <paramref
    /// name="currentIsland"/> (from a previous zone) must not be used to match periods recorded
    /// after the player already moved on, or before they even arrived - see
    /// <paramref name="currentLocationSince"/>/<paramref name="currentLocationSeenAt"/>.
    /// </param>
    /// <param name="currentLocationSince">
    /// When the player's current zone actually started (<c>ExtractedInfo.CurrentLocationSince</c>) -
    /// <paramref name="currentIsland"/> is only trusted when it was received at or after this time
    /// (a tab reading from before the player arrived must not apply to this zone).
    /// </param>
    /// <param name="currentLocationSeenAt">
    /// Time of the last scoreboard update that confirmed the player is still in the current zone
    /// (<c>ExtractedInfo.CurrentLocationSeenAt</c>) - <paramref name="currentIsland"/> is only
    /// trusted when it was received at or before this time (a tab reading must be confirmed by a
    /// later scoreboard update of the same zone, not just outrun a stale one).
    /// </param>
    public Classification Classify(string location, Dictionary<string, int> itemsCollected, double minutes,
        string claimedTask = null, Dictionary<string, double> prices = null, string currentIsland = null,
        DateTime? currentIslandAt = null, DateTime? currentLocationSince = null, DateTime? currentLocationSeenAt = null,
        bool allowShortInstanceWindow = false, bool allowShortItemMatch = false)
    {
        // a zero count (an item that came and went again) is no signal, whatever the window
        if ((minutes < 3 && !allowShortInstanceWindow) || itemsCollected == null || itemsCollected.Values.All(v => v == 0))
            return null;
        // coins spent on a purchase are not activity (their count is coins, not items) - see PseudoItems.IsPurchase
        var activityItems = itemsCollected.Where(kv => !PseudoItems.IsPurchase(kv.Key)).ToList();
        var totalItems = activityItems.Where(kv => kv.Value > 0).Sum(kv => (long)kv.Value);
        // dedicated-instance signatures (MinLocationOnlyItems <= 1) also count consumption/cost - see below
        var totalActivity = activityItems.Where(kv => kv.Value != 0).Sum(kv => Math.Abs((long)kv.Value));
        var hasShard = itemsCollected.Keys.Any(k => k.StartsWith("SHARD_"));
        // Only a currentIsland that is both needed (the static map has no answer for this exact
        // zone - never overrides a zone the map DOES resolve, even to a non-matching island) and
        // fresh (received while the player was actually in the current zone: at/after it started,
        // and confirmed by a scoreboard update at/after the tab reading) may be used as a fallback.
        var currentIslandIsFresh = currentIsland != null && currentIslandAt.HasValue
            && currentLocationSince.HasValue && currentLocationSeenAt.HasValue
            && currentIslandAt.Value >= currentLocationSince.Value
            && currentIslandAt.Value <= currentLocationSeenAt.Value;
        var candidates = new List<(DetectionSignature sig, bool itemMatched, double matchedValue)>();
        foreach (var sig in signatures)
        {
            // a short window is only trusted for dedicated instances (a floor's chest claim takes seconds)
            var shortItemMatchOnly = minutes < 3 && sig.MinLocationOnlyItems > 1;
            if (shortItemMatchOnly && !(allowShortItemMatch && minutes >= ShortItemMatchMinMinutes))
                continue;
            if (sig.Locations.Count > 0)
            {
                // the zone map resolves most island-level Locations against the sub-zone the
                // scoreboard actually reports; a fresh tab-reported island additionally resolves
                // zones the static map leaves ambiguous/unmapped (e.g. "Dragon's Lair").
                var locationMatches = SkyblockZones.Matches(sig.Locations, location)
                    || (currentIslandIsFresh && SkyblockZones.IslandOf(location) == null
                        && sig.Locations.Contains(currentIsland));
                if (!locationMatches)
                    continue;
            }
            if (sig.IsExcludedAt(location, itemsCollected.Where(kv => kv.Value > 0).Select(kv => kv.Key)))
                continue;
            List<string> matched = null;
            var detectionItems = sig.DetectionItemsAt(location);
            if (detectionItems.Count > 0)
            {
                matched = itemsCollected.Keys.Where(k => detectionItems.Contains(k)).ToList();
                // a cost (negative count) of an evidence item stands in for a detection item: a period of only
                // consumed items (eyes placed on the dragon altar) still belongs to the task that consumes them
                if (matched.Count == 0 && !(sig.EvidenceItems is { Count: > 0 }
                    && itemsCollected.Any(kv => kv.Value < 0 && sig.EvidenceItems.Contains(kv.Key))))
                    continue;
            }
            // a short window of a non-instance signature needs a detection item hit and real activity, not just the zone
            if (shortItemMatchOnly && (matched == null || totalItems < sig.MinLocationOnlyItems))
                continue;
            if (sig.RequireShardItems && !hasShard)
                continue;
            if (sig.ExcludeShardItems && hasShard)
                continue;
            // evidence items (see DetectionSignature.EvidenceItems): any non-zero count, positive or
            // negative, strengthens the match but their absence never disqualified it above
            var evidence = sig.EvidenceItems is { Count: > 0 }
                ? itemsCollected.Where(kv => kv.Value != 0 && sig.EvidenceItems.Contains(kv.Key)).Select(kv => kv.Key).ToList()
                : [];
            var itemMatched = matched != null || evidence.Count > 0;
            // minimum signal: a detection item hit, or enough generic activity for location-only tasks -
            // the threshold is per-signature (see DetectionSignature.MinLocationOnlyItems) so a task
            // whose zone is an unambiguous dedicated instance (a Catacombs floor, a Kuudra tier, a
            // hidden accounting zone) can lower it below the default 5.
            // For a dedicated instance (MinLocationOnlyItems <= 1: floors, Kuudra tiers, hidden zone
            // buckets) the zone alone is the evidence, so consumption-only periods ("-120x
            // TOXIC_ARROW_POISON", a lone "-6000000x DUNGEON_CHEST_COST") are activity too; other
            // signatures keep the positive-only rule.
            var activity = sig.MinLocationOnlyItems <= 1 ? totalActivity : totalItems;
            if (!itemMatched && activity < sig.MinLocationOnlyItems)
                continue;
            // Math.Abs: a COST pseudo tag (e.g. DUNGEON_CHEST_COST) stores a NEGATIVE count, but a
            // negative matchedValue would only ever lose tie-breaks it should win - the coin
            // magnitude spent/earned is what matters as evidence weight, not its sign. See
            // PseudoItems.ClassifierWeight for the pseudo-tag weighing itself (1 coin/unit).
            var matchedValue = matched?.Sum(m => Math.Abs((long)itemsCollected[m]) * PseudoItems.ClassifierWeight(m, prices)) ?? 0;
            matchedValue += evidence.Sum(m => Math.Abs((long)itemsCollected[m]) * PseudoItems.ClassifierWeight(m, prices));
            candidates.Add((sig, itemMatched, matchedValue));
        }
        if (candidates.Count == 0)
            return null;
        if (claimedTask != null)
        {
            var claimed = candidates.FirstOrDefault(c =>
                c.sig.MethodName.Equals(claimedTask, StringComparison.OrdinalIgnoreCase));
            if (claimed.sig != null)
                return new(claimed.sig.MethodName, claimed.itemMatched, claimed.sig.Category);
        }
        // a purchase (coins spent in a Bazaar/Auction chat line) happens during any activity and must never
        // take the period from a real, non-hidden task that also matched; it only classifies periods that
        // have no real activity (hidden accounting tasks like Hub Trading do not count as activity)
        var hasRealActivity = candidates.Any(c => !c.sig.Hidden && !c.sig.IsPurchaseSink);
        var best = candidates
            // a fallback task (DetectionSignature.Fallback) only ever classifies a period no regular task
            // matched at all - checked before the item evidence and the value, so a catch-all (even one
            // with a pricey tag) can never take a period away from another task
            .OrderBy(c => c.sig.Fallback)
            .ThenBy(c => hasRealActivity && c.sig.IsPurchaseSink)
            .ThenByDescending(c => c.itemMatched)           // item evidence beats location-only
            // a negative priority accounting bucket (Forge Claims) that matched by item must not take a period from a
            // public task that matched too, however pricey its items are (refined mithril vs the mined ore)
            .ThenBy(c => c.itemMatched && c.sig.Hidden && c.sig.Priority < 0)
            .ThenByDescending(c => c.matchedValue)          // most valuable matched items win
            .ThenByDescending(c => c.sig.Priority)          // explicit override
            .ThenBy(c => c.sig.MethodName, StringComparer.Ordinal) // deterministic
            .First();
        return new(best.sig.MethodName, best.itemMatched, best.sig.Category);
    }
}
