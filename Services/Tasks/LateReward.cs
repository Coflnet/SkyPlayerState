using System;
using System.Collections.Generic;
using System.Linq;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// A reward that arrives after the work that earned it: Galatea contest coupons are handed in minutes
/// after the foraging/hunting/fishing that filled the contest, a slayer boss drops its loot after the
/// mobs that spawned it were killed. A period holding nothing but such items must neither become a
/// task of its own nor be divided by its own few seconds - see <see cref="MethodTask.LateReward"/>.
/// </summary>
/// <param name="Tags">item tags that are the late reward</param>
/// <param name="EarnDuration">how long earning the reward takes (a Galatea contest lasts 20 minutes)</param>
/// <param name="Label">short display label for the breakdown ("Contest reward", "Boss drop")</param>
public record LateRewardSpec(IReadOnlySet<string> Tags, TimeSpan EarnDuration, string Label)
{
    /// <summary>Value of <see cref="DropInfo.Kind"/> for a late reward drop.</summary>
    public const string DropKind = "late_reward";

    /// <summary>
    /// True when the items hold at least one positive real (non pseudo) item and every one of them is
    /// a late reward tag. Costs and other negative counts are ignored.
    /// </summary>
    public bool IsOnlyReward(IReadOnlyDictionary<string, int> items)
    {
        if (items == null)
            return false;
        var any = false;
        foreach (var (tag, count) in items)
        {
            if (count <= 0 || PseudoItems.IsPseudo(tag))
                continue;
            if (!Tags.Contains(tag))
                return false;
            any = true;
        }
        return any;
    }
}

/// <summary>Galatea contest rewards, shared by every Galatea foraging/hunting task.</summary>
public static class GalateaContestRewards
{
    /// <summary>Every Galatea contest lasts 20 minutes.</summary>
    public static readonly TimeSpan ContestDuration = TimeSpan.FromMinutes(20);
    public const string Label = "Contest reward";

    /// <summary>Agatha (Murkwater Loch) and Starlyn are on Moonglade.</summary>
    public static readonly LateRewardSpec Moonglade = new(
        new HashSet<string> { "AGATHA_COUPON", "STARLYN_PRIZE" }, ContestDuration, Label);

    /// <summary>Miria (Miria's Hut) is on Torrhus.</summary>
    public static readonly LateRewardSpec Torrhus = new(
        new HashSet<string> { "MIRIA_COUPON", "MIRIA_PRIZE" }, ContestDuration, Label);

    /// <summary>
    /// The contest rewards earnable by a task whose <paramref name="locations"/> are all on one of the
    /// two Galatea islands (island keys or zones), otherwise null.
    /// </summary>
    public static LateRewardSpec ForLocations(IEnumerable<string> locations)
    {
        var islands = locations.Select(l => SkyblockZones.IslandOf(l) ?? l).Distinct().ToList();
        if (islands.Count != 1)
            return null;
        return islands[0] switch
        {
            "Moonglade" => Moonglade,
            "Torrhus" => Torrhus,
            _ => null
        };
    }
}

/// <summary>
/// Write time attribution of a reward-only period to the task that earned it, same idea as
/// <see cref="KuudraRewardAttribution"/>/<see cref="DungeonRewardAttribution"/>. The listener remembers
/// the last period of a task that declares a <see cref="MethodTask.LateReward"/> on
/// <see cref="Models.ExtractedInfo"/>; a reward-only period shortly after it, on the same island, is credited to
/// that task and adds no work time.
/// </summary>
public static class LateRewardAttribution
{
    /// <summary>A reward is credited to the earning task for this many earn durations after its last period.</summary>
    public const double ClaimWindowFactor = 2;

    /// <summary>
    /// The task a reward-only period belongs to, or null (orphan). <paramref name="earner"/> is the last
    /// remembered earning task; it must declare all of the period's items as late reward.
    /// </summary>
    public static ProfitTask Resolve(ProfitTask earner, DateTime earnerAt, string earnerLocation,
        string location, IReadOnlyDictionary<string, int> items, DateTime periodStart)
    {
        var spec = earner?.LateRewardDeclaration;
        if (spec == null || !spec.IsOnlyReward(items))
            return null;
        var delay = periodStart - earnerAt;
        if (delay > TimeSpan.FromTicks((long)(spec.EarnDuration.Ticks * ClaimWindowFactor)))
            return null;
        // the hand-in must be on the island the work was done on (Agatha is on Moonglade, Miria on Torrhus)
        var island = SkyblockZones.IslandOf(location);
        var earnIsland = SkyblockZones.IslandOf(earnerLocation);
        if (island != null && earnIsland != null && island != earnIsland)
            return null;
        return earner;
    }
}
