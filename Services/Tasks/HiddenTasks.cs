using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Hidden accounting tasks ──
// These never appear in /cofl task, /Task/methods or TaskEstimator.EstimateAll (MethodTask.Hidden -
// see TaskRegistry.PublicTasks/PublicMethodTasks) but fully participate in TaskClassifier and the
// aggregation/fold pipeline like any other task - that IS the point. Production log analysis (2026-09)
// found ~59% of coin value went unclassified, dominated by (a) private-island minion collection and
// (b) Bazaar/Auction House purchases (the purchased item shows up as "collected" the same as a real
// drop). Without these tasks that activity either got silently misattributed to whatever real task
// also happened to match the location/items, or inflated the "unclassified" bucket to the point it
// stopped being a useful signal. These tasks absorb exactly that noise so the unclassified remainder
// means what it says.

/// <summary>
/// Absorbs periods dominated by Bazaar purchases (see <see cref="Services.PurchaseListener"/> ->
/// <see cref="PseudoItems.BAZAAR_PURCHASE"/>). Its own profit is always ~0 (an EVIDENCE pseudo tag's
/// coin value is 0 - see <see cref="PseudoItems"/>/<see cref="CoinValueRegistry.Value"/>) - that is
/// intentional, buying something is not profit, it just should not count as unexplained revenue
/// either.
/// </summary>
public class BazaarPurchaseTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Bazaar Purchases";
    protected override string Category => "Trading";
    protected override HashSet<string> DetectionItems => [PseudoItems.BAZAAR_PURCHASE];
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs periods dominated by Bazaar purchases so that spending is "
        + "not counted as unclassified revenue or misattributed to an unrelated task. Never shown to players.";
}

/// <summary>Same purpose as <see cref="BazaarPurchaseTask"/>, for Auction House purchases.</summary>
public class AuctionPurchaseTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Auction Purchases";
    protected override string Category => "Trading";
    protected override HashSet<string> DetectionItems => [PseudoItems.AUCTION_PURCHASE];
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs periods dominated by Auction House purchases so that spending "
        + "is not counted as unclassified revenue or misattributed to an unrelated task. Never shown to players.";
}

/// <summary>
/// Absorbs private-island minion collection (picking up what your minions produced while away) - the
/// single largest source of unclassified production coin value found in the 2026-09 analysis.
/// DetectionItems is every minion product tag plus its compacted/enchanted forms, sourced from the
/// minion command's own data (<see cref="MinionData"/>) so this never drifts from what the minion
/// command itself already knows about.
/// </summary>
public class MinionCollectionTask : MethodTask
{
    // Computed once - MinionData.DetectionItems already caches its own result, but DetectionItems is
    // re-read on every classification/match call (TaskClassifier builds one DetectionSignature per
    // task at construction time, MethodTask.FindMatchingPeriods reads it directly) so this avoids
    // rebuilding a HashSet<string> copy every time.
    private static readonly HashSet<string> MinionDetectionItems =
        new(MinionData.DetectionItems, StringComparer.OrdinalIgnoreCase);

    protected override bool Hidden => true;
    protected override string MethodName => "Minion Collection";
    protected override string Category => "Passive";
    // "Your Island" is the literal scoreboard zone for a private island, the same string regardless
    // of island type - it is deliberately ambiguous/unmapped in SkyblockZones (AmbiguousZones), but
    // SkyblockZones.Matches checks a task's Locations against the exact zone string FIRST, before
    // ever consulting the zone-to-island map, so this literal match works without "Your Island"
    // needing to resolve to an island - see MinionCollectionTask.Tests.cs.
    protected override HashSet<string> Locations => ["Your Island"];
    protected override HashSet<string> DetectionItems => MinionDetectionItems;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs private-island minion collection so it is not counted as "
        + "unclassified revenue. Never shown to players.";
}

// ── Hidden accounting tasks for zones where no money making method exists (2026-09) ──
// Location-only, MinLocationOnlyItems 1 (their zone alone is enough signal - these exist purely to
// stop noise inflating the unclassified bucket, not to require a minimum activity level) and a
// Priority well below every public task, so any real, item-matched task at the same zone always
// wins - see TaskClassifier.Classify's itemMatched-first tie-break, which already guarantees this
// regardless of Priority for a task with its own DetectionItems (e.g. MinionCollectionTask/crop
// farming tasks); the low Priority only matters for the rare case of two location-only candidates.

