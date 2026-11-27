using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression (production audit 2026-11): sessions that paid a cost went negative on the net AFK check and were dropped
/// together with their loot (Ender Dragon 25 h dropped, shown ~5x too high), and instance claim loot folded without seconds
/// inflated the bucket rate so normal runs looked AFK (Kuudra T5 196M/h kept vs 24M/h over all outcomes).
/// </summary>
[NonParallelizable]
public class AfkPolicyTests
{
    private static readonly Dictionary<string, double> Prices = new()
    {
        ["SUMMONING_EYE"] = 1_000_000, ["OLD_FRAGMENT"] = 5_000, ["ESSENCE_WITHER"] = 2_000,
        ["ESSENCE_CRIMSON"] = 1_000, ["LOG"] = 10, ["MIRIA_COUPON"] = 1_000_000, ["HYPERION"] = 900_000_000,
        ["TARANTULA_WEB"] = 1_000, ["TOXIC_ARROW_POISON"] = 100
    };

    [TearDown]
    public void ResetSwitch() => TaskRateOptions.FullValueRates = false;

    /// <summary>A warm bucket of <paramref name="hours"/> hours holding <paramref name="counts"/>.</summary>
    private static BucketAggregate Bucket(double hours, Dictionary<string, double> counts)
        => new() { WSeconds = hours * 3600, WPeriods = 20, ItemCounts = counts };

