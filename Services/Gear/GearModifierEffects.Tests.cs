using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearModifierEffectsTests
{
    private static readonly GearModelOptions OnePlayer = new() { MinPlayers = 1 };

    private static GearChangeKey? Key(ObservedGear gear, string category = GearModifierTable.Farming, params string[] flags)
        => GearChangeKeys.For(Obs(1, gear, 1, 1, category: category, flags: flags).Observation, gear);

    [Test]
    public void Key_StaleOrAmbiguousParts_LeaveNoKey()
    {
        var gear = TestGear();

        Key(gear).Should().NotBeNull();
        foreach (var flag in new[] { "tool_ambiguous", "tool_stale", "armor_stale", "pet_stale", "equipment_stale" })
            Key(gear, flags: flag).Should().BeNull(flag);
        Key(gear, flags: "bait_stale").Should().NotBeNull();
        Key(gear, GearModifierTable.Dungeon, "equipment_stale").Should().NotBeNull("combat keys stop at the pet");
    }

    [Test]
    public void Key_UnknownParts_LeaveNoKey()
    {
        var noTool = TestGear();
        noTool.Slots.Remove(GearSnapshot.Tool);
        var noArmor = TestGear();
        foreach (var slot in GearSnapshot.ArmorSlots)
            noArmor.Slots.Remove(slot);

        Key(noTool).Should().BeNull();
        Key(noArmor).Should().BeNull();
        Key(TestGear(pet: null)).Should().BeNull();
        Key(TestGear(petTier: null)).Should().BeNull();
        Key(TestGear(equipment: false)).Should().BeNull();
        Key(TestGear(equipment: false), GearModifierTable.Dungeon).Should().NotBeNull();
    }

    [Test]
    public void Key_Parts()
    {
        var gear = TestGear();
        gear.Slots[GearSnapshot.Helmet].Enchants["pesterminator"] = 5;

        var key = Key(gear)!;

        key.Parts["tool.enchants.harvesting"].Should().Be("6");
        key.Parts["tool.reforge"].Should().Be("blessed");
        key.Parts["helmet.enchants.pesterminator"].Should().Be("5");
        key.Parts["equipment"].Should().Be("LOTUS_NECKLACE,-,-,-");
        key.Parts["armor"].Should().Be("FERMENTO_HELMET,FERMENTO_CHESTPLATE,FERMENTO_LEGGINGS,FERMENTO_BOOTS");
        key.Parts["pet"].Should().Be("ELEPHANT:LEGENDARY:100+");
    }

    /// <summary>Regression: raw 1-2-5 counter buckets were part of the key, so a growing counter split one setup.</summary>
    [Test]
    public void Counters_AreNotPartOfTheKey_NorAModifierChange()
    {
        var after = TestGear();
        after.Slots[GearSnapshot.Tool].Counters["farmed_cultivating"] = 2_000_000;
        var beforeKey = Key(TestGear())!;

        Key(after)!.Text.Should().Be(beforeKey.Text);
        beforeKey.Parts.Keys.Should().NotContain(k => k.Contains("counters"));
        var withCounter = new GearChangeKey(new SortedDictionary<string, string>(beforeKey.Parts.ToDictionary()) { ["tool.counters.farmed_cultivating"] = "2000000" });
        GearModifierEffects.Diff(beforeKey, withCounter).Should().BeEmpty("a counter-only difference is not a modifier change");
    }

    [Test]
    public void PairedDeltas_SamePlayerBeforeAndAfter_PooledOverPlayers_InverseCountsInverted()
    {
        var before = TestGear(harvesting: 5);
        var after = TestGear(harvesting: 6);
        var inputs = new[]
        {
            // player 1 upgrades: 1000 -> 1100 (x1.1)
            Obs(1, before, 1.5, 1000, at: Now.AddDays(-3)), Obs(1, after, 1.5, 1100, at: Now.AddDays(-1)),
            // player 2 downgrades: 1200 -> 1000, i.e. the upgrade is x1.2
            Obs(2, after, 1, 1200, at: Now.AddDays(-3)), Obs(2, before, 1, 1000, at: Now.AddDays(-1)),
            // player 3 has less than an hour after the change: not a pair
            Obs(3, before, 2, 1000, at: Now.AddDays(-3)), Obs(3, after, 0.5, 5000, at: Now.AddDays(-1))
        };

        var effect = GearModelService.Compute(Wheat, inputs, Now).Effects.Single();

        effect.Changes.Should().ContainSingle();
        effect.Changes[0].Should().BeEquivalentTo(new GearKeyChange { Component = "tool.enchants.harvesting", From = "5", To = "6" });
        effect.Players.Should().Be(2);
        effect.Pairs.Should().Be(2);
        effect.PercentChange.Should().Be(15, "the median of +10 % and +20 %, 2 significant digits");
        effect.Ratio.Should().Be(1.15);
        effect.Rounded.Should().BeTrue();
    }

    /// <summary>Regression: all of a player's A sessions were pooled, so A -&gt; B -&gt; A compared B with both A periods.</summary>
    [Test]
    public void PairedDeltas_AdjacentPeriodsOnly_EachSideFromItsOwnPeriod()
    {
        var a = TestGear(harvesting: 5);
        var b = TestGear(harvesting: 6);
        var inputs = new[]
        {
            Obs(1, a, 1, 1000, at: Now.AddDays(-5)), Obs(1, b, 1, 1100, at: Now.AddDays(-4)), Obs(1, a, 1, 2200, at: Now.AddDays(-3))
        };

        var effect = GearModifierEffects.Derive(inputs, OnePlayer).Single();

        effect.Pairs.Should().Be(2, "A -> B and B -> A");
        // A -> B: 1100 / 1000 = 1.1; B -> A: 2200 / 1100 = 2, inverted 0.5; geometric mean sqrt(0.55)
        effect.Ratio.Should().BeApproximately(System.Math.Sqrt(1.1 * 0.5), 1e-9);
    }

    [Test]
    public void PairedDeltas_ABAC_GivesTwoChanges()
    {
        var a = TestGear(harvesting: 5);
        var b = TestGear(harvesting: 6);
        var c = TestGear(harvesting: 5);
        c.Slots[GearSnapshot.Tool].Reforge = "bountiful";
        var inputs = new[]
        {
            Obs(1, a, 1, 1000, at: Now.AddDays(-5)), Obs(1, b, 1, 1100, at: Now.AddDays(-4)),
            Obs(1, a, 1, 1000, at: Now.AddDays(-3)), Obs(1, c, 1, 1300, at: Now.AddDays(-2))
        };

        var effects = GearModifierEffects.Derive(inputs, OnePlayer);

        effects.Should().HaveCount(2);
        var harvesting = effects.Single(e => e.Changes.Single().Component == "tool.enchants.harvesting");
        harvesting.Pairs.Should().Be(2);
        harvesting.Ratio.Should().BeApproximately(1.1, 1e-9);
        var reforge = effects.Single(e => e.Changes.Single().Component == "tool.reforge");
        reforge.Changes.Single().Should().BeEquivalentTo(new GearKeyChange { Component = "tool.reforge", From = "blessed", To = "bountiful" });
        reforge.Ratio.Should().BeApproximately(1.3, 1e-9);
    }

    [Test]
    public void PairedDeltas_OnePlayer_IsNotPublished()
    {
        var inputs = new[] { Obs(1, TestGear(harvesting: 5), 2, 1000, at: Now.AddDays(-3)), Obs(1, TestGear(harvesting: 6), 2, 1100, at: Now.AddDays(-1)) };

        GearModifierEffects.Derive(inputs).Should().BeEmpty();
    }

    [Test]
    public void KeyCache_SharesTheKeysOfRowsWithTheSameGearFlagsAndCategory()
    {
        var gear = TestGear();
        var cache = new GearChangeKeyCache();
        var first = Obs(1, gear, 1, 1000).Observation;
        var second = Obs(2, gear, 1, 2000).Observation;
        var flagged = Obs(3, gear, 1, 2000, flags: ["bait_stale"]).Observation;

        cache.Input(first, gear).ChangeKey.Should().BeSameAs(cache.Input(second, gear).ChangeKey);
        cache.Input(flagged, gear).ChangeKey.Should().NotBeSameAs(cache.Input(first, gear).ChangeKey);
    }
}
