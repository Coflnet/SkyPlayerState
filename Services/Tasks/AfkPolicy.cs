using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Decides whether a session was AFK (idle time that must not dilute a task's rate). A session that proves activity is
/// never AFK: a dungeon or Kuudra instance run (see <see cref="IsInstanceRun"/>) or a session with a kept cost (a negative
/// count that survived <see cref="TaskPeriodFolder.FilterOwnedItems"/>: a paid chest, placed Summoning Eyes). Only such
/// sessions went negative on the old net check, and instance loot that is claimed later made every normal run look idle.
/// Any other session is AFK when its gross gains per hour are below <see cref="Factor"/> of the bucket's gross rate.
/// </summary>
internal static class AfkPolicy
{
    internal const double Factor = 0.05;
    /// <summary>A bucket with fewer hours is no reference yet.</summary>
    internal const double MinBucketHours = 2;

    /// <summary>
    /// True when the session is AFK. <paramref name="rareGains"/> are the session's rare and promoted undeclared coins:
    /// real loot that counts for the session, while the bucket side compares common gains only (a rare-heavy pool would
    /// otherwise drop every normal session).
    /// </summary>
    public static bool IsAfk(MethodTask task, BucketAggregate bucket, Dictionary<string, double> commonCounts,
        double rareGains, double hours, Dictionary<string, double> prices)
    {
        if (hours <= 0 || !IsWarm(bucket))
            return false;
        if (ProvesActivity(task, commonCounts) || HasOnlyRareGains(commonCounts, rareGains))
            return false;
        var bucketRate = GrossRate(task, bucket, prices);
        var sessionRate = (SessionGross(task, commonCounts, prices) + rareGains) / hours;
        return bucketRate > 0 && sessionRate < Factor * bucketRate;
    }

    /// <summary>
    /// The session's gross common gains without its late reward tags, like <see cref="GrossRate"/>: a slayer session's
    /// boss drops would otherwise carry it past a line the bucket draws without them. A session holding nothing but late
    /// rewards (and pseudo tags) is judged on them, else it would always look idle.
    /// </summary>
    internal static double SessionGross(MethodTask task, Dictionary<string, double> counts, Dictionary<string, double> prices)
    {
        var work = GrossValue(counts, prices, tag => !IsLateReward(task, tag));
        var holdsWork = counts.Any(e => e.Value > 0 && !PseudoItems.IsPseudo(e.Key) && !IsLateReward(task, e.Key));
        return holdsWork ? work : GrossValue(counts, prices, _ => true);
    }

    /// <summary>An instance run or a session that paid a kept cost was played, whatever it earned.</summary>
    internal static bool ProvesActivity(MethodTask task, Dictionary<string, double> counts)
        => IsInstanceRun(task) || counts.Values.Any(c => c < 0);

    /// <summary>
    /// A dungeon floor or Kuudra tier: the instance cannot be entered idle. The hidden Kuudra Chest Claims task is a claim
    /// outside the instance, not a run.
    /// </summary>
    internal static bool IsInstanceRun(MethodTask task)
        => task is BaseDungeonTask or (BaseKuudraTask and not KuudraChestClaimsTask);

    /// <summary>
    /// All of a session's gains are rare loot and the common remainder holds nothing positive: the loot of a claim or a
    /// paid chest, not idle time.
    /// </summary>
    internal static bool HasOnlyRareGains(Dictionary<string, double> commonCounts, double rareGains)
        => rareGains > 0 && !commonCounts.Values.Any(c => c > 0);

    /// <summary>
    /// The bucket's gross common gains per hour: positive counts only, without the task's late reward tags. Late rewards
    /// fold without seconds (<see cref="TaskPeriodFolder.FoldLateReward"/>), so counting them would inflate the rate every
    /// session that did the work (but not the hand-in) is compared with.
    /// </summary>
    internal static double GrossRate(MethodTask task, BucketAggregate bucket, Dictionary<string, double> prices)
    {
        if (bucket == null || bucket.WSeconds <= 0)
            return 0;
        return GrossValue(bucket.ItemCounts, prices, tag => !IsLateReward(task, tag)) / (bucket.WSeconds / 3600.0);
    }

    private static bool IsLateReward(MethodTask task, string tag)
        => task?.LateRewardDeclaration?.Tags.Contains(tag) == true;

    private static bool IsWarm(BucketAggregate bucket)
        => bucket != null && bucket.WSeconds / 3600.0 >= MinBucketHours;

    /// <summary>Value of the positive counts of the tags <paramref name="includes"/> accepts.</summary>
    private static double GrossValue(Dictionary<string, double> counts, Dictionary<string, double> prices,
        Func<string, bool> includes)
        => counts.Where(e => e.Value > 0 && includes(e.Key)).Sum(e => TaskEstimator.UnitValue(e.Key, prices) * e.Value);
}
