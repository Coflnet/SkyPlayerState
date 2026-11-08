using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearClusterTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearClusteringTests
{
    private static List<GearCluster> Publish(params GearClusterInput[] inputs)
        => GearClusterService.Compute(Wheat, inputs, Now).Clusters;

    /// <summary>The finest published cluster (the nested levels share the members when nobody differs).</summary>
    private static GearCluster Finest(IEnumerable<GearCluster> clusters) => clusters.OrderByDescending(c => c.Level).First();

    private static GearClusterService Service(InMemoryGearReader reader, int gearCacheSize = GearClusterService.DefaultGearCacheSize)
        => new(reader, new Tasks.TaskRegistry(), null!, NullLogger<GearClusterService>.Instance, GearClusterOptions.Default, gearCacheSize);

    [Test]
    public void TwoSessionsOfOnePlayer_AreNotACluster_ASecondPlayerMakesOne()
    {
        var gear = TestGear();
        var onePlayer = new[] { Obs(1, gear, 2, 1000), Obs(1, gear, 2, 1200) };

        Publish(onePlayer).Should().BeEmpty("k = 2 counts distinct players, not sessions");

        var clusters = Publish([.. onePlayer, Obs(2, gear, 1, 900)]);
        clusters.Select(c => c.Level).Should().BeEquivalentTo([1, 2, 3], "every level of the same two players qualifies");
        clusters.Should().OnlyContain(c => c.Players == 2);
        Finest(clusters).ObservationsLabel.Should().Be("2+", "3 observations of 2 players are banded");
    }

    [Test]
    public void LessThanAnHourOfPlayTime_IsNotACluster()
    {
        var gear = TestGear();

        Publish(Obs(1, gear, 0.4, 1000), Obs(2, gear, 0.4, 1000)).Should().BeEmpty("0.8 h is below the 1 h minimum");
        Publish(Obs(1, gear, 0.5, 1000), Obs(2, gear, 0.5, 1000)).Should().NotBeEmpty();
    }

    [Test]
    public void EachPlayerIsCollapsedToOneHoursWeightedRate_BeforeTheMedian()
    {
        var gear = TestGear();
        // player 1: (4 * 100 + 4 * 900) / 8 h = 500/h; the session median of all nine sessions would be 200
        var inputs = new[]
        {
            Obs(1, gear, 1, 100), Obs(1, gear, 1, 100), Obs(1, gear, 1, 100), Obs(1, gear, 1, 100), Obs(1, gear, 4, 900),
            Obs(2, gear, 1, 200), Obs(3, gear, 1, 300), Obs(4, gear, 1, 700), Obs(5, gear, 1, 800)
        };

        GearClustering.PlayerRates(inputs).Single(r => r.Player == Player(1)).CommonPerHour.Should().BeApproximately(500, 1e-9);

        var cluster = Finest(Publish(inputs));
        cluster.CommonCoinsPerHour.Should().Be(500, "the median of 200, 300, 500, 700 and 800");
        cluster.Players.Should().Be(5);
        cluster.Observations.Should().Be(9);
        cluster.ObservationsLabel.Should().Be("9");
        cluster.Hours.Should().Be(12);
        cluster.CommonP25.Should().Be(300);
        cluster.CommonP75.Should().Be(700);
        cluster.Confidence.Should().Be(GearClustering.Medium, "the rates vary by half their mean");
        cluster.Rounded.Should().BeFalse();
    }

    [Test]
    public void RareDrops_ArePooledOverAllHours()
    {
        var gear = TestGear();

        var cluster = Finest(Publish(Obs(1, gear, 3, 1000, rarePerHour: 0), Obs(2, gear, 1, 1000, rarePerHour: 4000), Obs(3, gear, 1, 1000)));

        cluster.RareCoinsPerHour.Should().Be(800, "4000 coins over 5 hours");
        cluster.CoinsPerHour.Should().Be(1800);
    }

    /// <summary>Regression: rolled up clusters held only the observations no finer cluster took, a biased remainder.</summary>
    [Test]
    public void Levels_AreNested_AParentHoldsEveryObservationUnderItsKey()
    {
        var inputs = new[]
        {
            // same L3
            Obs(1, TestGear(harvesting: 6), 1, 1000), Obs(2, TestGear(harvesting: 6), 1, 1000),
            // same L2 as players 1 and 2, other harvesting levels
            Obs(3, TestGear(harvesting: 4), 1, 800), Obs(4, TestGear(harvesting: 5), 1, 900),
            // same L1, other pets
            Obs(5, TestGear(pet: "MOOSHROOM_COW"), 1, 700), Obs(6, TestGear(pet: "RABBIT"), 1, 600),
            // alone at every level
            Obs(7, TestGear(tool: "THEORETICAL_HOE_WHEAT_2"), 5, 500)
        };

        var clusters = Publish(inputs);

        clusters.Select(c => (c.Level, c.Players, c.CommonCoinsPerHour)).Should().Equal((3, 2, 1000.0), (2, 4, 950.0), (1, 6, 850.0));
        clusters[0].ParentKey.Should().Be(clusters[1].Key);
        clusters[1].ParentKey.Should().Be(clusters[2].Key);
        clusters[2].ParentKey.Should().BeNull();
        clusters.Select(c => c.IncludesChildren).Should().Equal(false, true, true);
        clusters[1].Key.Should().Contain("pet=ELEPHANT:LEGENDARY:100+").And.NotContain("harvesting");
        clusters[2].Key.Should().NotContain("pet=");
    }

    [Test]
    public void Keys_StaleOrAmbiguousParts_DropTheirLevel()
    {
        var gear = TestGear();
        GearSetupKey?[] Keys(string category, params string[] flags)
            => GearSetupKeys.For(Obs(1, gear, 1, 1, category: category, flags: flags));
        const string farming = GearModifierTable.Farming;

        Keys(farming).Should().OnlyContain(k => k != null);
        Keys(farming, "tool_ambiguous").Should().OnlyContain(k => k == null);
        Keys(farming, "tool_stale").Should().OnlyContain(k => k == null);
        Keys(farming, "armor_stale").Should().OnlyContain(k => k == null);
        Keys(farming, "pet_stale").Select(k => k?.Level).Should().Equal(1, null, null);
        Keys(farming, "equipment_stale").Select(k => k?.Level).Should().Equal(1, 2, null);
        Keys(farming, "bait_stale").Should().OnlyContain(k => k != null);
        Keys(GearModifierTable.Dungeon).Select(k => k?.Level).Should().Equal(1, 2, null);
    }

    [Test]
    public void Keys_UnknownParts_DropTheirLevel()
    {
        GearSetupKey?[] Keys(ObservedGear gear) => GearSetupKeys.For(Obs(1, gear, 1, 1));
        var noTool = TestGear();
        noTool.Slots.Remove(GearSnapshot.Tool);
        var noArmor = TestGear();
        foreach (var slot in GearSnapshot.ArmorSlots)
            noArmor.Slots.Remove(slot);

        Keys(noTool).Should().OnlyContain(k => k == null);
        Keys(noArmor).Should().OnlyContain(k => k == null);
        Keys(TestGear(pet: null)).Select(k => k?.Level).Should().Equal(1, null, null);
        Keys(TestGear(petTier: null)).Select(k => k?.Level).Should().Equal(1, null, null);
        Keys(TestGear(equipment: false)).Select(k => k?.Level).Should().Equal(1, 2, null);
    }

    [Test]
    public void StaleEquipment_ClustersAtThePetLevel()
    {
        var gear = TestGear();

        var clusters = Publish(Obs(1, gear, 1, 1000, flags: ["equipment_stale"]), Obs(2, gear, 1, 1000));

        Finest(clusters).Level.Should().Be(GearSetupKeys.WithPet, "player 1's equipment is stale, so only the L2 key has two players");
    }

    [Test]
    public void ModifierKeyParts()
    {
        var gear = TestGear();
        gear.Slots[GearSnapshot.Helmet].Enchants["pesterminator"] = 5;

        var key = GearSetupKeys.For(Obs(1, gear, 1, 1))[2]!;

        key.Parts["tool.enchants.harvesting"].Should().Be("6");
        key.Parts["tool.reforge"].Should().Be("blessed");
        key.Parts["helmet.enchants.pesterminator"].Should().Be("5");
        key.Parts["equipment"].Should().Be("LOTUS_NECKLACE,-,-,-");
        key.Parts["armor"].Should().Be("FERMENTO_HELMET,FERMENTO_CHESTPLATE,FERMENTO_LEGGINGS,FERMENTO_BOOTS");
    }

    /// <summary>Regression: raw 1-2-5 counter buckets were part of the L3 key, so a growing counter split one setup.</summary>
    [Test]
    public void Counters_AreNotPartOfTheKey_NorAModifierChange()
    {
        var before = TestGear();
        var after = TestGear();
        after.Slots[GearSnapshot.Tool].Counters["farmed_cultivating"] = 2_000_000;

        var beforeKey = GearSetupKeys.For(Obs(1, before, 1, 1))[2]!;
        var afterKey = GearSetupKeys.For(Obs(1, after, 1, 1))[2]!;

        afterKey.Text.Should().Be(beforeKey.Text);
        afterKey.Parts.Keys.Should().NotContain(k => k.Contains("counters"));
        var withCounter = new GearSetupKey(3, new SortedDictionary<string, string>(beforeKey.Parts.ToDictionary()) { ["tool.counters.farmed_cultivating"] = "2000000" });
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

        var effect = GearClusterService.Compute(Wheat, inputs, Now).Effects.Single();

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

        var effect = GearModifierEffects.Derive(inputs, GearModifierTable.Farming, new GearClusterOptions { MinPlayers = 1 }).Single();

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

        var effects = GearModifierEffects.Derive(inputs, GearModifierTable.Farming, new GearClusterOptions { MinPlayers = 1 });

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

        GearModifierEffects.Derive(inputs, GearModifierTable.Farming).Should().BeEmpty();
    }

    [Test]
    public void TwoPlayers_RatesRoundedToTwoSignificantDigits_NoPercentiles()
    {
        var gear = TestGear();
        var stats = (double ff) => new Dictionary<string, double> { ["Farming Fortune"] = ff };

        var cluster = Finest(Publish(Obs(1, gear, 1.23, 12_345, rarePerHour: 1_234, stats: stats(1234)), Obs(2, gear, 1.2, 23_456, stats: stats(1456))));

        cluster.CommonCoinsPerHour.Should().Be(18_000, "the median 17900.5 rounded");
        cluster.RareCoinsPerHour.Should().Be(620, "1518 coins over 2.43 h = 624.6 rounded");
        cluster.CoinsPerHour.Should().Be(18_620, "the sum of the published parts");
        cluster.Hours.Should().Be(2.4);
        cluster.CommonP25.Should().BeNull();
        cluster.CommonP75.Should().BeNull();
        cluster.StatMedians["Farming Fortune"].Should().Be(1300);
        cluster.Rounded.Should().BeTrue();
        cluster.Confidence.Should().Be(GearClustering.Low);
    }

    /// <summary>Regression: from 3 players the median and the two percentiles gave away all three players' rates.</summary>
    [Test]
    public void BelowFivePlayers_RoundedAndBanded_FromFiveExact()
    {
        var gear = TestGear();
        var rates = new[] { 12_345, 23_456, 34_567, 45_678, 56_789 };
        var five = rates.Select((r, i) => Obs(i + 1, gear, 1, r)).ToList();
        five.Add(Obs(1, gear, 1, 12_345));
        five.Add(Obs(2, gear, 1, 23_456));

        var three = Finest(Publish([.. five.Where(o => o.Player != Player(4) && o.Player != Player(5))]));
        three.Players.Should().Be(3);
        three.CommonCoinsPerHour.Should().Be(23_000);
        three.CommonP25.Should().BeNull();
        three.Observations.Should().Be(5, "5 observations stay 5 in the 1-2-5 band");
        three.Rounded.Should().BeTrue();

        var all = Finest(Publish([.. five]));
        all.CommonCoinsPerHour.Should().Be(34_567);
        all.CommonP25.Should().Be(23_456);
        all.CommonP75.Should().Be(45_678);
        all.Observations.Should().Be(7);
        all.Rounded.Should().BeFalse();
    }

    [Test]
    public void ObservationCounts_AreBandedBelowFivePlayers()
    {
        var gear = TestGear();
        var inputs = Enumerable.Range(0, 7).Select(i => Obs(i % 2 + 1, gear, 1, 1000)).ToArray();

        var cluster = Finest(Publish(inputs));

        cluster.Observations.Should().Be(5);
        cluster.ObservationsLabel.Should().Be("5+");
    }

    [Test]
    public void Confidence_HighNeedsFivePlayersAndLowVariation()
    {
        GearClustering.Confidence(5, 0.2).Should().Be(GearClustering.High);
        GearClustering.Confidence(5, 0.5).Should().Be(GearClustering.Medium);
        GearClustering.Confidence(3, 0.1).Should().Be(GearClustering.Medium);
        GearClustering.Confidence(2, 0.1).Should().Be(GearClustering.Low);
    }

    [TestCase(12_345, 12_000)]
    [TestCase(17_900.5, 18_000)]
    [TestCase(-987, -990)]
    [TestCase(0.01234, 0.012)]
    [TestCase(0, 0)]
    public void RoundSignificant_TwoDigits(double value, double expected)
    {
        GearClusterPublisher.RoundSignificant(value, 2).Should().Be(expected);
    }

    [Test]
    public void StatMedians_OnlyBaseGearObservations_AndOnlyStatsOfTwoPlayers()
    {
        var gear = TestGear();
        var inputs = new[]
        {
            Obs(1, gear, 1, 1000, stats: new() { ["Farming Fortune"] = 1000, ["Wheat Fortune"] = 50 }),
            Obs(2, gear, 1, 1000, stats: new() { ["Farming Fortune"] = 1200 }),
            Obs(3, gear, 1, 1000)
        };

        GearClusterItems.StatMedians(inputs, 2).Should().BeEquivalentTo(new Dictionary<string, double> { ["Farming Fortune"] = 1100 },
            "one player's Wheat Fortune is not published");
    }

    [Test]
    public void Items_TheModifiersMostPlayersShare()
    {
        var bountiful = TestGear(harvesting: 6);
        bountiful.Slots[GearSnapshot.Tool].Reforge = "bountiful";

        var clusters = Publish(Obs(1, TestGear(harvesting: 6), 3, 1000), Obs(2, bountiful, 1, 1000), Obs(3, TestGear(harvesting: 5), 1, 1000));

        var cluster = Finest(clusters);
        cluster.Level.Should().Be(GearSetupKeys.WithPet, "the reforge and the enchant level split the L3 key");
        var tool = cluster.Slots[GearSnapshot.Tool];
        tool.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
        tool.Reforge.Should().Be("blessed", "players 1 and 3");
        tool.Enchants["harvesting"].Should().Be(6, "players 1 and 2");
        cluster.Pet!.Type.Should().Be("ELEPHANT");
        cluster.Slots.Keys.Should().Contain([GearSnapshot.Helmet, GearSnapshot.Necklace]);
    }

    /// <summary>Regression: the representative piece published one player's gems, potato books and lore stats.</summary>
    [Test]
    public void Items_NoSinglePlayerAttribute_LoreStatsPerPlayerFirst()
    {
        var gemmed = TestGear();
        var tool = gemmed.Slots[GearSnapshot.Tool];
        tool.Gems["PERIDOT_0"] = "FINE_PERIDOT_GEM";
        tool.HotPotatoCount = 10;
        tool.Counters["farmed_cultivating"] = 2_000_000;
        var withStats = (ObservedGear gear, double ff) =>
        {
            gear.Slots[GearSnapshot.Tool].Stats["Farming Fortune"] = ff;
            return gear;
        };

        var cluster = Finest(Publish(
            Obs(1, withStats(gemmed, 100), 1, 1000), Obs(1, withStats(TestGear(), 100), 1, 1000), Obs(1, withStats(TestGear(), 100), 1, 1000),
            Obs(2, withStats(TestGear(), 200), 1, 1000), Obs(3, withStats(TestGear(), 300), 1, 1000)));

        var published = cluster.Slots[GearSnapshot.Tool];
        published.Gems.Should().BeEmpty("only player 1 has the gem");
        published.HotPotatoCount.Should().BeNull();
        published.Counters["farmed_cultivating"].Should().Be(1_000_000, "the highest bucket two players reach");
        published.Stats["Farming Fortune"].Should().Be(200, "the median of the players 100, 200 and 300, not of the five sessions");
    }

    [Test]
    public void Items_ASlotOnlyOnePlayerFilled_IsNotListed()
    {
        var otherNecklace = TestGear();
        otherNecklace.Slots[GearSnapshot.Necklace] = new GearPiece { Tag = "TEST_OTHER_NECKLACE", ItemType = "NECKLACE" };

        var single = Finest(Publish(Obs(1, TestGear(), 1, 1000), Obs(2, TestGear(equipment: false), 1, 1000)));
        var differing = Finest(Publish(Obs(1, TestGear(), 1, 1000), Obs(2, otherNecklace, 1, 1000)));

        single.Slots.Should().NotContainKey(GearSnapshot.Necklace, "only one player's necklace is known");
        differing.Slots.Should().NotContainKey(GearSnapshot.Necklace, "each necklace tag belongs to one player");
        single.Slots.Should().ContainKey(GearSnapshot.Tool);
    }

    [Test]
    public void Items_PetOfAnL1Cluster_OnlySharedParts()
    {
        var levelled = Finest(Publish(Obs(1, TestGear(petLevel: 100), 1, 1000), Obs(2, TestGear(petLevel: 90), 1, 1000)));
        var differentPets = Finest(Publish(Obs(1, TestGear(), 1, 1000), Obs(2, TestGear(pet: "RABBIT"), 1, 1000)));

        levelled.Level.Should().Be(GearSetupKeys.ToolArmor);
        levelled.Pet!.Type.Should().Be("ELEPHANT");
        levelled.Pet.Tier.Should().Be("LEGENDARY");
        levelled.Pet.Level.Should().Be(0, "the players' levels differ");
        differentPets.Pet.Should().BeNull();
    }

    /// <summary>Regression: the top 12 by players dropped the best rate cluster when it had only 2 players.</summary>
    [Test]
    public void TopTwelve_KeepsTheBestRateCluster_AmongTwenty()
    {
        var inputs = new List<GearClusterInput>();
        for (var set = 0; set < 20; set++)
            for (var p = 0; p < 3; p++)
                inputs.Add(Obs(100 + set * 3 + p, TestGear(armor: $"TEST_SET_{set}"), 2, 1000 + set));
        inputs.Add(Obs(1, TestGear(armor: "TEST_BEST"), 1, 50_000));
        inputs.Add(Obs(2, TestGear(armor: "TEST_BEST"), 1, 50_000));

        var clusters = Publish([.. inputs]);

        clusters.Should().HaveCount(12);
        clusters[0].CoinsPerHour.Should().Be(50_000);
        clusters[0].Players.Should().Be(2);
        clusters.Where(c => c.Key.Contains("TEST_BEST")).Select(c => c.Level).Should().BeEquivalentTo([1, 2, 3], "the best of each level is kept");
    }

    [Test]
    public void Privacy_NoPlayerKeyOrFingerprintInThePublicJson()
    {
        var gear = TestGear();
        var inputs = new[]
        {
            Obs(1, TestGear(harvesting: 5), 1.5, 1000, at: Now.AddDays(-3)), Obs(1, gear, 1.5, 1100, at: Now.AddDays(-1)),
            Obs(2, TestGear(harvesting: 5), 1.5, 1000, at: Now.AddDays(-3)), Obs(2, gear, 1.5, 1200, at: Now.AddDays(-1))
        };
        var result = GearClusterService.Compute(Wheat, inputs, Now);
        result.Clusters.Should().NotBeEmpty();
        result.Effects.Should().NotBeEmpty();

        var json = JsonSerializer.Serialize(new TaskGearClusters { Clusters = result.Clusters, ModifierEffects = result.Effects });

        foreach (var input in inputs)
        {
            json.Should().NotContain(input.Observation.PlayerKey);
            json.Should().NotContain(input.Observation.GearFingerprint);
        }
        json.Should().NotContainEquivalentOf("playerKey").And.NotContainEquivalentOf("fingerprint");
    }

    [Test]
    public void Compute_OtherCategoriesObservations_AreLeftOut()
    {
        var gear = TestGear();

        Publish(Obs(1, gear, 1, 1000), Obs(2, gear, 1, 1000, category: GearModifierTable.Other)).Should().BeEmpty();
    }

    [Test]
    public async Task Service_LoadsThirteenWeeks_JoinsTheGear_AndKeepsTheResult()
    {
        var reader = new InMemoryGearReader();
        var gear = TestGear();
        reader.Add(Obs(1, gear, 1, 1000, at: Now.AddDays(-80)), Obs(2, gear, 1, 3000, at: Now.AddDays(-1)),
            Obs(3, gear, 1, 99_000, at: Now.AddDays(-100)));
        var orphan = Obs(4, TestGear(harvesting: 1), 1, 50_000, at: Now.AddDays(-2));
        reader.Rows.Add((Wheat, orphan.Observation)); // its gear row expired
        var service = Service(reader);

        var result = await service.Refresh(Wheat, Now);

        reader.WeeksRead.Should().HaveCount(GearClusterService.WindowWeeks);
        var cluster = Finest(result.Clusters);
        cluster.Players.Should().Be(2, "the 100 day old observation is outside the window, the orphan has no gear");
        cluster.CommonCoinsPerHour.Should().Be(2000);
        service.GetMeasured("wheat farming").Should().BeSameAs(result);

        await service.Refresh(Wheat, Now);
        reader.GearReads.Should().Be(2, "known fingerprints are cached, only the orphan is asked for again");
    }

    /// <summary>Regression: a full gear cache was cleared after the task's cached fingerprints were chosen, dropping their observations.</summary>
    [Test]
    public async Task Service_FullGearCache_KeepsTheTasksGear()
    {
        var reader = new InMemoryGearReader();
        reader.Add(Obs(1, TestGear(harvesting: 6), 1, 1000), Obs(2, TestGear(harvesting: 5), 1, 1000));
        var service = Service(reader, gearCacheSize: 2);
        (await service.Refresh(Wheat, Now)).Clusters.Should().NotBeEmpty();

        reader.Add(Obs(3, TestGear(harvesting: 4), 1, 1000));
        var result = await service.Refresh(Wheat, Now);

        result.Clusters.Single(c => c.Level == GearSetupKeys.WithPet).Players.Should().Be(3, "the two cached fingerprints and the new one");
    }

    [Test]
    public void KeyCache_SharesTheKeysOfRowsWithTheSameGearFlagsAndCategory()
    {
        var gear = TestGear();
        var cache = new GearSetupKeyCache();
        var first = Obs(1, gear, 1, 1000).Observation;
        var second = Obs(2, gear, 1, 2000).Observation;
        var flagged = Obs(3, gear, 1, 2000, flags: ["pet_stale"]).Observation;

        cache.Input(first, gear).Keys.Should().BeSameAs(cache.Input(second, gear).Keys);
        cache.Input(flagged, gear).Keys.Should().NotBeSameAs(cache.Input(first, gear).Keys);
    }
}
