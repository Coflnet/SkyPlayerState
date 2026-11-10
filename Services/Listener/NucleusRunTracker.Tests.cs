using System;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using MessagePack;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Crystal Nucleus run tracking from chat. The raw lines are copied from a real chat log of a full run (SkyHanni chat log
/// 2025-06-02, see <see cref="NucleusChatParser"/>); the mod strips the color codes before sending, older clients did not.
/// </summary>
public class NucleusRunTrackerTests
{
    private const string FoundFirstRaw = "§f                       §r§5§l✦ CRYSTAL FOUND §r§7(1§r§7/5§r§7)";
    private const string FoundThirdRaw = "§f                       §r§5§l✦ CRYSTAL FOUND §r§7(3§r§7/5§r§7)";
    private const string CrystalNameRaw = "§f                              §r§5Amethyst Crystal";
    private const string PlacedRaw = "§5§l✦ §r§dYou placed the §r§bSapphire Crystal§r§d!";
    private const string KeepExploringRaw = "  §r§dKeep exploring the §r§5Crystal Hollows §r§dto find the rest!";
    private const string BundleRaw = "  §r§5§lCRYSTAL NUCLEUS LOOT BUNDLE ";
    private const string RewardsRaw = "  §r§a§lREWARDS";
    private const string RewardLineRaw = "    §r§9❤ Fine Ruby Gemstone §r§8x2";
    private const string PickUpRaw = "§7Pick it up near the §r§5Nucleus Vault§r§7!";
    private static readonly DateTime T0 = new(2026, 11, 10, 21, 6, 43, DateTimeKind.Utc);

    private static string Stripped(string raw) => System.Text.RegularExpressions.Regex.Replace(raw, "§.", "");

    [TestCase(FoundFirstRaw, NucleusChatEvent.CrystalFound, 1)]
    [TestCase(FoundThirdRaw, NucleusChatEvent.CrystalFound, 3)]
    [TestCase(PlacedRaw, NucleusChatEvent.CrystalPlaced, 0)]
    [TestCase(BundleRaw, NucleusChatEvent.BundleClaimed, 0)]
    [TestCase(CrystalNameRaw, NucleusChatEvent.None, 0)]
    [TestCase(KeepExploringRaw, NucleusChatEvent.None, 0)]
    [TestCase(RewardsRaw, NucleusChatEvent.None, 0)]
    [TestCase(RewardLineRaw, NucleusChatEvent.None, 0)]
    [TestCase(PickUpRaw, NucleusChatEvent.None, 0)]
    public void ParsesTheRealLines_WithAndWithoutColorCodes(string raw, NucleusChatEvent expected, int count)
    {
        foreach (var line in new[] { raw, Stripped(raw) })
        {
            NucleusChatParser.Parse(line, out var found).Should().Be(expected, line);
            found.Should().Be(count);
        }
    }

    [TestCase("Party > Someone: ✦ CRYSTAL FOUND (1/5)")]
    [TestCase("[MVP+] Someone: CRYSTAL NUCLEUS LOOT BUNDLE")]
    [TestCase("Guild > Someone: ✦ You placed the Jade Crystal!")]
    [TestCase("✦ CRYSTAL FOUND (6/5)")]
    [TestCase("")]
    [TestCase(null)]
    public void PlayerChatIsNoEvent(string line)
        => NucleusChatParser.Parse(line, out _).Should().Be(NucleusChatEvent.None);

    private static ExtractedInfo Info(string profile = "Apple") => new() { CurrentProfileName = profile };

    private static void Chat(ExtractedInfo info, DateTime at, params string[] lines) => NucleusRunTracker.Apply(info, lines, at);

    [Test]
    public void FullRun_StartsAtTheFirstCrystal_AndTheClaimKeepsThatStart()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw, CrystalNameRaw);
        Chat(info, T0.AddMinutes(2), FoundThirdRaw);
        Chat(info, T0.AddMinutes(6), PlacedRaw, KeepExploringRaw);
        Chat(info, T0.AddMinutes(6.5), PlacedRaw, BundleRaw, RewardsRaw, RewardLineRaw, PickUpRaw);

        var run = info.NucleusRun;
        run.ClaimAt.Should().Be(T0.AddMinutes(6.5));
        run.ClaimedRunStart.Should().Be(T0);
        run.Start.Should().Be(default, "the claim ends the run");
        NucleusRunTracker.IsActive(run, "Apple", T0.AddMinutes(7)).Should().BeFalse();
    }

    [Test]
    public void FirstCrystalAfterAClaim_StartsTheNextRun()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(6), BundleRaw);
        Chat(info, T0.AddMinutes(9), FoundFirstRaw);

        info.NucleusRun.Start.Should().Be(T0.AddMinutes(9));
        info.NucleusRun.ClaimedRunStart.Should().Be(T0);
        NucleusRunTracker.IsActive(info.NucleusRun, "Apple", T0.AddMinutes(10)).Should().BeTrue();
    }

    [Test]
    public void FirstCrystalWithoutAClaim_RestartsTheRun()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(20), FoundFirstRaw);

        info.NucleusRun.Start.Should().Be(T0.AddMinutes(20), "a missed claim must not stretch the previous run");
    }

    [Test]
    public void ProfileSwitch_AbandonsTheRun()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        info.CurrentProfileName = "Banana";

        NucleusRunTracker.IsActive(info.NucleusRun, info.CurrentProfileName, T0.AddMinutes(5)).Should().BeFalse();
        Chat(info, T0.AddMinutes(10), BundleRaw);
        info.NucleusRun.ClaimedRunStart.Should().Be(default, "the run was on another profile");
    }

    [Test]
    public void UnknownProfileName_IsNoSwitch()
    {
        var info = Info(profile: null);
        Chat(info, T0, FoundFirstRaw);
        info.CurrentProfileName = "Apple";

        NucleusRunTracker.IsActive(info.NucleusRun, "Apple", T0.AddMinutes(5)).Should().BeTrue();
    }

    [Test]
    public void GapWithoutCrystals_AbandonsTheRun()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        var afterGap = T0 + NucleusRunTracker.MaxEvidenceGap + TimeSpan.FromMinutes(1);

        NucleusRunTracker.IsActive(info.NucleusRun, "Apple", afterGap).Should().BeFalse();
        Chat(info, afterGap, FoundThirdRaw);
        info.NucleusRun.Start.Should().Be(afterGap, "a crystal after the gap starts a new run (lower bound)");
    }

    [Test]
    public void CrystalsKeepARunAlive_ButNotLongerThanTheMaxRunLength()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(50), PlacedRaw);
        Chat(info, T0.AddMinutes(100), PlacedRaw);

        NucleusRunTracker.IsActive(info.NucleusRun, "Apple", T0.AddMinutes(100)).Should().BeTrue("placed crystals are evidence");
        Chat(info, T0.AddMinutes(150), BundleRaw);
        info.NucleusRun.ClaimedRunStart.Should().Be(default, "a 2.5 hour run is other play with a few crystals");
    }

    [Test]
    public void ClaimWithoutAKnownStart_KeepsNoStart()
    {
        var info = Info();
        Chat(info, T0, PlacedRaw, PlacedRaw, BundleRaw);

        info.NucleusRun.ClaimAt.Should().Be(T0);
        info.NucleusRun.ClaimedRunStart.Should().Be(default, "placing never starts a run, the attribution falls back to a bound");
    }

    [Test]
    public void LaggedClaim_AnchorsOnTheProcessingTime()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        NucleusRunTracker.Apply(info, [BundleRaw], T0.AddMinutes(6), T0.AddMinutes(9));

        info.NucleusRun.ClaimAt.Should().Be(T0.AddMinutes(6));
        info.NucleusRun.ClaimProcessedAt.Should().Be(T0.AddMinutes(9));
    }

    [Test]
    public void SecondClaimBeforeTheClose_CountsTwoRunsAndKeepsTheFirstStart()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(8), BundleRaw);
        Chat(info, T0.AddMinutes(9), FoundFirstRaw);
        Chat(info, T0.AddMinutes(16), BundleRaw);

        info.NucleusRun.PendingClaims.Should().Be(2);
        info.NucleusRun.ClaimedRunStart.Should().Be(T0);
        info.NucleusRun.ClaimProcessedAt.Should().Be(T0.AddMinutes(16));
    }

    [Test]
    public void RepeatedBundleLine_DoesNotEraseTheClaimedStart()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(8), BundleRaw);
        Chat(info, T0.AddMinutes(8.2), BundleRaw);

        info.NucleusRun.ClaimAt.Should().Be(T0.AddMinutes(8));
        info.NucleusRun.ClaimedRunStart.Should().Be(T0);
    }

    [Test]
    public void OtherChat_LeavesTheStateUntouched()
    {
        var info = Info();
        Chat(info, T0, "You collected 3 items", PickUpRaw);

        info.NucleusRun.Should().BeNull();
    }

    [Test]
    public void RunStateSurvivesARoundTripAndCopy()
    {
        var info = Info();
        Chat(info, T0, FoundFirstRaw);
        Chat(info, T0.AddMinutes(6), BundleRaw);
        Chat(info, T0.AddMinutes(9), FoundThirdRaw);

        info.NucleusRun.Held = [new TaskSession { DetectedTask = "Jade Mining", StartTime = T0, LastItemTime = T0.AddMinutes(4), Items = new() { ["ROUGH_JADE_GEM"] = 300 } }];
        info.NucleusRun.BundleSeen = true;

        var copy = MessagePackSerializer.Deserialize<ExtractedInfo>(MessagePackSerializer.Serialize(info));
        var constructed = new ExtractedInfo(info);

        foreach (var run in new[] { copy.NucleusRun, constructed.NucleusRun })
        {
            run.Start.Should().Be(T0.AddMinutes(9));
            run.Profile.Should().Be("Apple");
            run.LastEvidenceAt.Should().Be(T0.AddMinutes(9));
            run.CrystalsFound.Should().Be(3);
            run.ClaimAt.Should().Be(T0.AddMinutes(6));
            run.ClaimedRunStart.Should().Be(T0);
            run.ClaimPending.Should().BeTrue();
            run.ClaimProcessedAt.Should().Be(T0.AddMinutes(6));
            run.PendingClaims.Should().Be(1);
            run.BundleSeen.Should().BeTrue();
            run.Held.Should().ContainSingle().Which.Items.Should().Contain("ROUGH_JADE_GEM", 300);
        }
        constructed.NucleusRun.Should().NotBeSameAs(info.NucleusRun);
        constructed.NucleusRun.Held.Should().NotBeSameAs(info.NucleusRun.Held);
        constructed.NucleusRun.Held[0].Should().NotBeSameAs(info.NucleusRun.Held[0]);
    }
}
