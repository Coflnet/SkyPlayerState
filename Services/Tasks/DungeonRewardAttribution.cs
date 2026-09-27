using System;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Combines a Dungeon Hub reward-claim period with the Catacombs run it belongs to. Reward loot
/// (essence, fragments, books, ...) lands in a period whose scoreboard zone is "Dungeon Hub" - the
/// player teleported out of the dungeon (or is claiming at Croesus) before the loot shows up as
/// "collected" - a median of ~3.7 minutes after their last confirmed floor period in production
/// logs. Left unresolved, that period is unclassified even though it is really part of the run.
/// <para>
/// Pure/static so it is trivially unit testable; called from
/// <see cref="Services.CollectionListener.StoreLocationProfit"/>, which resolves the period's
/// location through <see cref="ResolveLocation"/> BEFORE using it for <c>Period.Location</c>,
/// classification, session accumulation and the "Profit summary" log line - see the class doc there.
/// </para>
/// </summary>
public static class DungeonRewardAttribution
{
    /// <summary>
    /// How long after leaving a Catacombs floor a "Dungeon Hub" period is still attributed back to
    /// it. Production logs show a median claim delay of ~3.7 minutes (teleport out / walk to
    /// Croesus); 30 minutes comfortably covers slower claimers without reattributing an unrelated,
    /// much later hub period.
    /// </summary>
    private static readonly TimeSpan MaxClaimDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Small tolerance for a period start landing a hair before the floor reading was stamped (same-
    /// tick ordering) - a period genuinely predating the floor reading by more than this is never
    /// attributed to it.
    /// </summary>
    private static readonly TimeSpan NegativeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves the effective location a just-flushed period should be attributed to.
    /// </summary>
    /// <param name="location">The period's raw/live location.</param>
    /// <param name="lastFloorZone">
    /// <see cref="Models.ExtractedInfo.LastDungeonFloor"/> - the Catacombs floor zone the player was
    /// last confirmed in (or last claimed at Croesus for), or null/empty when unknown.
    /// </param>
    /// <param name="lastFloorAt"><see cref="Models.ExtractedInfo.LastDungeonFloorAt"/>.</param>
    /// <param name="periodStart">Start time of the period being resolved.</param>
    /// <returns>
    /// <paramref name="lastFloorZone"/> when <paramref name="location"/> canonicalizes to "Dungeon
    /// Hub" and the claim falls within <see cref="MaxClaimDelay"/> (and not more than
    /// <see cref="NegativeTolerance"/> before it); otherwise <paramref name="location"/> unchanged.
    /// </returns>
    public static string ResolveLocation(string location, string lastFloorZone, DateTime lastFloorAt, DateTime periodStart)
    {
        if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(lastFloorZone))
            return location;
        if (SkyblockZones.Canonical(location) != "Dungeon Hub")
            return location;
        var delay = periodStart - lastFloorAt;
        if (delay < -NegativeTolerance || delay > MaxClaimDelay)
            return location;
        return lastFloorZone;
    }

    /// <summary>
    /// True for a real Catacombs floor zone ("The Catacombs (F1)".."(F7)"/"(M1)".."(M7)") - the
    /// Entrance ("(E)") is excluded, it has no reward chests/loot.
    /// </summary>
    public static bool IsFloorZone(string zone) => FloorOf(zone) != null;

    /// <summary>
    /// The floor label ("F7"/"M3") for a Catacombs floor zone string ("The Catacombs (F7)"), or null
    /// when <paramref name="zone"/> is not a floor zone (including the Entrance, "(E)").
    /// </summary>
    public static string FloorOf(string zone)
    {
        var canonical = SkyblockZones.Canonical(zone);
        const string prefix = "The Catacombs (";
        if (string.IsNullOrEmpty(canonical) || !canonical.StartsWith(prefix, StringComparison.Ordinal)
            || !canonical.EndsWith(")", StringComparison.Ordinal))
            return null;
        var floor = canonical.Substring(prefix.Length, canonical.Length - prefix.Length - 1);
        return floor == "E" ? null : floor;
    }
}
