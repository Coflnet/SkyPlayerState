using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Detection rules of one task, mirror of the matching in MethodTask.FindMatchingPeriods.
/// </summary>
public record DetectionSignature(
    string MethodName,
    HashSet<string> Locations,
    HashSet<string> DetectionItems,
    bool RequireShardItems,
    bool ExcludeShardItems,
    int Priority,
    string Category,
    string DerivedFrom = null);

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
    /// location-only match with fewer than 5 items collected).
    /// </summary>
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
        DateTime? currentIslandAt = null, DateTime? currentLocationSince = null, DateTime? currentLocationSeenAt = null)
    {
        if (minutes < 3 || itemsCollected == null || itemsCollected.Count == 0)
            return null;
        var totalItems = itemsCollected.Values.Where(v => v > 0).Sum();
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
            List<string> matched = null;
            if (sig.DetectionItems.Count > 0)
            {
                matched = itemsCollected.Keys.Where(k => sig.DetectionItems.Contains(k)).ToList();
                if (matched.Count == 0)
                    continue;
            }
            if (sig.RequireShardItems && !hasShard)
                continue;
            if (sig.ExcludeShardItems && hasShard)
                continue;
            var itemMatched = matched != null;
            // minimum signal: a detection item hit, or enough generic activity for location-only tasks
            if (!itemMatched && totalItems < 5)
                continue;
            var matchedValue = matched?.Sum(m => (prices?.GetValueOrDefault(m) ?? 0) * itemsCollected[m]) ?? 0;
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
        var best = candidates
            .OrderByDescending(c => c.itemMatched)          // item evidence beats location-only
            .ThenByDescending(c => c.matchedValue)          // most valuable matched items win
            .ThenByDescending(c => c.sig.Priority)          // explicit override
            .ThenBy(c => c.sig.MethodName, StringComparer.Ordinal) // deterministic
            .First();
        return new(best.sig.MethodName, best.itemMatched, best.sig.Category);
    }
}
