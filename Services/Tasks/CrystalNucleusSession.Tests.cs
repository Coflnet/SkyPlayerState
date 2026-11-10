using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using MessagePack;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// A Crystal Nucleus run (the hunt for five crystals across the Crystal Hollows, ended by the loot bundle claim) becomes one
/// Crystal Nucleus period: <see cref="NucleusRunRouter"/> holds the run's Crystal Hollows sessions and merges them on the
/// claim. Regression: the hunt used to be split into other tasks and the 1-3 minute claim visit was too short to classify,
/// while a Divan Fragment selected a "nucleus" period by its outcome (46M/h).
/// Fragments are fed the way CollectionListener.AccumulateSession does it; chat goes through <see cref="NucleusRunTracker"/>.
/// </summary>
public class CrystalNucleusSessionTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly NucleusRunRouter Router = new(new TaskSessionService(new TaskClassifier(Registry)));
    private const string Player = "00000000000000000000000000000001";
    private static readonly DateTime T0 = new(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);
    // rising gem prices make every zone of the hunt a new task, so the hunt flushes several sessions
    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ROUGH_JADE_GEM"] = 1, ["ROUGH_AMBER_GEM"] = 3, ["ROUGH_AMETHYST_GEM"] = 9, ["ROUGH_SAPPHIRE_GEM"] = 27,
        ["ROUGH_TOPAZ_GEM"] = 81, ["DIVAN_FRAGMENT"] = 1_300_000, ["FLAWED_RUBY_GEM"] = 200, ["GEMSTONE_MIXTURE"] = 150_000
    };

    // real lines (color codes stripped by the mod), see NucleusRunTrackerTests
    private const string Found1 = "                       ✦ CRYSTAL FOUND (1/5)";
    private const string Found5 = "                       ✦ CRYSTAL FOUND (5/5)";
    private const string Placed = "✦ You placed the Sapphire Crystal!";
    private const string Bundle = "  CRYSTAL NUCLEUS LOOT BUNDLE ";

    private static readonly (string Zone, string Gem, int From)[] Hunt =
    [
        ("Jungle Temple", "ROUGH_JADE_GEM", 0), ("Goblin Queen's Den", "ROUGH_AMBER_GEM", 4),
        ("Mines of Divan", "ROUGH_AMETHYST_GEM", 8), ("Lost Precursor City", "ROUGH_SAPPHIRE_GEM", 12),
        ("Khazad-dûm", "ROUGH_TOPAZ_GEM", 16)
    ];
    private static readonly Dictionary<string, int> BundleLoot = new()
    {
        ["DIVAN_FRAGMENT"] = 1, ["FLAWED_RUBY_GEM"] = 12, ["GEMSTONE_MIXTURE"] = 1, ["JUNGLE_KEY"] = -1
    };

    private sealed class PlayerFeed(ExtractedInfo info = null)
    {
        public readonly ExtractedInfo Info = info ?? new() { CurrentProfileName = "Apple" };
        public readonly List<Period> Folded = [];

        public void Feed(string zone, Dictionary<string, int> items, double from, double to)
        {
            var fragment = Fragment(zone, items, from, to);
            Folded.AddRange(Router.Accumulate(Info, Player, fragment, null, Prices, fragment.EndTime));
        }

        public void FeedHunt(IEnumerable<(string Zone, string Gem, int From)> zones)
        {
            foreach (var (zone, gem, from) in zones)
                Feed(zone, new() { [gem] = 300 }, from, from + 4);
        }

        public void Idle(string zone, double at) => Feed(zone, new(), at, at);

        public void Chat(double at, params string[] lines) => NucleusRunTracker.Apply(Info, lines, T0.AddMinutes(at));

        /// <summary>Chat received at <paramref name="receivedAt"/> but processed (Kafka lag) at <paramref name="processedAt"/>.</summary>
        public void LaggedChat(double receivedAt, double processedAt, params string[] lines)
            => NucleusRunTracker.Apply(Info, lines, T0.AddMinutes(receivedAt), T0.AddMinutes(processedAt));

        public Period Nucleus => Folded.SingleOrDefault(p => p.DetectedTask == NucleusMiningTask.TaskName);
        public List<Period> Others => Folded.Where(p => p.DetectedTask != NucleusMiningTask.TaskName).ToList();
    }

    private static Period Fragment(string zone, Dictionary<string, int> items, double from, double to) => new()
    {
        PlayerUuid = Player, Location = zone, Server = "mini1", ItemsCollected = items,
        StartTime = T0.AddMinutes(from), EndTime = T0.AddMinutes(to)
    };

    /// <summary>The hunt through five zones, the claim at minute 21, the bundle picked up at the nucleus, then on to the Jungle.</summary>
    private static void PlayHunt(PlayerFeed player, bool chat)
    {
        if (chat)
            player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        if (chat)
            player.Chat(21, Found5, Placed, Placed, Placed, Placed, Placed, Bundle);
        player.Feed("Crystal Nucleus", new(BundleLoot), 20, 22);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26);
        player.Idle("Jungle", 40);
    }

    /// <summary>Hours the folder counts for the classified periods (Fold uses <see cref="MethodTask.EffectiveDuration(Period)"/>).</summary>
    private static double FoldedHours(IEnumerable<Period> periods)
        => periods.Where(p => p.DetectedTask != null)
            .Sum(p => ((MethodTask)Registry.GetByName(p.DetectedTask)).EffectiveDuration(p).TotalHours);

    private static double WallHours(IEnumerable<Period> periods) => periods.Sum(p => (p.EndTime - p.StartTime).TotalHours);

    private static List<string> Describe(IEnumerable<Period> periods)
        => periods.Select(p => $"{p.DetectedTask}|{p.StartTime:O}|{p.EndTime:O}|"
            + string.Join(",", p.ItemsCollected.OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value}"))).ToList();

    [Test]
    public void FullRunAcrossZones_IsOneNucleusPeriodSpanningTheHunt_WithTheBundleLoot()
    {
        var player = new PlayerFeed();
        PlayHunt(player, chat: true);

        var run = player.Nucleus;
        run.Should().NotBeNull();
        run.StartTime.Should().Be(T0, "the session open at the first crystal is part of the hunt");
        run.EndTime.Should().Be(T0.AddMinutes(22));
        run.ItemsCollected.Should().Contain(PseudoItems.NUCLEUS_RUN, 1).And.Contain("DIVAN_FRAGMENT", 1)
            .And.Contain("ROUGH_JADE_GEM", 300).And.Contain("ROUGH_TOPAZ_GEM", 300).And.Contain("FLAWED_RUBY_GEM", 12);
        run.ZoneSeconds.Keys.Should().Contain(["Jungle Temple", "Goblin Queen's Den", "Mines of Divan", "Lost Precursor City", "Khazad-dûm", "Crystal Nucleus"]);
        player.Info.NucleusRun.Held.Should().BeNull();
        player.Info.NucleusRun.ClaimPending.Should().BeFalse();
    }

    [Test]
    public void FullRun_KeepsTheBundleLootAndTheRunCosts()
    {
        var player = new PlayerFeed();
        PlayHunt(player, chat: true);

        var nucleus = (MethodTask)Registry.GetByName(NucleusMiningTask.TaskName);
        TaskPeriodFolder.FilterOwnedItems(player.Nucleus.ItemsCollected, nucleus)
            .Should().Contain("DIVAN_FRAGMENT", 1).And.Contain("GEMSTONE_MIXTURE", 1).And.Contain("JUNGLE_KEY", -1);
    }

    [Test]
    public void FullRun_NoHuntTimeIsCountedTwice_AndNoneIsLost()
    {
        var withRun = new PlayerFeed();
        PlayHunt(withRun, chat: true);
        var withoutRun = new PlayerFeed();
        PlayHunt(withoutRun, chat: false);

        withoutRun.Folded.Should().OnlyContain(p => p.DetectedTask != null, "every session of the hunt classifies");
        withRun.Folded.Should().OnlyContain(p => p.DetectedTask != null);
        FoldedHours(withRun.Folded).Should().BeApproximately(FoldedHours(withoutRun.Folded), 1e-9, "the hunt's time only moves to Crystal Nucleus");
        WallHours(withRun.Folded).Should().BeApproximately(WallHours(withoutRun.Folded), 1e-9);
        var run = withRun.Nucleus;
        withRun.Others.Should().OnlyContain(p => p.StartTime >= run.EndTime || p.EndTime <= run.StartTime);
        withoutRun.Nucleus.Should().BeNull();
    }

    [Test]
    public void ClaimWithoutAKnownStart_TakesOnlyTheOpenSession_AtLeastTheMinimumDuration()
    {
        var player = new PlayerFeed();
        player.FeedHunt(Hunt);
        player.Chat(21, Placed, Placed, Bundle);
        player.Feed("Crystal Nucleus", new(BundleLoot), 20, 22);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26);

        var run = player.Nucleus;
        run.Should().NotBeNull();
        run.StartTime.Should().Be(T0.AddMinutes(16), "only the session open at the claim, the earlier hunt was folded already");
        run.ItemsCollected.Should().Contain("ROUGH_TOPAZ_GEM", 300).And.Contain("DIVAN_FRAGMENT", 1).And.NotContainKey("ROUGH_JADE_GEM");
        ((MethodTask)Registry.GetByName(NucleusMiningTask.TaskName)).EffectiveDuration(run).Should().Be(TimeSpan.FromMinutes(10));
        player.Others.Should().HaveCount(4, "the hunt before the open session keeps its own tasks")
            .And.OnlyContain(p => p.DetectedTask != null && p.EndTime <= run.StartTime);
    }

    [Test]
    public void DivanFragmentWithoutARun_NeverSelectsCrystalNucleus_AndIsNotTheOtherTasksLoot()
    {
        var player = new PlayerFeed();
        PlayHunt(player, chat: false);

        player.Nucleus.Should().BeNull();
        var withFragment = player.Folded.Single(p => p.ItemsCollected.ContainsKey("DIVAN_FRAGMENT"));
        var task = (MethodTask)Registry.GetByName(withFragment.DetectedTask);
        TaskPeriodFolder.FilterOwnedItems(withFragment.ItemsCollected, task).Should().NotContainKey("DIVAN_FRAGMENT");
    }

    [Test]
    public void WithoutRunEvidence_NothingIsHeld_AndEverySessionFoldsAsBefore()
    {
        var plain = new TaskSessionService(new TaskClassifier(Registry));
        var reference = new ExtractedInfo { CurrentProfileName = "Apple" };
        var fragments = Hunt.Select(h => Fragment(h.Zone, new() { [h.Gem] = 300 }, h.From, h.From + 4))
            .Append(Fragment("Crystal Nucleus", new(BundleLoot), 20, 22))
            .Append(Fragment("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26))
            .Append(Fragment("Jungle", new(), 40, 40));
        var expected = fragments.Select(f => plain.Accumulate(reference, Player, f, null, Prices, f.EndTime)).Where(p => p != null);

        var player = new PlayerFeed();
        PlayHunt(player, chat: false);

        Describe(player.Folded).Should().Equal(Describe(expected));
        player.Info.NucleusRun.Should().BeNull("no nucleus chat line arrives until the allowlist ships");
    }

    [Test]
    public void AClosedClaimWithoutANewRun_HoldsNothing()
    {
        var player = new PlayerFeed();
        player.Info.NucleusRun = new NucleusRunState { ClaimAt = T0.AddHours(-3), ClaimedRunStart = T0.AddHours(-3.2) };
        PlayHunt(player, chat: false);
        var reference = new PlayerFeed();
        PlayHunt(reference, chat: false);

        player.Info.NucleusRun.Held.Should().BeNull();
        Describe(player.Folded).Should().Equal(Describe(reference.Folded));
    }

    [Test]
    public void AbandonedRun_FoldsItsHeldSessionsAsTheirOwnTasks()
    {
        var player = new PlayerFeed();
        var reference = new PlayerFeed();
        player.Chat(1, Found1);
        foreach (var feed in new[] { player, reference })
        {
            feed.FeedHunt(Hunt);
            feed.Idle("Jungle", 1 + 61);
        }

        player.Info.NucleusRun.Held.Should().BeNull("the run lapsed an hour after its last crystal");
        Describe(player.Folded.OrderBy(p => p.StartTime)).Should().Equal(Describe(reference.Folded.OrderBy(p => p.StartTime)));
        player.Folded.Should().HaveCount(5);
    }

    [Test]
    public void ProfileSwitchMidRun_FoldsTheHeldSessionsAsTheirOwnTasks()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt.Take(2));
        player.Info.NucleusRun.Held.Should().HaveCount(1);

        player.Info.CurrentProfileName = "Banana";
        player.FeedHunt(Hunt.Skip(2).Take(1));

        player.Info.NucleusRun.Held.Should().BeNull();
        player.Folded.Select(p => p.DetectedTask).Should().Equal("Jade Mining", "Amber Mining");
    }

    [Test]
    public void SessionsOutsideTheHollows_AreNeverHeld()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.Feed("Coal Mine", new() { ["COAL"] = 900 }, 0, 4);
        player.Idle("Coal Mine", 10);

        player.Folded.Should().ContainSingle().Which.DetectedTask.Should().Be("Coal Mining");
        player.Info.NucleusRun.Held.Should().BeNull();
    }

    [Test]
    public void HeldSessionsAreCapped_TheOldestFoldAsTheirOwnTasks()
    {
        const int sessions = NucleusRunRouter.MaxHeldSessions + 5;
        var player = new PlayerFeed();
        player.Chat(0, Found1);
        for (var i = 0; i < sessions; i++)
        {
            player.Chat(i * 4, Placed);
            // every fragment is worth more than the session before it, so each one is a new task
            var (zone, gem, _) = Hunt[i % Hunt.Length];
            player.Feed(zone, new() { [gem] = 300 * (int)Math.Pow(100, i / Hunt.Length) }, i * 4, i * 4 + 4);
        }

        var held = player.Info.NucleusRun.Held;
        held.Should().HaveCount(NucleusRunRouter.MaxHeldSessions);
        held.Sum(s => s.Items.Count).Should().BeLessThanOrEqualTo(NucleusRunRouter.MaxHeldItemEntries);
        (player.Folded.Count + held.Count + 1).Should().Be(sessions, "no session is dropped, the last one is still open");
        player.Folded.Should().OnlyContain(p => p.DetectedTask != null && p.DetectedTask != NucleusMiningTask.TaskName);
        player.Folded.Max(p => p.EndTime).Should().BeOnOrBefore(held.Min(s => s.StartTime), "the oldest are folded first");
    }

    [Test]
    public void ARunSurvivesAPodMove_ThroughThePersistedState()
    {
        var before = new PlayerFeed();
        before.Chat(1, Found1);
        before.FeedHunt(Hunt.Take(3));
        before.Info.NucleusRun.Held.Should().NotBeEmpty();

        var after = new PlayerFeed(MessagePackSerializer.Deserialize<ExtractedInfo>(MessagePackSerializer.Serialize(before.Info)));
        after.FeedHunt(Hunt.Skip(3));
        after.Chat(21, Found5, Placed, Bundle);
        after.Feed("Crystal Nucleus", new(BundleLoot), 20, 22);
        after.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26);

        var straight = new PlayerFeed();
        PlayHunt(straight, chat: true);
        Describe([after.Nucleus]).Should().Equal(Describe([straight.Nucleus]));
    }

    [Test]
    public void CrystalNucleus_StaysHiddenUntilRunsAreVerified_ButItsRunsStillFold()
    {
        Registry.PublicMethodTasks.Select(TaskEstimator.GetTaskName).Should().NotContain(NucleusMiningTask.TaskName);
        ((MethodTask)Registry.GetByName(NucleusMiningTask.TaskName)).IsHidden.Should().BeTrue();
        var player = new PlayerFeed();
        PlayHunt(player, chat: true);
        player.Nucleus.Should().NotBeNull("the hidden task keeps collecting run data");
    }

    /// <summary>
    /// Regression: the claim was compared on the message clock (ReceivedAt) against fragments on the processing clock. A
    /// claim line processed late closed the run before its bundle fragment, and the Divan Fragment was lost.
    /// </summary>
    [Test]
    public void LaggedClaimLine_StillGetsItsBundleLoot()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        player.LaggedChat(receivedAt: 15, processedAt: 21, Found5, Placed, Bundle);
        player.Feed("Crystal Nucleus", new(BundleLoot), 20, 22);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26);

        player.Nucleus.Should().NotBeNull();
        player.Nucleus.ItemsCollected.Should().Contain("DIVAN_FRAGMENT", 1);
        player.Nucleus.EndTime.Should().Be(T0.AddMinutes(22));
    }

    /// <summary>Regression: a same-zone flush between the claim line and the vault pickup closed the run before the loot.</summary>
    [Test]
    public void BundleLootAfterAFlushBoundary_IsStillTheRunsLoot()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        player.Chat(21, Found5, Placed, Bundle);
        player.Feed("Crystal Nucleus", new() { ["JADE_CRYSTAL"] = -1, ["SAPPHIRE_CRYSTAL"] = -1 }, 20, 21.5);
        player.Feed("Crystal Nucleus", new(BundleLoot), 21.5, 22.5);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22.5, 26);

        player.Nucleus.Should().NotBeNull();
        player.Nucleus.ItemsCollected.Should().Contain("DIVAN_FRAGMENT", 1);
        player.Others.Should().NotContain(p => p.ItemsCollected.ContainsKey("DIVAN_FRAGMENT"));
    }

    [Test]
    public void WithoutBundleLoot_TheRunClosesAfterTheGrace()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        player.Chat(21, Bundle);
        player.Feed("Jungle", new() { ["ROUGH_TOPAZ_GEM"] = 300 }, 21.5, 22.5);
        player.Info.NucleusRun.ClaimPending.Should().BeTrue("a fragment within the grace still belongs to the run");
        player.Feed("Jungle", new() { ["ROUGH_TOPAZ_GEM"] = 300 }, 23, 27);

        player.Nucleus.EndTime.Should().Be(T0.AddMinutes(22.5));
        player.Info.NucleusRun.ClaimPending.Should().BeFalse();
    }

    [Test]
    public void AClaimThatNoLaterFragmentCloses_ClosesAfterMaxClaimPending()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        player.Chat(21, Bundle);
        // starts within the grace, but is only processed after MaxClaimPending
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 21.5, 21 + NucleusRunRouter.MaxClaimPending.TotalMinutes + 1);

        player.Nucleus.Should().NotBeNull();
        player.Nucleus.EndTime.Should().Be(T0.AddMinutes(20), "the late fragment is not part of the run");
        player.Info.NucleusRun.ClaimPending.Should().BeFalse();
    }

    [Test]
    public void HeldSessionsFromBeforeTheRunWindow_FoldAsTheirOwnTasks()
    {
        var player = new PlayerFeed();
        player.Chat(0, Found1);
        player.FeedHunt(Hunt.Take(2));
        player.Chat(40, Placed);
        player.Chat(60, Found1);
        player.FeedHunt([("Mines of Divan", "ROUGH_AMETHYST_GEM", 60)]);
        player.Info.NucleusRun.Held.Should().HaveCount(2, "the first run was restarted, its sessions are still held");
        player.Chat(65, Bundle);
        player.Feed("Crystal Nucleus", new(BundleLoot), 64, 66);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 66, 70);

        player.Others.Select(p => p.DetectedTask).Should().Equal("Jade Mining", "Amber Mining");
        player.Nucleus.StartTime.Should().Be(T0.AddMinutes(60));
        player.Nucleus.ItemsCollected.Should().ContainKey("ROUGH_AMETHYST_GEM").And.NotContainKey("ROUGH_JADE_GEM");
    }

    [Test]
    public void TwoClaimsBeforeTheClose_AreOnePeriodCountingTwoRuns()
    {
        var player = new PlayerFeed();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);
        player.Chat(21, Bundle);
        player.Chat(22.5, Bundle);
        player.Info.NucleusRun.ClaimedRunStart.Should().Be(T0.AddMinutes(1), "the first run's start is kept");
        player.Feed("Crystal Nucleus", new(BundleLoot), 20, 23);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 23, 27);

        player.Nucleus.ItemsCollected.Should().Contain(PseudoItems.NUCLEUS_RUN, 2);
    }

    private sealed class ThrowingSessions() : TaskSessionService(new TaskClassifier(Registry))
    {
        public override Period Accumulate(ExtractedInfo info, string playerUuid, Period fragment, string claimedTask,
            Dictionary<string, double> prices, DateTime now) => throw new InvalidOperationException("boom");
    }

    [Test]
    public void AFailingSessionMerge_StillReturnsTheReleasedSessions()
    {
        var info = new ExtractedInfo { CurrentProfileName = "Apple" };
        info.NucleusRun = new NucleusRunState
        {
            Held = [new() { DetectedTask = "Jade Mining", StartTime = T0, LastItemTime = T0.AddMinutes(4), Location = "Jungle Temple", Items = new() { ["ROUGH_JADE_GEM"] = 300 } }]
        };

        var periods = new NucleusRunRouter(new ThrowingSessions()).Accumulate(info, Player,
            Fragment("Jungle", new() { ["ROUGH_JADE_GEM"] = 1 }, 10, 12), null, Prices, T0.AddMinutes(12));

        periods.Should().ContainSingle().Which.DetectedTask.Should().Be("Jade Mining");
        info.NucleusRun.Held.Should().BeNull();
    }

    [Test]
    public void OnlyTheNewestHeldSessionKeepsItsGear_TheMergeStillComparesFingerprints()
    {
        var player = new PlayerFeed();
        player.Info.CurrentGear = new GearSnapshot();
        player.Chat(1, Found1);
        player.FeedHunt(Hunt);

        var held = player.Info.NucleusRun.Held;
        held.Should().HaveCount(4);
        held.Take(3).Should().OnlyContain(s => s.Gear == null && s.GearFingerprint != null);
        held.Last().Gear.Should().NotBeNull();

        player.Chat(21, Bundle);
        player.Feed("Crystal Nucleus", new(BundleLoot), 20, 22);
        player.Feed("Jungle", new() { ["ROUGH_JADE_GEM"] = 300 }, 22, 26);
        player.Nucleus.Gear.Should().NotBeNull();
        player.Nucleus.GearChanged.Should().BeFalse("one gear was worn throughout");
    }
}
