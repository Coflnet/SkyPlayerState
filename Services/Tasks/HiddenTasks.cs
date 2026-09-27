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
