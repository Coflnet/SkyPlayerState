using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearPieceCatalogTests
{
    private static GearScorer Scorer => GearScorer.For(Wheat, GearModifierTable.Farming)!;

    private static List<GearCatalogEntry> Catalog(params GearObservationInput[] inputs)
    {
        var builds = Builds(inputs);
        return GearPieceCatalog.Build(builds, Scorer, GearScoreDistribution.Of(builds, GearModelOptions.Default), 2);
    }

    private static List<GearCatalogEntry> Tools(IEnumerable<GearCatalogEntry> catalog) => catalog.Where(e => e.Slot == GearSnapshot.Tool).ToList();

    [Test]
    public void APieceOfOnePlayer_IsNotPublished()
    {
        var catalog = Catalog(Obs(1, TestGear(tool: "TEST_RARE_HOE"), 1, 1000), Obs(1, TestGear(tool: "TEST_RARE_HOE"), 3, 1000),
            Obs(2, TestGear(), 1, 1000), Obs(3, TestGear(), 1, 1000));

        Tools(catalog).Select(e => e.Tag).Should().Equal("THEORETICAL_HOE_WHEAT_3");
        catalog.Should().NotContain(e => e.Tag == "TEST_RARE_HOE", "two sessions of one player are one player");
    }

    [Test]
    public void VariantsOfTwoPlayers_ArePublishedAsTheyAre_TheRestMergedWithoutTheirOwnModifiers()
    {
        var catalog = Catalog(
            Obs(1, TestGear(harvesting: 6), 1, 1000), Obs(2, TestGear(harvesting: 6), 1, 1000),
            Obs(3, TestGear(harvesting: 5), 1, 1000), Obs(4, TestGear(harvesting: 5), 1, 1000),
            Obs(5, TestGear(harvesting: 4), 1, 1000), Obs(6, TestGear(harvesting: 3), 1, 1000));

        var tools = Tools(catalog);

        tools.Select(t => t.Piece!.Enchants.GetValueOrDefault("harvesting")).Should().Equal(6, 5, 3);
        tools.Select(t => t.Players).Should().Equal(2, 2, 2);
        tools[2].Piece!.Reforge.Should().Be("blessed", "the merged players share the reforge");
        tools[2].Piece!.Enchants["harvesting"].Should().Be(3, "the highest level both merged players (4 and 3) reach");
        tools.Select(t => t.Id).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ModifiersOfFewerThanTwoPlayers_AreStripped_NumericOnesTheHighestTwoReach()
    {
        var gemmed = TestGear();
        gemmed.Slots[GearSnapshot.Tool].Gems["PERIDOT_0"] = "FINE_PERIDOT_GEM";
        gemmed.Slots[GearSnapshot.Tool].HotPotatoCount = 10;
        var bountiful = TestGear();
        bountiful.Slots[GearSnapshot.Tool].Reforge = "bountiful";

        var tool = Tools(Catalog(Obs(1, gemmed, 1, 1000), Obs(2, bountiful, 1, 1000), Obs(3, TestGear(harvesting: 5), 1, 1000))).Single();

        tool.Players.Should().Be(3);
        tool.Piece!.Gems.Should().BeEmpty("only player 1 has the gem");
        tool.Piece.HotPotatoCount.Should().BeNull();
        tool.Piece.Reforge.Should().Be("blessed", "players 1 and 3");
        tool.Piece.Enchants["harvesting"].Should().Be(6, "players 1 and 2");
    }

    /// <summary>Regression: modifiers fewer than 2 players shared exactly were stripped, so the top players' upgrades vanished.</summary>
    [Test]
    public void NumericModifiers_TheHighestValueTwoPlayersReach()
    {
        GearObservationInput Player(int player, int harvesting, int? potatoBooks, bool recombobulated, string rarity, long cultivated)
        {
            var gear = TestGear(harvesting: harvesting);
            var tool = gear.Slots[GearSnapshot.Tool];
            tool.HotPotatoCount = potatoBooks;
            tool.Recombobulated = recombobulated;
            tool.Rarity = rarity;
            tool.Enchants["cultivating"] = harvesting + 3;
            tool.Counters["farmed_cultivating"] = cultivated;
            tool.Gems["PERIDOT_0"] = $"TEST_GEM_{player}";
            return Obs(player, gear, 1, 1000);
        }

        var tool = Tools(Catalog(Player(1, 6, 15, true, "MYTHIC", 50_000_000), Player(2, 5, 10, true, "LEGENDARY", 20_000_000),
            Player(3, 4, null, false, "EPIC", 1_000_000))).Single();

        tool.Piece!.Enchants.Should().BeEquivalentTo(new Dictionary<string, int> { ["harvesting"] = 5, ["cultivating"] = 8 });
        tool.Piece.HotPotatoCount.Should().Be(10);
        tool.Piece.Recombobulated.Should().BeTrue();
        tool.Piece.Rarity.Should().Be("LEGENDARY");
        tool.Piece.Counters["farmed_cultivating"].Should().Be(20_000_000);
        tool.Piece.Gems.Should().BeEmpty("gems are categorical: no gem is shared");
    }

    [Test]
    public void LoreStats_TheHighestTwoPlayersReach_TheContributionIsScoredFromThem()
    {
        var tool = Tools(Catalog(Obs(1, TestGear(harvesting: 1, toolFortune: 100), 1, 1000), Obs(2, TestGear(harvesting: 2, toolFortune: 200), 1, 1000),
            Obs(3, TestGear(harvesting: 3, toolFortune: 300), 1, 1000))).Single();

        tool.Piece!.Stats["Farming Fortune"].Should().Be(200);
        tool.Contribution.Should().Be(200);
    }

    [Test]
    public void SetPieces_CarryTheirShareOfTheSetBonus()
    {
        var helmet = Catalog(Obs(1, TestGear(), 1, 1000), Obs(2, TestGear(), 1, 1000)).Single(e => e.Slot == GearSnapshot.Helmet);

        helmet.OwnContribution.Should().Be(0);
        helmet.SetShare.Should().Be(18.75, "a quarter of the Fermento +75");
        helmet.Effects.Single().Should().Match<GearEntryEffect>(e => e.Effect.Id == "set-fermento" && e.IsSetShare && e.Amount == 18.75);
    }

    [Test]
    public void Pets_TypeOfTwoPlayers_LevelTheLowerOfTheTwo()
    {
        var catalog = Catalog(Obs(1, TestGear(petLevel: 100), 1, 1000), Obs(2, TestGear(petLevel: 90), 1, 1000), Obs(3, TestGear(pet: "RABBIT"), 1, 1000));

        var pet = catalog.Single(e => e.Slot == GearPieceCatalog.PetSlot);
        pet.Pet!.Type.Should().Be("ELEPHANT");
        pet.Pet.Tier.Should().Be("LEGENDARY");
        pet.Pet.Level.Should().Be(90, "both players reach level 90");
        pet.Contribution.Should().Be(135, "Trunk Efficiency 1.5 per level");
        pet.Effects.Single().Effect.Id.Should().Be("pet-elephant-trunk-efficiency");
    }

    [Test]
    public void Pets_TierAndLevel_TheHighestTwoPlayersReach()
    {
        var catalog = Catalog(Obs(1, TestGear(petLevel: 100), 1, 1000), Obs(2, TestGear(petLevel: 90, petTier: "MYTHIC"), 1, 1000),
            Obs(3, TestGear(petLevel: 50, petTier: "EPIC"), 1, 1000));

        var pet = catalog.Single(e => e.Slot == GearPieceCatalog.PetSlot).Pet!;

        pet.Tier.Should().Be("LEGENDARY", "players 1 and 2 reach Legendary");
        pet.Level.Should().Be(90, "players 1 and 2 reach level 90");
    }

    /// <summary>Regression: tier and level were picked independently, so the published pet could be one nobody had.</summary>
    [Test]
    public void Pets_TierAndLevel_AreACombinationTwoPlayersReachTogether()
    {
        var catalog = Catalog(Obs(1, TestGear(petLevel: 50), 1, 1000), Obs(2, TestGear(petLevel: 60), 1, 1000),
            Obs(3, TestGear(petLevel: 100, petTier: "EPIC"), 1, 1000), Obs(4, TestGear(petLevel: 100, petTier: "EPIC"), 1, 1000));

        var pets = catalog.Where(e => e.Slot == GearPieceCatalog.PetSlot).Select(e => (e.Pet!.Tier, e.Pet.Level)).ToList();

        pets.Should().Contain(("LEGENDARY", 50), "players 1 and 2 have a Legendary of at least level 50");
        pets.Should().NotContain(("LEGENDARY", 100), "no player has a level 100 Legendary");
    }

    [Test]
    public void PetContribution_IsScoredWithTheMostCommonToolAndArmor_SoToolPerksCount()
    {
        var gemScorer = GearScorer.For("Gemstone Mining", GearModifierTable.MiningGem)!;
        ObservedGear Gear(int player) => new()
        {
            Slots = new()
            {
                [GearSnapshot.Tool] = new() { Tag = "TEST_DRILL", ItemType = "DRILL", Reforge = $"test{player}" },
                [GearSnapshot.Helmet] = Armor("TEST_PLAIN", GearSnapshot.Helmet)
            },
            Pet = new GearPet { Type = "SCATHA", Tier = "LEGENDARY", Level = 100 }
        };
        var builds = GearPlayerBuilds.From([Obs(1, Gear(1), 1, 1000, category: GearModifierTable.MiningGem),
            Obs(2, Gear(2), 1, 1000, category: GearModifierTable.MiningGem)], gemScorer);

        var pet = GearPieceCatalog.Build(builds, gemScorer, null, 2).Single(e => e.Slot == GearPieceCatalog.PetSlot);

        pet.Contribution.Should().Be(125, "Drill Infusion needs the drill the players use");
        pet.Effects.Single().Effect.Id.Should().Be("pet-scatha-drill-infusion-legendary");
    }

    /// <summary>Regression: a merged group that published the same piece as a variant was dropped with its players.</summary>
    [Test]
    public void GroupsPublishingTheSamePiece_AreMergedWithTheirPlayers()
    {
        // players 1 + 2 share harvesting 6 (a variant); players 3 and 4 differ only in their gems, so the rest group
        // publishes harvesting 6 without gems as well: the same piece
        GearObservationInput Gemmed(int player)
        {
            var gear = TestGear(harvesting: 6);
            gear.Slots[GearSnapshot.Tool].Gems["PERIDOT_0"] = $"TEST_GEM_{player}";
            return Obs(player, gear, 1, 1000);
        }
        var catalog = Catalog(Obs(1, TestGear(harvesting: 6), 1, 1000), Obs(2, TestGear(harvesting: 6), 1, 1000), Gemmed(3), Gemmed(4));

        Tools(catalog).Should().ContainSingle().Which.Players.Should().Be(4);
    }

    [Test]
    public void UsedAtScorePercentile_MedianOverPlayers()
    {
        var catalog = Catalog(Obs(1, ScoreGear(100, tool: "TEST_LOW"), 1, 1000), Obs(2, ScoreGear(110, tool: "TEST_LOW"), 1, 1000),
            Obs(3, ScoreGear(500, tool: "TEST_HIGH"), 1, 1000), Obs(4, ScoreGear(600, tool: "TEST_HIGH"), 1, 1000));

        catalog.Single(e => e.Tag == "TEST_HIGH").UsedAtPercentile.Should().BeApproximately(100 * 2.5 / 3, 1e-9, "ranks 2 and 3 of 0 .. 3");
        catalog.Single(e => e.Tag == "TEST_LOW").UsedAtPercentile.Should().BeApproximately(100 * 0.5 / 3, 1e-9);
    }

    [Test]
    public void Published_BelowFivePlayers_RoundedAndBanded()
    {
        var tool = Tools(Catalog(Obs(1, TestGear(harvesting: 1, toolFortune: 123.4), 1, 1000), Obs(2, TestGear(harvesting: 2, toolFortune: 234.5), 1, 1000),
            Obs(3, TestGear(harvesting: 3, toolFortune: 345.6), 1, 1000))).Single();

        var published = GearModelPublisher.Piece(tool);

        published.Item!.Stats["Farming Fortune"].Should().Be(230);
        published.ScoreContribution.Should().Be(230);
        published.Players.Should().Be(2);
        published.PlayersLabel.Should().Be("2+");
        published.Rounded.Should().BeTrue();
        published.UsedAtScorePercentile.Should().BeNull("below 5 players");
    }

    private static readonly GearScorer FishingScorer = GearScorer.For("Water Fishing", GearModifierTable.FishingWater)!;

    private static GearObservationInput FishingObs(int player, string? tier, int level = 100)
    {
        var gear = TestGear(pet: "FLYING_FISH", petTier: tier, petLevel: level, armor: "TEST_PLAIN", equipment: false);
        gear.Slots[GearSnapshot.Tool] = new GearPiece { Tag = "TEST_ROD", ItemType = "FISHING ROD" };
        return Obs(player, gear, 1, 1000, category: GearModifierTable.FishingWater);
    }

    private static GearCatalogEntry FishingPet(params GearObservationInput[] inputs)
    {
        var builds = GearPlayerBuilds.From(inputs, FishingScorer);
        return GearPieceCatalog.Build(builds, FishingScorer, null, 2).Single(e => e.Slot == GearPieceCatalog.PetSlot);
    }

    /// <summary>Regression: pets whose rarity was not shared were published without one and scored 0.</summary>
    [Test]
    public void Pets_LegendaryAndEpic_ArePublishedEpic_WithTheEpicPerk()
    {
        var pet = FishingPet(FishingObs(1, "LEGENDARY"), FishingObs(2, "EPIC"));

        pet.Pet!.Tier.Should().Be("EPIC");
        pet.Contribution.Should().Be(75, "Quick Reel at Epic");
    }

    /// <summary>
    /// Regression: a pet seen without a tier (tab list) was published without one and scored 0. The unknown sightings
    /// (level 80) form a variant of their own: without the filled tier it is published tierless.
    /// </summary>
    [Test]
    public void Pets_UnknownTier_TakesThePlayersOwnKnownTier()
    {
        var catalog = GearPieceCatalog.Build(GearPlayerBuilds.From([FishingObs(1, "LEGENDARY"), FishingObs(2, "LEGENDARY"),
            FishingObs(1, null, 80), FishingObs(2, null, 80)], FishingScorer), FishingScorer, null, 2);

        var pets = catalog.Where(e => e.Slot == GearPieceCatalog.PetSlot).ToList();
        pets.Should().Contain(e => e.Pet!.Level == 80 && e.Pet.Tier == "LEGENDARY" && e.Contribution == 64, "Quick Reel 80 at level 80");
        pets.Should().OnlyContain(e => e.Pet!.Tier == "LEGENDARY");
    }

    [Test]
    public void TierFiller_OwnTierFirst_ThenTheTypesLowestKnownTier()
    {
        GearSeen<GearPet> Seen(int player, string type, string? tier) => new(new GearPet { Type = type, Tier = tier, Level = 100 }, 1, Player(player));
        var fill = GearSharedItems.TierFiller([Seen(1, "FLYING_FISH", "LEGENDARY"), Seen(1, "FLYING_FISH", "EPIC"), Seen(2, "FLYING_FISH", "EPIC"),
            Seen(3, "FLYING_FISH", null), Seen(1, "ELEPHANT", "LEGENDARY"), Seen(2, "ELEPHANT", null)], 2);
        string? Filled(int player, string type) => fill(new GearPet { Type = type, Level = 100 }, Player(player)).Tier;

        Filled(1, "FLYING_FISH").Should().Be("LEGENDARY", "player 1's highest known tier");
        Filled(3, "FLYING_FISH").Should().Be("EPIC", "the lowest tier of the two players known with one");
        Filled(2, "ELEPHANT").Should().BeNull("only one player is known with a tier");
        fill(new GearPet { Type = "FLYING_FISH", Tier = "RARE", Level = 1 }, Player(1)).Tier.Should().Be("RARE", "a known tier stays");
    }

    [Test]
    public void Pets_UnknownTier_OfAPlayerNeverSeenWithOne_TakesTheTypesLowestKnownTier()
    {
        var catalog = GearPieceCatalog.Build(GearPlayerBuilds.From([FishingObs(1, "LEGENDARY"), FishingObs(2, "EPIC"),
            FishingObs(3, null, 50), FishingObs(4, null, 50)], FishingScorer), FishingScorer, null, 2);

        catalog.Where(e => e.Slot == GearPieceCatalog.PetSlot).Should().OnlyContain(e => e.Pet!.Tier == "EPIC" && e.Contribution > 0);
    }

    [Test]
    public void Pets_UnknownTier_StaysUnknown_WhenFewerThanTwoPlayersShowATier()
    {
        var pet = FishingPet(FishingObs(1, "LEGENDARY"), FishingObs(2, null), FishingObs(3, null));

        pet.Pet!.Tier.Should().BeNull("one player's tier would be published otherwise");
    }
}
