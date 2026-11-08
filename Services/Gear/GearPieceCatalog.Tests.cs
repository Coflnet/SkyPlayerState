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

        tools.Select(t => t.Piece!.Enchants.GetValueOrDefault("harvesting")).Should().Equal(6, 5, 0);
        tools.Select(t => t.Players).Should().Equal(2, 2, 2);
        tools[2].Piece!.Reforge.Should().Be("blessed", "the merged players share the reforge");
        tools.Select(t => t.Id).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ModifiersOfFewerThanTwoPlayers_AreStripped()
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

    [Test]
    public void LoreStats_AreMediansOverPlayers_TheContributionIsScoredFromThem()
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
}
