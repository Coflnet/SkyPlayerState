using System.Collections.Generic;
using System.Linq;

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
/// <param name="EvidenceItems">
/// Optional, NON-mandatory supporting items (see MethodTask.EvidenceItems): a non-zero count of one
/// (positive or negative) makes a location match item-matched and adds to its matchedValue, but an
/// absent one never disqualifies the candidate (unlike DetectionItems).
/// </param>
/// <param name="Fallback">
/// Catch-all flag (see MethodTask.Fallback): every candidate of a fallback task ranks below every
/// candidate of a regular task, so it can only ever classify a period no regular task matched -
/// never take one away from another task, not even by out-valuing it.
/// </param>
/// <param name="CostItems">
/// Real item tags the task consumes (see MethodTask.KeepableCostTags). A NEGATIVE count (item consumed or sold) only
/// counts as detection/evidence for a tag listed here or a cost pseudo tag (DUNGEON_CHEST_COST); any other consumed
/// item says nothing about the activity (candy spent at a shop, exp bottles drunk, an ingredient of a craft flip).
/// </param>
/// <param name="ZoneDetectionItems">
/// Per-zone replacement of <see cref="DetectionItems"/> (see MethodTask.ZoneDetectionItems): in a zone
/// matching one of the keys the period needs one of THESE items, whatever DetectionItems lists.
/// </param>
/// <param name="ExcludedZones">
/// Zones never matched even though <see cref="Locations"/> covers them (see MethodTask.ExcludedZones), e.g. the
/// Glacite Tunnels for a task listing the whole Dwarven Mines island.
/// </param>
/// <param name="ZoneExcludedItems">
/// Per-zone veto (see MethodTask.ZoneExcludedItems): in a zone matching one of the keys, a period
/// holding a positive count of any of these items is never attributed to this task.
/// </param>
/// <param name="BulkClaim">
/// Classification only (see MethodTask.BulkClaim): a period this rule calls bulk counts as an item match of this task and
/// is not vetoed by <see cref="ZoneExcludedItems"/>.
/// </param>
/// <param name="BulkYield">
/// Classification only (see MethodTask.BulkYield): a period this rule calls bulk is never attributed to this task.
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
    int MinLocationOnlyItems = 5,
    HashSet<string> EvidenceItems = null,
    bool Fallback = false,
    Dictionary<string, HashSet<string>> ZoneDetectionItems = null,
    Dictionary<string, HashSet<string>> ZoneExcludedItems = null,
    bool Hidden = false,
    HashSet<string> CostItems = null,
    HashSet<string> ExcludedZones = null,
    BulkItemRule BulkClaim = null,
    BulkItemRule BulkYield = null)
{
    /// <summary>True when <paramref name="zone"/> is in <see cref="Locations"/> and not in <see cref="ExcludedZones"/>.</summary>
    public bool MatchesZone(string zone) => SkyblockZones.Matches(Locations, zone, ExcludedZones);

    /// <summary>
    /// True for the hidden Bazaar/Auction purchase accounting tasks: their only detection items are
    /// purchase pseudo tags. They classify purchase-only periods but must never displace a real task.
    /// </summary>
    public bool IsPurchaseSink => DetectionItems.Count > 0 && DetectionItems.All(PseudoItems.IsPurchase);

    /// <summary>
    /// True when <paramref name="count"/> of <paramref name="tag"/> is a signal for this task: a gained item (&gt; 0), or a
    /// consumed one (&lt; 0) that is a cost of this task (<see cref="CostItems"/> or a cost pseudo tag).
    /// </summary>
    public bool CountsAsSignal(string tag, int count)
        => count > 0 || (count < 0 && (PseudoItems.IsCost(tag) || (CostItems?.Contains(tag) ?? false)));

    /// <summary>The items one of which a period at <paramref name="zone"/> must hold: the zone-specific set when one applies, else <see cref="DetectionItems"/>.</summary>
    public HashSet<string> DetectionItemsAt(string zone)
    {
        if (ZoneDetectionItems != null)
            foreach (var (key, items) in ZoneDetectionItems)
                if (SkyblockZones.Matches([key], zone))
                    return items;
        return DetectionItems;
    }

    /// <summary>True when a zone veto (<see cref="ZoneExcludedItems"/>) applies at <paramref name="zone"/> to a period holding <paramref name="itemTags"/>.</summary>
    public bool IsExcludedAt(string zone, IEnumerable<string> itemTags)
    {
        if (ZoneExcludedItems == null)
            return false;
        foreach (var (key, items) in ZoneExcludedItems)
            if (SkyblockZones.Matches([key], zone) && itemTags.Any(items.Contains))
                return true;
        return false;
    }
}

/// <summary>
/// One stat signal affecting a task's rates.
/// Value is normalized as clamp(value / Max, 0, 1) and weighted into the effectiveness score.
/// Used to group tracked data from players with similar stats into buckets (see StatScoreService).
/// </summary>
/// <param name="Key">namespaced signal key, e.g. skill:Fishing, attr:Fishing Speed, gear:FISHING_ROD, pet:FISHING, hotm:tier, hotf:tier, agatha:level</param>
/// <param name="Weight">relative importance among the task's factors</param>
/// <param name="Max">normalization cap for the raw value</param>
public record StatFactor(string Key, double Weight, double Max);
