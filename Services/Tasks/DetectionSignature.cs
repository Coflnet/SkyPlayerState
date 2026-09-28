using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Detection rules of one task, mirror of the matching in MethodTask.FindMatchingPeriods.
/// Used by TaskClassifier to attribute periods to tasks without re-deriving each task's own
/// matching rules.
/// </summary>
/// <param name="MinLocationOnlyItems">
/// Minimum total item count a location-only match (no DetectionItems hit) needs for this task's
/// signature to be considered - see MethodTask.MinLocationOnlyItems/TaskClassifier.Classify. Default
/// 5 matches the classifier's original hardcoded minimum; tasks whose zone is an instance that can
/// only mean one activity (a specific Catacombs floor, a specific Kuudra tier, a hidden accounting
/// zone) lower it to 1 since the zone itself is already unambiguous evidence.
/// </param>
public record DetectionSignature(
    string MethodName,
    HashSet<string> Locations,
    HashSet<string> DetectionItems,
    bool RequireShardItems,
    bool ExcludeShardItems,
    int Priority,
    string Category,
    string DerivedFrom = null,
    int MinLocationOnlyItems = 5);

/// <summary>
/// One stat signal affecting a task's rates.
/// Value is normalized as clamp(value / Max, 0, 1) and weighted into the effectiveness score.
/// Used to group tracked data from players with similar stats into buckets (see StatScoreService).
/// </summary>
/// <param name="Key">namespaced signal key, e.g. skill:Fishing, attr:Fishing Speed, gear:FISHING_ROD, pet:FISHING, hotm:tier, hotf:tier, agatha:level</param>
/// <param name="Weight">relative importance among the task's factors</param>
/// <param name="Max">normalization cap for the raw value</param>
public record StatFactor(string Key, double Weight, double Max);
