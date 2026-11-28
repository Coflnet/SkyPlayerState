using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Turning Empty Chum Buckets into Full Chum Buckets and selling those (community wiki "Chum Bucket", checked 2026-11-28:
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

    protected override string MethodName => "Empty Chum Buckets";
    protected override HashSet<string> Locations => AnyIsland;
    // a gained Full Chum Bucket marks the method (bucket purchases are taken off the counts by PurchaseDiscount); a positive
    // Empty bucket or Chum is only the supply, and lava fishers collect Chum as junk
    protected override HashSet<string> DetectionItems => ["FULL_CHUM_BUCKET"];
    protected override List<MethodDrop> FormulaDrops => [new("FULL_CHUM_BUCKET", BucketsPerHour)];
    protected override List<MethodDrop> FormulaCosts =>
        [new("EMPTY_CHUM_BUCKET", BucketsPerHour), new("CHUM", BucketsPerHour * ChumPerBucket)];
    protected override string Category => "Fishing";
    protected override string Where => "Glowing Mushroom Cave";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Chum_Bucket";
    protected override string HowTo =>
        "Get Empty Chum Buckets from Moby in the Glowing Mushroom Cave (2,000 coins and 8 Glowing Mushrooms each) or with a Bazaar buy order. "
        + "Fill each bucket with 10 Chum: craft it together with 10 Chum, or place it next to water while you fish and pick it up once it holds 10 Chum. "
        + "Sell the Full Chum Buckets on the Bazaar, players buy them to trade at Moby. "
        + "Compare the prices first: the profit is the Full Chum Bucket price minus what the empty bucket and the 10 Chum cost you.";
    protected override string ActionUnit => "buckets";
    protected override double ActionsPerHour => BucketsPerHour;
}
