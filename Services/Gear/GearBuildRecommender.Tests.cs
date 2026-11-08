using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearBuildRecommenderTests
{
    private static readonly GearScorer Scorer = GearScorer.For(Wheat, GearModifierTable.Farming)!;
    private static readonly GearModelOptions Options = GearModelOptions.Default;

    private static GearCatalogEntry Entry(string slot, GearPiece piece, int players = 5)
        => new($"{slot}:{piece.Tag}", slot, piece, null, players, Scorer.PieceContribution(slot, piece), Scorer.SetShare(slot, piece), null, []);

    private static GearCatalogEntry PetEntry(GearPet pet, int players = 5)
        => new($"pet:{pet.Type}", GearPieceCatalog.PetSlot, null, pet, players, Scorer.PetContribution(pet), 0, null, []);

    private static IEnumerable<GearCatalogEntry> ArmorSet(string set, double fortune)
        => GearSnapshot.ArmorSlots.Select(slot => Entry(slot, Armor(set, slot, fortune)));

    private static GearCatalogEntry Tool(double fortune) => Entry(GearSnapshot.Tool, new GearPiece { Tag = "TEST_HOE", Stats = new() { ["Farming Fortune"] = fortune } });

    [Test]
    public void AFullSet_BeatsBetterSinglePieces_WhenItsBonusMakesItScoreHigher()
    {
        // mix: 4 × 30 = 120; Fermento: 4 × 20 + 75 = 155
        var catalog = ArmorSet("TEST_MIX", 30).Concat(ArmorSet("FERMENTO", 20)).Append(Tool(100)).ToList();

        var build = GearBuildRecommender.Recommend(catalog, Scorer)!;

        build.Entries[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
        build.Entries.Values.Count(e => e.Tag.StartsWith("FERMENTO")).Should().Be(4);
        build.Score.Should().Be(255);
        build.Effects.Select(e => e.Effect.Id).Should().Equal("set-fermento");
    }

    [Test]
    public void BetterSinglePieces_BeatASetWhoseBonusIsTooSmall()
    {
        // mix: 4 × 30 = 120; Fermento: 4 × 0 + 75
        var catalog = ArmorSet("TEST_MIX", 30).Concat(ArmorSet("FERMENTO", 0)).Append(Tool(100)).ToList();

        var build = GearBuildRecommender.Recommend(catalog, Scorer)!;

        build.Entries.Values.Should().NotContain(e => e.Tag.StartsWith("FERMENTO"));
        build.Score.Should().Be(220);
    }

    [Test]
    public void PerSlot_TheHighestContribution()
    {
        var elephant = new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 100 };
        var catalog = new List<GearCatalogEntry>
        {
            Tool(0), PetEntry(elephant), PetEntry(new GearPet { Type = "RABBIT", Tier = "LEGENDARY", Level = 100 }, players: 9)
        };

        var build = GearBuildRecommender.Recommend(catalog, Scorer)!;

        build.Entries[GearPieceCatalog.PetSlot].Tag.Should().Be("ELEPHANT", "it adds score, the more used Rabbit does not");
        build.Score.Should().Be(150);
    }

    /// <summary>Regression: slots whose pieces add no score (Rancher's Boots, a Rabbit) were left empty in the build.</summary>
    [Test]
    public void SlotsWhereNothingAddsScore_GetTheMostUsedPiece()
    {
        var catalog = new List<GearCatalogEntry>
        {
            Tool(100),
            Entry(GearSnapshot.Boots, new GearPiece { Tag = "TEST_RANCHERS_BOOTS" }, players: 7),
            Entry(GearSnapshot.Boots, new GearPiece { Tag = "TEST_OTHER_BOOTS" }, players: 3),
            PetEntry(new GearPet { Type = "RABBIT", Tier = "LEGENDARY", Level = 100 }, players: 6),
            PetEntry(new GearPet { Type = "TEST_PET", Tier = "LEGENDARY", Level = 100 }, players: 2)
        };

        var build = GearBuildRecommender.Recommend(catalog, Scorer)!;

        build.Entries[GearSnapshot.Boots].Tag.Should().Be("TEST_RANCHERS_BOOTS");
        build.Entries[GearPieceCatalog.PetSlot].Tag.Should().Be("RABBIT");
        build.Score.Should().Be(100);
    }

    [Test]
    public void EmptyCatalog_NoRecommendation()
    {
        GearBuildRecommender.Recommend([], Scorer).Should().BeNull();
    }

    [Test]
    public void Rate_FromTheCurve_ExtrapolatedAtMostTwentyPercent()
    {
        var curve = new GearCurveModel(Math.Log(1000), 0.001, 200, 1000, 5, 10, GearCurveModel.TheilSen);

        var inside = GearBuildRecommender.Rate(800, curve, 50, null, Scorer.Formula, Options)!;
        var beyond = GearBuildRecommender.Rate(2000, curve, 50, null, Scorer.Formula, Options)!;

        inside.Should().Be(new GearRecommendedRate(Math.Round(1000 * Math.Exp(0.8) + 50), false, false, GearBuildRecommender.BasisCurve));
        beyond.Extrapolated.Should().BeTrue();
        beyond.CoinsPerHour.Should().Be(Math.Round(1000 * Math.Exp(1.2) + 50), "read at 1.2 × the max observed score, not at 2000");
    }

    /// <summary>
    /// Regression: the cap was the cohort's best player (later its P75, which can land exactly on one player's rate), so a
    /// capped rate could publish one player's own rate. The cap is the cohort's published rate.
    /// </summary>
    [Test]
    public void Rate_WithACohort_IsTheCohortsPublishedRate_NeverTheCurve()
    {
        var curve = new GearCurveModel(Math.Log(1000), 0.001, 200, 1000, 5, 10, GearCurveModel.TheilSen);

        GearBuildRecommender.Rate(1100, curve, 0, 1900, Scorer.Formula, Options)
            .Should().Be(new GearRecommendedRate(1900, false, false, GearBuildRecommender.BasisCohort), "above the curve's prediction is not used");
        GearBuildRecommender.Rate(100, curve, 0, 1_000_000, Scorer.Formula, Options)!.CoinsPerHour
            .Should().Be(1_000_000, "nor below it: the build stands for the late game cohort");
    }

    [Test]
    public void Pet_IsChosenWithTheBuildsItems_PerksThatNeedTheToolCount()
    {
        var scorer = GearScorer.For("Gemstone Mining", GearModifierTable.MiningGem)!;
        var drill = new GearPiece { Tag = "TEST_DRILL", ItemType = "DRILL", Stats = new() { ["Gemstone Fortune"] = 100 } };
        var scatha = new GearPet { Type = "SCATHA", Tier = "LEGENDARY", Level = 100 };
        var other = new GearPet { Type = "TEST_PET", Tier = "LEGENDARY", Level = 100, Stats = new() { ["Gemstone Fortune"] = 50 } };
        var catalog = new List<GearCatalogEntry>
        {
            new("tool:drill", GearSnapshot.Tool, drill, null, 5, scorer.PieceContribution(GearSnapshot.Tool, drill), 0, null, []),
            new("pet:scatha", GearPieceCatalog.PetSlot, null, scatha, 5, scorer.PetContribution(scatha), 0, null, []),
            new("pet:other", GearPieceCatalog.PetSlot, null, other, 5, scorer.PetContribution(other), 0, null, [])
        };

        var build = GearBuildRecommender.Recommend(catalog, scorer)!;

        build.Entries[GearPieceCatalog.PetSlot].Id.Should().Be("pet:scatha", "+125 Gemstone Fortune with a drill beats +50, though alone it is 0");
        build.Score.Should().Be(225);
    }

    [Test]
    public void Rate_WithoutACurve_TheCohortsRate_ElseNone()
    {
        GearBuildRecommender.Rate(500, null, 0, 12_000, Scorer.Formula, Options)
            .Should().Be(new GearRecommendedRate(12_000, false, false, GearBuildRecommender.BasisCohort));
        GearBuildRecommender.Rate(500, null, 0, null, Scorer.Formula, Options).Should().BeNull();
    }
}
