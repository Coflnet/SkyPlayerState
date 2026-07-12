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
    string Category);

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

    public TaskClassifier(TaskRegistry registry)
    {
        signatures = registry.MethodTasks
            .Select(t => t.GetDetectionSignature())
            // a task with neither locations nor detection items (e.g. passive trap tasks)
            // provides no evidence to match on and would classify as anything anywhere
            .Where(s => s.Locations.Count > 0 || s.DetectionItems.Count > 0)
            .ToList();
    }

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
    public Classification Classify(string location, Dictionary<string, int> itemsCollected, double minutes,
        string claimedTask = null, Dictionary<string, double> prices = null)
    {
        if (minutes < 3 || itemsCollected == null || itemsCollected.Count == 0)
            return null;
        var totalItems = itemsCollected.Values.Where(v => v > 0).Sum();
        var hasShard = itemsCollected.Keys.Any(k => k.StartsWith("SHARD_"));
        var candidates = new List<(DetectionSignature sig, bool itemMatched, double matchedValue)>();
        foreach (var sig in signatures)
        {
            if (sig.Locations.Count > 0 && !sig.Locations.Contains(location))
                continue;
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