/// <summary>
/// Everything that appears on the private island that is not a minion product is a transfer (chests,
/// Bazaar via cookie, crafting inputs/outputs, ...), not profit. MinionCollectionTask (item matched)
/// still wins wherever its own detection items occur.
/// </summary>
public class PrivateIslandActivityTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Private Island Activity";
    protected override string Category => "Passive";
    protected override HashSet<string> Locations => ["Your Island"];
    protected override int MinLocationOnlyItems => 1;
    protected override int Priority => -100;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs private-island activity that is not minion collection (chests, "
        + "Bazaar via cookie, crafting) so it is not counted as unclassified revenue. Never shown to players.";
}

/// <summary>
/// Rift items are bound to the Rift dimension (not tradable), so anything collected there is not
/// coin profit. Item-matched tasks still win wherever their own detection items occur.
/// </summary>
public class RiftActivityTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Rift Activity";
    protected override string Category => "Passive";
    protected override HashSet<string> Locations => ["Rift"];
    protected override int MinLocationOnlyItems => 1;
    protected override int Priority => -100;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: items collected in the Rift are dimension-bound and not coin profit, so "
        + "they are not counted as unclassified revenue. Never shown to players.";
}

/// <summary>
/// Periods with no scoreboard area read yet (ExtractedInfo.CurrentLocation defaults to "Unknown").
/// Their large "profit" is whole-inventory/sack baseline catch-up at session start, not earnings.
/// </summary>
public class UnknownLocationTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Unknown Location";
    protected override string Category => "Passive";
    protected override HashSet<string> Locations => ["Unknown"];
    protected override int MinLocationOnlyItems => 1;
    protected override int Priority => -100;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: periods before any scoreboard area was read. These hold whole-inventory/sack "
        + "baseline catch-ups at session start (billions of apparent coins), not earnings. Never shown to players.";
}

/// <summary>
/// Forge outputs (Skeleton Key, Refined Mithril, Perfect Plate, Drill Engine, ...) are crafted from
/// purchased inputs - their value is not profit, it is a transformation of coins already spent.
/// </summary>
public class ForgeClaimsTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Forge Claims";
    protected override string Category => "Passive";
    protected override HashSet<string> Locations => ["The Forge", "Forge Basin"];
    protected override int MinLocationOnlyItems => 1;
    protected override int Priority => -100;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs Forge claims (crafted from purchased inputs) so their output "
        + "value is not counted as unclassified revenue. Never shown to players.";
}

/// <summary>
/// Hub (and Crimson Isle equivalent) trading spots: Auction House, Bazaar, Bank, and player-facing
/// shop NPCs. "Community Center"/"Wizard Tower" are matched as literal zone strings, the same trick
/// MinionCollectionTask uses for "Your Island" - they are deliberately left in
/// SkyblockZones.AmbiguousZones (shared with other islands, pinned by
/// SkyblockZonesTests.AmbiguousZone_WithFormatCodeNoise_StillResolvesNull) rather than added to the
/// Hub's zone map, since that would misattribute the same zone names on other islands too; Matches()
/// checks a task's Locations against the exact zone string before ever consulting the island map, so
/// the literal match still works. "Museum" additionally canonicalizes every player's own museum zone
/// (e.g. "LXMini's Museum") via SkyblockZones.Canonical.
/// </summary>
public class HubTradingTask : MethodTask
{
    protected override bool Hidden => true;
    protected override string MethodName => "Hub Trading";
    protected override string Category => "Trading";
    protected override HashSet<string> Locations =>
    [
        "Auction House", "Bazaar Alley", "Bank", "Community Center", "Builder's House", "Village",
        "Pet Care", "Blacksmith", "Fashion Shop", "Thaumaturgist", "Taylor's Shop", "Shen's Auction",
        "Wizard Tower", "Museum"
    ];
    protected override int MinLocationOnlyItems => 1;
    protected override int Priority => -100;
    protected override TaskType TaskType => TaskType.Passive;
    public override string Description =>
        "Hidden accounting task: absorbs Hub/Crimson Isle trading-spot activity (Auction House, Bazaar, "
        + "Bank, shop NPCs, museum donations) so it is not counted as unclassified revenue. Never shown to players.";
}
