using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Newtonsoft.Json;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// <see cref="MethodTask.LateReward"/>: rewards that arrive after the work that earned them. Production
/// 2026-10: a 30 coupon hand-in that lasted 16.6 seconds made "Galatea Contests" a 102.9M coins/hour task.
/// </summary>
public class LateRewardTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0);

    private static Period MakePeriod(string location, DateTime start, TimeSpan duration, long profit,
        Dictionary<string, int> items, string detectedTask = null) => new()
    {
        PlayerUuid = "test", Server = "m1", Location = location, Profit = profit,
        StartTime = start, EndTime = start + duration, ItemsCollected = items, DetectedTask = detectedTask
    };

    private static TaskParams MakeParams(params Period[] periods) => new()
    {
        TestTime = Start.AddHours(2),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = periods.GroupBy(l => l.Location).ToDictionary(l => l.Key, l => l.ToArray()),
        CleanPrices = new Dictionary<string, long> { { "AGATHA_COUPON", 12211 }, { "FIG_LOG", 100 }, { "NULL_SPHERE", 500_000 } },
        BazaarPrices = [],
        Names = new Dictionary<string, string>()
    };

    // the production hand-in: 30 coupons, 366330 coins, 16.6 seconds
    private static Period HandIn(DateTime start, string detectedTask = null)
        => MakePeriod("Murkwater Loch", start, TimeSpan.FromSeconds(16.6), 366_330, new() { { "AGATHA_COUPON", 30 } }, detectedTask);

    private static Period FigWork(DateTime start, string detectedTask = "Fig Foraging")
        => MakePeriod("Moonglade Marsh", start, TimeSpan.FromMinutes(10), 1_000_000, new() { { "FIG_LOG", 10000 } }, detectedTask);

    // ── the bug ──

    [Test]
    public void GalateaContests_IsNoPublicTask()
    {
        var registry = new TaskRegistry();
        registry.GetByName("Galatea Contests").Should().NotBeNull("it stays a classification sink");
        registry.PublicTasks.Select(t => (t as MethodTask)?.GetDetectionSignature().MethodName).Should().NotContain("Galatea Contests");
        registry.PublicMethodTasks.Select(t => t.GetDetectionSignature().MethodName).Should().NotContain("Galatea Contests");
    }

    [Test]
    public async Task HandInAlone_IsNotDividedByItsSeconds_ItCountsForTheContestDuration()
    {
        // 366330 over 16.6 s would be 79.4M/h; as an orphan it counts for the 20 minute contest: 1098990/h
        var result = await new GalateaContestsTask().Execute(MakeParams(HandIn(Start)));

        result.ProfitPerHour.Should().Be(1_098_990);
        result.Breakdown.TrackedHours.Should().BeApproximately(20.0 / 60, 1e-9);
    }

    [Test]
    public async Task OrphanRewardPeriod_UsesTheTwentyMinuteEarnDuration_EvenWhenOnlyAFewSecondsLong()
    {
        var result = await new GalateaContestsTask().Execute(MakeParams(
            MakePeriod("Miria's Hut", Start, TimeSpan.FromSeconds(3), 120_000, new() { { "MIRIA_COUPON", 10 } })));

        result.Breakdown.TrackedHours.Should().BeApproximately(1.0 / 3, 1e-9);
        result.ProfitPerHour.Should().Be(360_000);
    }

    // ── credited to the earning task ──

    [Test]
    public async Task CouponOnlyPeriodAfterForaging_IsCreditedToTheForagingTask_WithoutHandInTime()
    {
        var work = FigWork(Start);
        var handIn = HandIn(Start.AddMinutes(12), "Fig Foraging");

        var result = await new FigForagingTask().Execute(MakeParams(work, handIn));

        // (1,000,000 + 366,330) over the 10 minutes of work only
        result.ProfitPerHour.Should().Be((int)((1_366_330) / (10.0 / 60)));
        result.Breakdown.TrackedHours.Should().BeApproximately(10.0 / 60, 1e-9);
        var coupon = result.Breakdown.Drops.Single(d => d.ItemTag == "AGATHA_COUPON");
        coupon.Kind.Should().Be("late_reward");
        coupon.Label.Should().Be("Contest reward");
        coupon.ContributionPerHour.Should().BeApproximately(30 * 12211 / (10.0 / 60), 1);
        result.Breakdown.Drops.Single(d => d.ItemTag == "FIG_LOG").Kind.Should().BeNull();
        result.Breakdown.LateRewardPerHour.Should().BeApproximately(coupon.ContributionPerHour, 1e-6);
        result.Breakdown.LateRewardLabel.Should().Be("Contest reward");
        result.Details.Should().Contain("Contest reward");
    }

    [Test]
    public async Task CouponOnlyPeriod_NotCreditedByTheListener_StaysOutOfTheForagingTask()
    {
        // DetectedTask is the listener's decision (Galatea Contests sink here): the task must not guess
        var result = await new FigForagingTask().Execute(MakeParams(FigWork(Start), HandIn(Start.AddMinutes(12), "Galatea Contests")));

        result.ProfitPerHour.Should().Be(6_000_000);
        result.Breakdown.Drops.Should().NotContain(d => d.ItemTag == "AGATHA_COUPON");
    }

    [Test]
    public async Task CreditedRewardThatWorkFellOutOfTheWindow_CountsForTheEarnDuration()
    {
        var result = await new FigForagingTask().Execute(MakeParams(HandIn(Start, "Fig Foraging")));

        result.Breakdown.TrackedHours.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Test]
    public async Task CouponsInAMixedPeriod_AreLabelledToo()
    {
        var mixed = MakePeriod("Murkwater Loch", Start, TimeSpan.FromMinutes(10), 1_366_330,
            new() { { "FIG_LOG", 10000 }, { "AGATHA_COUPON", 30 } }, "Fig Foraging");

        var result = await new FigForagingTask().Execute(MakeParams(mixed));

        result.Breakdown.Drops.Single(d => d.ItemTag == "AGATHA_COUPON").Kind.Should().Be("late_reward");
        result.Breakdown.TrackedHours.Should().BeApproximately(10.0 / 60, 1e-9);
    }

    [Test]
    public void LateRewardJson_IsAdditive()
    {
        var plain = JsonConvert.SerializeObject(new DropInfo { ItemTag = "FIG_LOG" });
        var late = JsonConvert.SerializeObject(new DropInfo { ItemTag = "AGATHA_COUPON", Kind = "late_reward", Label = "Contest reward" });

        plain.Should().Contain("\"Kind\":null").And.Contain("\"ItemTag\":\"FIG_LOG\"");
        late.Should().Contain("\"Kind\":\"late_reward\"").And.Contain("\"Label\":\"Contest reward\"");
    }

    // ── attribution rule ──

    [Test]
    public void Resolve_CreditsTheEarnerOnTheSameIslandWithinTheWindow()
    {
        var fig = new FigForagingTask();
        var items = new Dictionary<string, int> { { "AGATHA_COUPON", 30 }, { "STARLYN_PRIZE", 1 } };

        LateRewardAttribution.Resolve(fig, Start, "Moonglade Marsh", "Murkwater Loch", items, Start.AddMinutes(25))
            .Should().BeSameAs(fig);
    }

    [Test]
    public void Resolve_IsNullForAnotherIsland_ALateHandIn_OrAMixedPeriod()
    {
        var fig = new FigForagingTask();
        var coupons = new Dictionary<string, int> { { "AGATHA_COUPON", 30 } };

        // Miria is on Torrhus, the foraging was on Moonglade
        LateRewardAttribution.Resolve(fig, Start, "Moonglade Marsh", "Miria's Hut", new Dictionary<string, int> { { "MIRIA_COUPON", 3 } }, Start.AddMinutes(1))
            .Should().BeNull();
        // more than two contest durations later
        LateRewardAttribution.Resolve(fig, Start, "Moonglade Marsh", "Murkwater Loch", coupons, Start.AddMinutes(41)).Should().BeNull();
        // real items besides the reward: classified by those, not by this rule
        LateRewardAttribution.Resolve(fig, Start, "Moonglade Marsh", "Murkwater Loch",
            new Dictionary<string, int> { { "AGATHA_COUPON", 30 }, { "FIG_LOG", 5 } }, Start.AddMinutes(1)).Should().BeNull();
        // nothing remembered / a task without a late reward
        LateRewardAttribution.Resolve(null, default, null, "Murkwater Loch", coupons, Start).Should().BeNull();
        LateRewardAttribution.Resolve(new OakForagingTask(), Start, "Forest", "Murkwater Loch", coupons, Start).Should().BeNull();
    }

    [Test]
    public void GalateaForagingAndHuntingTasks_DeclareTheContestRewardOfTheirOwnIsland()
    {
        new FigForagingTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("AGATHA_COUPON", "STARLYN_PRIZE");
        new MangroveForagingTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("AGATHA_COUPON", "STARLYN_PRIZE");
        new HelixForagingTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("MIRIA_COUPON", "MIRIA_PRIZE");
        new HoneycombGatheringTask().LateRewardDeclaration.Tags.Should().Contain("MIRIA_COUPON");
        new ChillHuntingTask().LateRewardDeclaration.Tags.Should().Contain("AGATHA_COUPON");
        new TikiHuntingTask().LateRewardDeclaration.Tags.Should().Contain("MIRIA_COUPON");
        new FigForagingTask().LateRewardDeclaration.EarnDuration.Should().Be(TimeSpan.FromMinutes(20));
        new OakForagingTask().LateRewardDeclaration.Should().BeNull("the Hub has no contests");
    }

    // ── Galatea fishing and diving (island trackers) ──

    private static Period FishingWork(DateTime start)
        => MakePeriod("Driptoad Delve", start, TimeSpan.FromMinutes(10), 600_000, new() { { "CLAY_BALL", 3000 } }, null);

    private static Period DivingWork(DateTime start)
        => MakePeriod("Murkwater Depths", start, TimeSpan.FromMinutes(10), 600_000, new() { { "CLAY_BALL", 3000 } }, null);

    [TestCase(typeof(GalateaFishingTask), "Driptoad Delve", "GalateaFishing")]
    [TestCase(typeof(GalateaDivingTask), "Murkwater Depths", "GalateaDiving")]
    public async Task CouponOnlyPeriodAfterGalateaFishingOrDiving_IsCreditedWithoutHandInTime(Type taskType, string zone, string name)
    {
        var task = (ProfitTask)Activator.CreateInstance(taskType);
        var work = MakePeriod(zone, Start, TimeSpan.FromMinutes(10), 600_000, new() { { "CLAY_BALL", 3000 } });
        var handIn = HandIn(Start.AddMinutes(12), name);
        task.RegistryName.Should().Be(name);
        task.IsAttributedLateReward(handIn).Should().BeTrue();

        var result = await task.Execute(MakeParams(work, handIn));

        // (600,000 + 366,330) over the 10 minutes of work only, not 16.6 more seconds
        result.ProfitPerHour.Should().Be((int)(966_330 / (10.0 / 60)));
        result.Breakdown.TrackedHours.Should().BeApproximately(10.0 / 60, 1e-9);
        var coupon = result.Breakdown.Drops.Single();
        coupon.ItemTag.Should().Be("AGATHA_COUPON");
        coupon.Kind.Should().Be("late_reward");
        coupon.Label.Should().Be("Contest reward");
        coupon.ContributionPerHour.Should().BeApproximately(30 * 12211 / (10.0 / 60), 1);
        result.Breakdown.LateRewardPerHour.Should().BeApproximately(coupon.ContributionPerHour, 1e-6);
        result.Breakdown.LateRewardLabel.Should().Be("Contest reward");
        result.Details.Should().Contain("AGATHA_COUPON");
    }

    [Test]
    public async Task GalateaFishing_CouponPeriodNotCreditedByTheListener_AddsNothing()
    {
        // coupons that fell into the orphan sink are not guessed into the fishing task
        var result = await new GalateaFishingTask().Execute(MakeParams(FishingWork(Start), HandIn(Start.AddMinutes(12), "Galatea Contests")));

        result.ProfitPerHour.Should().Be(3_600_000);
        result.Breakdown.Drops.Should().BeNullOrEmpty();
        result.Breakdown.LateRewardPerHour.Should().BeNull();
    }

    [Test]
    public async Task IslandTaskWithoutALateRewardDeclaration_IsUnchanged()
    {
        var task = new GalateaTask();
        task.LateRewardDeclaration.Should().BeNull();

        var result = await task.Execute(MakeParams(MakePeriod("Tangleburg", Start, TimeSpan.FromMinutes(10), 600_000, new() { { "CLAY_BALL", 3000 } })));

        result.ProfitPerHour.Should().Be(3_600_000);
        result.Breakdown.Drops.Should().BeNullOrEmpty();
    }

    [Test]
    public void GalateaFishingAndDiving_DeclareTheMoonglideContestReward()
    {
        new GalateaFishingTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("AGATHA_COUPON", "STARLYN_PRIZE");
        new GalateaDivingTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("AGATHA_COUPON", "STARLYN_PRIZE");
        new GalateaFishingMethodTask().LateRewardDeclaration.Should().NotBeNull("the classified Galatea fishing method earns the contest too");
        new GalateaTask().LateRewardDeclaration.Should().BeNull();
    }

    [Test]
    public void Resolve_CreditsGalateaFishingAndDivingTrackers()
    {
        var coupons = new Dictionary<string, int> { { "AGATHA_COUPON", 30 } };
        var fishing = new GalateaFishingTask();
        var diving = new GalateaDivingTask();

        LateRewardAttribution.Resolve(fishing, Start, "Driptoad Delve", "Murkwater Loch", coupons, Start.AddMinutes(12)).Should().BeSameAs(fishing);
        LateRewardAttribution.Resolve(diving, Start, "Murkwater Depths", "Murkwater Loch", coupons, Start.AddMinutes(12)).Should().BeSameAs(diving);
    }

    [Test]
    public void FindIslandEarner_PicksTheMostSpecificTrackerForTheZone_NeverForAHandIn()
    {
        var registry = new TaskRegistry();

        // Driptoad Delve is in both lists, fishing is the narrower one
        Coflnet.Sky.PlayerState.Services.CollectionListener.FindIslandEarner(registry, FishingWork(Start)).Should().BeOfType<GalateaFishingTask>();
        Coflnet.Sky.PlayerState.Services.CollectionListener.FindIslandEarner(registry, DivingWork(Start)).Should().BeOfType<GalateaDivingTask>();
        // the hand-in itself and work off the water are nobody's earner
        Coflnet.Sky.PlayerState.Services.CollectionListener.FindIslandEarner(registry, HandIn(Start)).Should().BeNull();
        Coflnet.Sky.PlayerState.Services.CollectionListener.FindIslandEarner(registry, FigWork(Start)).Should().BeNull();
    }

    // ── slayers ──

    [Test]
    public async Task SlayerBossDropOnlyPeriod_IsCreditedToTheSlayerTask_ForAtLeastTheEarnDuration()
    {
        var registry = new TaskRegistry();
        var classifier = new TaskClassifier(registry);
        var items = new Dictionary<string, int> { { "NULL_SPHERE", 2 } };
        classifier.Classify("Dragon's Nest", items, 10).TaskName.Should().Be("T4 Voidglooms");

        // 20 seconds for 1M coins used to be 180M/h; the boss drop counts for the 5 minute floor: 12M/h
        var boss = MakePeriod("Dragon's Nest", Start, TimeSpan.FromSeconds(20), 1_000_000, items, "T4 Voidglooms");
        var result = await new T4VoidgloomsTask().Execute(MakeParams(boss));

        result.ProfitPerHour.Should().Be(12_000_000);
        var drop = result.Breakdown.Drops.Single(d => d.ItemTag == "NULL_SPHERE");
        drop.Kind.Should().Be("late_reward");
        drop.Label.Should().Be("Boss drop");
        result.Breakdown.LateRewardLabel.Should().Be("Boss drop");
    }

    [Test]
    public async Task SlayerBossDrop_AfterMobKills_DoesNotCountTheMobTimeTwice()
    {
        var mobs = MakePeriod("Dragon's Nest", Start, TimeSpan.FromMinutes(4), 40_000, new() { { "SUMMONING_EYE", 4 } }, "T4 Voidglooms");
        var boss = MakePeriod("Dragon's Nest", Start.AddMinutes(4), TimeSpan.FromSeconds(20), 1_000_000, new() { { "NULL_SPHERE", 2 } }, "T4 Voidglooms");

        var result = await new T4VoidgloomsTask().Execute(MakeParams(mobs, boss));

        // the floor reaches back no further than the end of the mob period: 4 min + 20 s, not 4 + 5 min
        result.Breakdown.TrackedHours.Should().BeApproximately((4 * 60 + 20) / 3600.0, 1e-6);
    }

    [Test]
    public void BossOnlyTags_AreDeclaredOnlyWhereTheCodeSaysBossOnly()
    {
        new T4VoidgloomsTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("NULL_SPHERE");
        new TarantulaSlayerTask().LateRewardDeclaration.Tags.Should().BeEquivalentTo("TARANTULA_WEB");
        new RevenantSlayerTask().LateRewardDeclaration.Should().BeNull("Revenant Flesh is not documented as boss-only");
        new SvenSlayerTask().LateRewardDeclaration.Should().BeNull();
    }
}
