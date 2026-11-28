using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Crafting Empty Chum Buckets into Full Chum Buckets and selling those (community wiki "Chum Bucket", checked 2026-11-28:
/// Moby in the Glowing Mushroom Cave sells an Empty Chum Bucket for 2,000 coins and 8 Glowing Mushrooms; one Empty Chum
/// Bucket and 10 Chum craft a Full Chum Bucket, which players buy on the Bazaar to trade at Moby). Bazaar 2026-11-28:
/// Full 18,005 sell price against Empty 10,000 and Chum 0.2, while an Empty bucket from Moby costs ~11,800 at the
/// Glowing Mushroom sell price, so only filling the bucket pays; selling the empty one does not.
/// </summary>
public class EmptyChumBucketsTask : MethodTask
{
    /// <summary>
    /// One inventory of buckets an hour: the largest craft batch seen was 34 at once (production 2026-11-27), and the
    /// whole Bazaar only fills ~80 Empty Chum Bucket buy orders an hour (13.4k insta-sells a week, 2026-11-28).
    /// </summary>
    private const double BucketsPerHour = 40;
    private const int ChumPerBucket = 10;

    /// <summary>
    /// Crafting works anywhere, so every island (and the private island) is a location; only the item tells the method.
    /// Not location-free: that would also match the stale-location dumps booked at "Unknown" and take them from the
    /// hidden Unknown Location bucket. Left out: the Rift (its items are dimension-bound) and the seasonal Jerry's Workshop.
    /// </summary>
    private static readonly HashSet<string> AnyIsland =
        [.. SkyblockZones.IslandInfo.Keys.Where(island => island is not ("Rift" or "Jerry")), "Your Island"];

    /// <summary>
    /// Catches of fishing: a bucket placed while fishing fills up and is picked up full, which belongs to the fishing task
    /// (one Full Chum Bucket outvalues a period of fish and would take it).
    /// </summary>
    private static readonly HashSet<string> FishingCatches =
    [
        .. FishingEvidence.Water, .. FishingEvidence.Lava, .. DeclaredLoot.BayouSeaCreatures, .. SpookyFishingEvidence.Exclusive
    ];

    protected override string MethodName => "Empty Chum Buckets";
    protected override HashSet<string> Locations => AnyIsland;
    // a gained Full Chum Bucket marks the method (bucket purchases are taken off the counts by PurchaseDiscount); a positive
    // Empty bucket or Chum is only the supply, and lava fishers collect Chum as junk
    protected override HashSet<string> DetectionItems => ["FULL_CHUM_BUCKET"];
    /// <summary>
    /// Fishing catches per gained Full Chum Bucket from which the buckets were placed while fishing, not crafted. A placed
    /// bucket fills with the Chum of several sea creatures, so it comes with dozens of catches: production 2026-11-21..28
    /// periods with placed buckets (Chum not spent) held 25-220 catches per Full bucket, crafts in a named zone none, and
    /// crafts mixed into long fishing backlogs (10 Chum spent per bucket) up to 18. Gains only: sessions classify their
    /// positive counts (costs ride along for the folder), so a consumed Chum can not be the evidence, and a couple of minion
    /// items next to a craft stay far below.
    /// </summary>
    private const int MinCatchesPerPlacedBucket = 20;

    // only crafting counts: buckets that come with a period's worth of fishing were placed while fishing
    protected override bool IsForeignPeriod(IReadOnlyDictionary<string, int> items)
        => Catches(items) >= MinCatchesPerPlacedBucket * (long)Math.Max(0, items.GetValueOrDefault("FULL_CHUM_BUCKET"));

    private static long Catches(IReadOnlyDictionary<string, int> items)
        => items.Where(kv => kv.Value > 0 && FishingCatches.Contains(kv.Key)).Sum(kv => (long)kv.Value);

    protected override List<MethodDrop> FormulaDrops => [new("FULL_CHUM_BUCKET", BucketsPerHour)];
    protected override List<MethodDrop> FormulaCosts =>
        [new("EMPTY_CHUM_BUCKET", BucketsPerHour), new("CHUM", BucketsPerHour * ChumPerBucket)];
    protected override string Category => "Fishing";
    protected override string Where => "Glowing Mushroom Cave";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Chum_Bucket";
    protected override string HowTo =>
        "Get Empty Chum Buckets from Moby in the Glowing Mushroom Cave (2,000 coins and 8 Glowing Mushrooms each) or with a Bazaar buy order. "
        + "Craft each Empty Chum Bucket together with 10 Chum into a Full Chum Bucket; Chum is fishing junk and cheap on the Bazaar. "
        + "A bucket that fills up while placed next to you during fishing is counted with your fishing, not here. "
        + "Sell the Full Chum Buckets on the Bazaar, players buy them to trade at Moby. "
        + "Compare the prices first: the profit is the Full Chum Bucket price minus what the empty bucket and the 10 Chum cost you.";
    protected override string ActionUnit => "buckets";
    protected override double ActionsPerHour => BucketsPerHour;
}
