using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearSessionTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    internal static GearSnapshot Gear(GearPiece? tool = null, string evidence = GearToolEvidence.Optimal, string helmet = "FERMENTO_HELMET",
        string reforge = "mossy", GearPet? pet = null)
    {
        var gear = new GearSnapshot { ToolEvidence = evidence, ArmorAt = T0, ToolAt = T0, PetAt = T0, Pet = pet };
        gear.Slots[GearSnapshot.Helmet] = GearNormalizer.Normalize(GearTestItems.Armor(helmet, "HELMET", reforge))!;
        if (tool != null)
            gear.Slots[GearSnapshot.Tool] = tool;
        return gear;
    }

    private static GearPiece Hoe(long cultivated = 100) => GearNormalizer.Normalize(GearTestItems.Hoe(cultivated))!;

    [Test]
    public void SwappedArmorPiece_SetsGearChanged_AndFreezesTheGear()
    {
        var session = new TaskSession();
        GearSessionTracker.Track(session, Gear(Hoe()), T0);

        GearSessionTracker.Track(session, Gear(Hoe(), helmet: "HELIANTHUS_HELMET"), T0);
        GearSessionTracker.Track(session, Gear(Hoe()), T0);

        session.GearChanged.Should().BeTrue();
        session.Gear!.Slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
    }

    [Test]
    public void ArmorReadBeforeTheSessionStarted_IsReplacedWithoutAChange()
    {
        // wardrobe swap: the session starts with the mining armor read earlier, the next upload shows the farming armor
        var miningArmor = Gear(Hoe(), helmet: "DIVAN_HELMET");
        miningArmor.ArmorAt = T0.AddMinutes(-4);
        var session = new TaskSession { StartTime = T0.AddMinutes(-5), DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(session, miningArmor, T0);

        var farming = Gear(Hoe(), helmet: "FERMENTO_HELMET");
        farming.ArmorAt = T0.AddMinutes(1);
        GearSessionTracker.Track(session, farming, T0.AddMinutes(1));

        session.GearChanged.Should().BeFalse();
        session.GearSince.Should().Be(T0);
        session.Gear!.Slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");

        // once confirmed during the session, a swap counts
        GearSessionTracker.Track(session, Gear(Hoe(), helmet: "HELIANTHUS_HELMET"), T0.AddMinutes(2));
        session.GearChanged.Should().BeTrue();
    }

    [Test]
    public void RememberedHoe_InAMiningSession_IsIgnored_AndTheDrillTakesItsPlace()
    {
        // farming -> mining: the hoe is still the remembered tool when the mining session starts, then the drill is picked
        var session = new TaskSession { StartTime = T0, DetectedTask = "Mithril Mining" };
        GearSessionTracker.Track(session, Gear(Hoe()), T0);
        session.Gear!.Slots.Should().NotContainKey(GearSnapshot.Tool, "a farming tool is no mining tool");

        GearSessionTracker.Track(session, Gear(GearNormalizer.Normalize(GearTestItems.Drill())!), T0.AddMinutes(1));

        session.GearChanged.Should().BeFalse();
        session.Gear.ToolPiece!.Tag.Should().Be("MITHRIL_DRILL_2");
    }

    [Test]
    public void TabOnlyPet_WithAnotherTypeSpelling_IsNoChange()
    {
        var session = new TaskSession();
        GearSessionTracker.Track(session, Gear(Hoe(), pet: new GearPet { Type = "MOOSHROOM_COW", Tier = "LEGENDARY", Level = 100 }), T0);

        GearSessionTracker.Track(session, Gear(Hoe(), pet: new GearPet { Type = "MOOSHROOM_COW_X", Level = 100 }), T0);

        session.GearChanged.Should().BeFalse();
    }

    [Test]
    public void Reforged_SetsGearChanged()
    {
        var session = new TaskSession();
        GearSessionTracker.Track(session, Gear(Hoe()), T0);

        GearSessionTracker.Track(session, Gear(Hoe(), reforge: "bountiful"), T0);

        session.GearChanged.Should().BeTrue();
    }

    [Test]
    public void GrowingCounters_AreNoChange()
    {
        var session = new TaskSession { DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(session, Gear(Hoe(100)), T0);
        var start = session.GearFingerprint;

        GearSessionTracker.Track(session, Gear(Hoe(150)), T0);

        session.GearChanged.Should().BeFalse();
        session.Gear!.ToolPiece!.Counters["farmed_cultivating"].Should().Be(150);
        session.GearFingerprint.Should().Be(start, "counters are bucketed in the fingerprint");
    }

    [Test]
    public void NewlySeenParts_FillIn_AndAnAmbiguousToolKeepsTheKnownOne()
    {
        var session = new TaskSession { DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(session, Gear(Hoe()), T0);
        var later = Gear(evidence: GearToolEvidence.Ambiguous, pet: new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 100 });
        later.Slots[GearSnapshot.Necklace] = new GearPiece { Tag = "LOTUS_NECKLACE" };

        GearSessionTracker.Track(session, later, T0);

        session.GearChanged.Should().BeFalse();
        session.Gear!.ToolPiece!.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
        session.Gear.ToolEvidence.Should().Be(GearToolEvidence.Optimal);
        session.Gear.Slots.Should().ContainKey(GearSnapshot.Necklace);
        session.Gear.Pet!.Type.Should().Be("ELEPHANT");
    }

    [Test]
    public void APieceReadWithoutModifiers_IsNoChange()
    {
        var session = new TaskSession();
        GearSessionTracker.Track(session, Gear(Hoe()), T0);
        var menuRead = Gear(Hoe());
        menuRead.Slots[GearSnapshot.Helmet] = new GearPiece { Tag = "FERMENTO_HELMET", Rarity = "LEGENDARY" };

        GearSessionTracker.Track(session, menuRead, T0);

        session.GearChanged.Should().BeFalse();
        session.Gear!.Slots[GearSnapshot.Helmet].Reforge.Should().Be("mossy");
    }

    [Test]
    public void OtherKnownTool_OrOtherPet_SetsGearChanged()
    {
        // the hoe left the inventory, the session now farms with another one
        var tool = new TaskSession { DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(tool, Gear(Hoe()), T0);
        var other = GearNormalizer.Normalize(GearTestItems.FarmingTool("ADVANCED_GARDENING_HOE", "§7Farming Fortune: §6+15"))!;
        var swapped = Gear(other);
        swapped.ToolCandidates = [other];
        GearSessionTracker.Track(tool, swapped, T0);
        tool.GearChanged.Should().BeTrue();

        var pet = new TaskSession();
        GearSessionTracker.Track(pet, Gear(Hoe(), pet: new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY" }), T0);
        GearSessionTracker.Track(pet, Gear(Hoe(), pet: new GearPet { Type = "MOOSHROOM_COW", Tier = "LEGENDARY" }), T0);
        pet.GearChanged.Should().BeTrue();
    }

    /// <summary>Regression: a re-pick among the same candidates (e.g. a better hoe moved into the hotbar) set GearChanged.</summary>
    [Test]
    public void AnotherOptimalPick_WhileThePreviousToolIsStillCarried_IsNoChange()
    {
        var session = new TaskSession { DetectedTask = "Wheat Farming" };
        var generic = GearNormalizer.Normalize(GearTestItems.FarmingTool("ADVANCED_GARDENING_HOE", "§7Farming Fortune: §6+15"))!;
        var first = Gear();
        first.ToolCandidates = [generic];
        GearSessionTracker.Track(session, first, T0);
        session.Gear!.ToolPiece!.Tag.Should().Be("ADVANCED_GARDENING_HOE");

        var later = Gear();
        later.ToolCandidates = [generic, Hoe()];
        GearSessionTracker.Track(session, later, T0);

        session.GearChanged.Should().BeFalse();
        session.Gear.ToolPiece!.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
    }

    /// <summary>Regression: while the session's task was unknown, the snapshot's pick for another task became the session tool.</summary>
    [Test]
    public void WithoutASessionTask_NoToolIsKept()
    {
        var session = new TaskSession();

        GearSessionTracker.Track(session, Gear(Hoe()), T0);

        session.Gear!.Slots.Should().NotContainKey(GearSnapshot.Tool);
        session.Gear.ToolEvidence.Should().Be(GearToolEvidence.Ambiguous);
    }

    [Test]
    public void StatSamples_AddUpPerStat()
    {
        var session = new TaskSession();

        GearSessionTracker.AddStatSample(session, new Dictionary<string, double> { ["Farming Fortune"] = 1000 }, T0).Should().BeTrue();
        GearSessionTracker.AddStatSample(session, new Dictionary<string, double> { ["Farming Fortune"] = 1200, ["Pests Alive"] = 2 }, T0.AddMinutes(1));
        GearSessionTracker.AddStatSample(null, new Dictionary<string, double> { ["Farming Fortune"] = 50 }, T0).Should().BeFalse();

        session.StatSums["Farming Fortune"].Should().Be(2200);
        session.StatSamples["Farming Fortune"].Should().Be(2);
        session.StatSamples["Pests Alive"].Should().Be(1);
        session.LastStatSampleAt.Should().Be(T0.AddMinutes(1));
    }

    [Test]
    public void SessionTool_IsRePicked_ForTheSessionsTask()
    {
        // the reading was taken while the classifier still saw farming: the mining session uses its own optimal tool
        var session = new TaskSession { DetectedTask = "Mithril Mining" };
        var reading = Gear(Hoe());
        reading.ToolCandidates = [Hoe(), GearNormalizer.Normalize(GearTestItems.Drill())!];

        GearSessionTracker.Track(session, reading, T0);

        session.Gear!.ToolPiece!.Tag.Should().Be("MITHRIL_DRILL_2");
        session.Gear.ToolEvidence.Should().Be(GearToolEvidence.Optimal);
    }

    [Test]
    public void SessionService_CarriesGearAndSamplesIntoTheFlushedPeriod()
    {
        var service = new TaskSessionService(new TaskClassifier(new TaskRegistry()));
        var info = new ExtractedInfo { CurrentGear = Gear(Hoe()) };
        Period Fragment(int from, int to, Dictionary<string, int>? items) => new()
        {
            PlayerUuid = "p", Location = "Ruins", Server = "s", StartTime = T0.AddMinutes(from), EndTime = T0.AddMinutes(to),
            ItemsCollected = items ?? new()
        };
        GearSnapshot ReadAt(int minute, string helmet)
        {
            var gear = Gear(Hoe(), helmet: helmet);
            gear.ArmorAt = gear.ToolAt = T0.AddMinutes(minute);
            return gear;
        }

        service.Accumulate(info, "p", Fragment(0, 5, new() { ["X"] = 5 }), null, null, T0.AddMinutes(5));
        info.CurrentSession!.GearSince.Should().Be(T0.AddMinutes(5));
        info.CurrentGear = ReadAt(6, "FERMENTO_HELMET");
        service.Accumulate(info, "p", Fragment(5, 7, new() { ["X"] = 5 }), null, null, T0.AddMinutes(7));
        GearSessionTracker.AddStatSample(info.CurrentSession, new Dictionary<string, double> { ["Farming Fortune"] = 900 }, T0.AddMinutes(7));
        info.CurrentGear = ReadAt(8, "HELIANTHUS_HELMET");
        service.Accumulate(info, "p", Fragment(7, 10, new() { ["X"] = 5 }), null, null, T0.AddMinutes(10));
        var flush = service.Accumulate(info, "p", Fragment(10, 10, null), null, null, T0.AddMinutes(20));

        flush.Should().NotBeNull();
        flush.Gear!.Slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
        flush.GearChanged.Should().BeTrue();
        flush.StatSums["Farming Fortune"].Should().Be(900);
        flush.StatSamples["Farming Fortune"].Should().Be(1);
    }

    [Test]
    public void SessionService_AGearFailure_NeverLosesTheFlush()
    {
        var service = new TaskSessionService(new TaskClassifier(new TaskRegistry()));
        var broken = new GearSnapshot();
        broken.Slots[GearSnapshot.Tool] = null!; // copying it throws
        var info = new ExtractedInfo { CurrentGear = broken };
        Period Fragment(int from, int to, Dictionary<string, int>? items) => new()
        {
            PlayerUuid = "p", Location = "Ruins", Server = "s", StartTime = T0.AddMinutes(from), EndTime = T0.AddMinutes(to),
            ItemsCollected = items ?? new()
        };

        service.Accumulate(info, "p", Fragment(0, 5, new() { ["X"] = 5 }), null, null, T0.AddMinutes(5));
        service.Accumulate(info, "p", Fragment(5, 10, new() { ["X"] = 5 }), null, null, T0.AddMinutes(10));
        var flush = service.Accumulate(info, "p", Fragment(10, 10, null), null, null, T0.AddMinutes(20));

        flush.Should().NotBeNull();
        flush.ItemsCollected["X"].Should().Be(10);
        flush.GearChanged.Should().BeTrue("unusable gear keeps the session out of the observations");
    }
}
