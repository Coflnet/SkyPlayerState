using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Detection rules of one task, mirror of the matching in MethodTask.FindMatchingPeriods.
/// Used by TaskClassifier to attribute periods to tasks without re-deriving each task's own
/// matching rules.
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
/// One stat signal affecting a task's rates.
/// Value is normalized as clamp(value / Max, 0, 1) and weighted into the effectiveness score.
/// Used to group tracked data from players with similar stats into buckets (see StatScoreService).
/// </summary>
/// <param name="Key">namespaced signal key, e.g. skill:Fishing, attr:Fishing Speed, gear:FISHING_ROD, pet:FISHING, hotm:tier, hotf:tier, agatha:level</param>
/// <param name="Weight">relative importance among the task's factors</param>
/// <param name="Max">normalization cap for the raw value</param>
public record StatFactor(string Key, double Weight, double Max);