    [TestCase(false)]
    [TestCase(true)]
    public void DragonPeriod_WithPlacedEyes_IsNotAfk(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var bucket = Bucket(10, new() { ["OLD_FRAGMENT"] = 10_000 });

        AfkPolicy.IsAfk(new EnderDragonTask(), bucket, new() { ["SUMMONING_EYE"] = -2 }, 0, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void M7Period_WithChestCostAndLittleEssence_IsNotAfk()
    {
        var bucket = Bucket(10, new() { ["ESSENCE_WITHER"] = 500_000 });
        var counts = new Dictionary<string, double> { [PseudoItems.DUNGEON_CHEST_COST] = -2_000_000, ["ESSENCE_WITHER"] = 25 };

        AfkPolicy.IsAfk(new M7Task(), bucket, counts, 0, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void FloorPeriod_WithOnlyARunMarker_IsNotAfk()
    {
        var bucket = Bucket(10, new() { ["ESSENCE_WITHER"] = 500_000 });

        AfkPolicy.IsAfk(new F7Task(), bucket, new() { [PseudoItems.DUNGEON_RUN] = 1 }, 0, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void KuudraT5EssenceSession_AgainstAClaimInflatedBucket_IsNotAfk()
    {
        // the claims' armor folded without seconds: 900M over 10 hours dwarfs the essence of a normal run
        var bucket = Bucket(10, new() { ["ESSENCE_CRIMSON"] = 20_000, ["HYPERION"] = 10 });

        AfkPolicy.IsAfk(new KuudraT5Task(), bucket, new() { ["ESSENCE_CRIMSON"] = 1_000 }, 0, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void IdleKuudraChestClaims_AreNoInstanceRun()
    {
        AfkPolicy.IsInstanceRun(new KuudraChestClaimsTask()).Should().BeFalse();
        AfkPolicy.IsInstanceRun(new KuudraT1Task()).Should().BeTrue();
        AfkPolicy.IsInstanceRun(new M4Task()).Should().BeTrue();
        AfkPolicy.IsInstanceRun(new EnderDragonTask()).Should().BeFalse();
        AfkPolicy.IsInstanceRun(null).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OpenWorldSession_WithNearlyNoValue_IsAfk(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var bucket = Bucket(10, new() { ["LOG"] = 1_000_000 });

        AfkPolicy.IsAfk(new HelixForagingTask(), bucket, new() { ["LOG"] = 10 }, 0, 0.5, Prices).Should().BeTrue();
        AfkPolicy.IsAfk(new HelixForagingTask(), bucket, new(), 0, 0.5, Prices).Should().BeTrue();
    }

    [Test]
    public void ColdBucket_OrNoTime_IsNeverAfk()
    {
        AfkPolicy.IsAfk(null, Bucket(1, new() { ["LOG"] = 1_000_000 }), new(), 0, 0.5, Prices).Should().BeFalse();
        AfkPolicy.IsAfk(null, null, new(), 0, 0.5, Prices).Should().BeFalse();
        AfkPolicy.IsAfk(null, Bucket(10, new() { ["LOG"] = 1_000_000 }), new(), 0, 0, Prices).Should().BeFalse();
    }

    [Test]
    public void GrossRate_LeavesOutLateRewardTags_AndCosts()
    {
        // Helix Foraging's Miria coupons are handed in later and fold without seconds
        var bucket = Bucket(10, new() { ["LOG"] = 100_000, ["MIRIA_COUPON"] = 100, ["SUMMONING_EYE"] = -5 });

        AfkPolicy.GrossRate(new HelixForagingTask(), bucket, Prices).Should().BeApproximately(100_000, 1e-6);
    }

    [Test]
    public void ForagingSession_IsComparedWithoutTheBucketsCoupons()
    {
        // 30 minutes of 1k logs = 20k/h; the logs alone are 100k/h (5%: 5k/h), with the coupons 10.1M/h (5%: 505k/h)
        var bucket = Bucket(10, new() { ["LOG"] = 100_000, ["MIRIA_COUPON"] = 100 });

        AfkPolicy.IsAfk(new HelixForagingTask(), bucket, new() { ["LOG"] = 1_000 }, 0, 0.5, Prices).Should().BeFalse();
    }

    /// <summary>10 hours: 10M/h of work loot and 2.7M/h of boss webs, which the bucket leaves out.</summary>
    private static BucketAggregate TarantulaBucket()
        => Bucket(10, new() { ["TOXIC_ARROW_POISON"] = 1_000_000, ["TARANTULA_WEB"] = 27_000 });

    [Test]
    public void SlayerSession_IsJudgedWithoutItsBossDrops_LikeTheBucket()
    {
        // 30 minutes: 200/h of work loot and 1M/h of webs; with the webs it would clear the 500k/h line
        var counts = new Dictionary<string, double> { ["TOXIC_ARROW_POISON"] = 1, ["TARANTULA_WEB"] = 500 };

        AfkPolicy.SessionGross(new TarantulaSlayerTask(), counts, Prices).Should().BeApproximately(100, 1e-6);
        AfkPolicy.IsAfk(new TarantulaSlayerTask(), TarantulaBucket(), counts, 0, 0.5, Prices).Should().BeTrue();
    }

    [Test]
    public void SlayerSession_WithOnlyBossDrops_IsJudgedOnThem()
    {
        var counts = new Dictionary<string, double> { ["TARANTULA_WEB"] = 500, [PseudoItems.DUNGEON_RUN] = 1 };

        AfkPolicy.SessionGross(new TarantulaSlayerTask(), counts, Prices).Should().BeApproximately(500_000, 1e-6);
        AfkPolicy.IsAfk(new TarantulaSlayerTask(), TarantulaBucket(), counts, 0, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void RareGains_CountForTheSession()
    {
        var bucket = Bucket(10, new() { ["LOG"] = 100_000_000 });
        var counts = new Dictionary<string, double> { ["LOG"] = 1 };

        AfkPolicy.IsAfk(null, bucket, counts, 0, 0.5, Prices).Should().BeTrue();
        AfkPolicy.IsAfk(null, bucket, counts, 50_000_000, 0.5, Prices).Should().BeFalse();
    }

    [Test]
    public void OnlyRareGains_AreNeverAfk()
        => AfkPolicy.IsAfk(null, Bucket(10, new() { ["LOG"] = 1e12 }), new(), 1_000_000, 10, Prices).Should().BeFalse();
}


/// <summary>
/// Fold level regressions on the Ender Dragon, an open-world task (no instance exemption) that pays for its fights:
/// <list type="bullet">
/// <item>The AFK check compared the session's net value (placed eyes included), so a fight that placed eyes went
/// negative and was dropped as AFK; its seconds were lost while its loot could arrive later without seconds. A kept cost
/// now proves activity with either switch.</item>
/// <item>Winsorizing ran before the AFK check: its cap follows the net rate, so on a cost-heavy task the best sessions
/// were scaled below the gross AFK line and dropped.</item>
/// </list>
/// </summary>
[NonParallelizable]
public class AfkPolicyFoldTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "Ender Dragon";

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["OLD_FRAGMENT"] = 5_000, ["SUMMONING_EYE"] = 1_000_000, ["PET_ENDER_DRAGON"] = 5_000_000
    };

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task)
            => System.Threading.Tasks.Task.FromResult(Stored);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    [TearDown]
    public void ResetSwitch() => TaskRateOptions.FullValueRates = false;

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make(BucketAggregate bucket)
    {
        var aggregates = new FakeAggregates();
        aggregates.SetSnapshotForTest(new() { [(Task, StatScoreService.UnknownBucket)] = bucket });
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    /// <summary>10 hours of 5M/h fragments with <paramref name="eyes"/> eyes placed (1M each).</summary>
    private static BucketAggregate Bucket(double eyes) => new()
    {
        WSeconds = 36000, WPeriods = 20,
        ItemCounts = new() { ["OLD_FRAGMENT"] = 10_000, ["SUMMONING_EYE"] = -eyes }
    };

    private static Period At(DateTime start, double minutes, Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "Dragon's Nest", DetectedTask = Task,
        StartTime = start, EndTime = start.AddMinutes(minutes), ItemsCollected = items
    };

    private static readonly DateTime Start = new(2026, 11, 20, 12, 0, 0);

    [TestCase(false)]
    [TestCase(true)]
    public async Task FightThatPlacedEyes_KeepsItsSeconds_AndTheLateDropAddsNone(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var (folder, aggregates) = Make(Bucket(20));
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);

        // 100k/h of fragments gained, net -3.9M/h after the 2 eyes
        await folder.Fold(At(Start, 30, new() { ["OLD_FRAGMENT"] = 10, ["SUMMONING_EYE"] = -2 }), new StateObject(), Prices);
        await folder.FoldLateReward(At(Start.AddMinutes(31), 0.2, new() { ["PET_ENDER_DRAGON"] = 1 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        aggregates.Stored.WSeconds.Should().BeApproximately(1800, 1, "the late drop decays the row a little but adds no seconds");
        aggregates.Stored.ItemCounts["SUMMONING_EYE"].Should().BeApproximately(-2, 1e-6);
    }

    [Test]
    public async Task FightThatOnlyPlacedEyes_IsNotAfk()
    {
        var (folder, aggregates) = Make(Bucket(20));
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);

        await folder.Fold(At(Start, 30, new() { ["SUMMONING_EYE"] = -2 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        aggregates.Stored.WSeconds.Should().BeApproximately(1800, 1);
    }

    /// <summary>
    /// The bucket earns 5M/h gross but only 50k/h net. A strong eye-free session (1M/h) is winsorized to 4x the net rate
    /// (200k/h), below 5% of the gross rate (250k/h); judged before the cap it is no AFK session.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task StrongSession_OnACostHeavyBucket_IsWinsorizedButNotAfk(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var (folder, aggregates) = Make(Bucket(49.5));
        var afk = FoldMetrics.CountOf(Task, FoldMetrics.Afk);
        var winsorized = FoldMetrics.WinsorizedOf(Task);

        await folder.Fold(At(Start, 30, new() { ["OLD_FRAGMENT"] = 100 }), new StateObject(), Prices);

        FoldMetrics.CountOf(Task, FoldMetrics.Afk).Should().Be(afk);
        FoldMetrics.WinsorizedOf(Task).Should().Be(winsorized + 1, "the cap still applies to what is folded");
        aggregates.Stored.WSeconds.Should().BeApproximately(1800, 1);
        aggregates.Stored.ItemCounts["OLD_FRAGMENT"].Should().BeApproximately(20, 1e-6);
    }
}
