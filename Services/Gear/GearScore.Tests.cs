using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearModelTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearScoreTests
{
    private static Dictionary<string, double> Stats(params (string Name, double Value)[] stats) => stats.ToDictionary(s => s.Name, s => s.Value);

    private static GearScorer Scorer(string task, string category, string[] locations, params GearEffect[] effects)
        => new(GearScoreFormulas.For(category, task)!, new GearEffectTable(effects).For(new GearTaskContext(task, category, locations)));

    private static GearScorer Embedded(string task, string category, params string[] locations)
        => new(GearScoreFormulas.For(category, task)!, GearEffectTable.Embedded.For(new GearTaskContext(task, category, locations)));

    private static ObservedGear Gear(GearPet? pet = null, params (string Slot, GearPiece Piece)[] pieces)
        => new() { Slots = pieces.ToDictionary(p => p.Slot, p => p.Piece), Pet = pet };

    private static GearPiece Piece(string tag, params (string Name, double Value)[] stats) => new() { Tag = tag, Stats = Stats(stats) };

    [Test]
    public void Farming_FarmingFortunePlusTheTaskCropsFortune()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Farming, Wheat)!;

        formula.Score(Stats(("Farming Fortune", 1000), ("Wheat Fortune", 100), ("Carrot Fortune", 500))).Should().Be(1100);
        formula.Weights.Keys.Should().BeEquivalentTo(["Farming Fortune", "Wheat Fortune"]);
    }

    [Test]
    public void MiningAndForaging_Weights()
    {
        GearScoreFormulas.For(GearModifierTable.MiningGem, "Gemstone Mining")!
            .Score(Stats(("Gemstone Fortune", 300), ("Mining Speed", 2000), ("Pristine", 5), ("Mining Spread", 100))).Should().Be(300 + 200 + 100 + 50);
        GearScoreFormulas.For(GearModifierTable.MiningOre, "Mithril Mining")!
            .Score(Stats(("Mining Fortune", 400), ("Mining Speed", 1000), ("Pristine", 5))).Should().Be(500, "Pristine only counts for gemstones");
        GearScoreFormulas.For(GearModifierTable.Foraging, "Oak Foraging")!.Score(Stats(("Foraging Fortune", 200), ("Sweep", 10))).Should().Be(250);
    }

    [Test]
    public void Fishing_TrophyChanceOnlyForTrophyTasks()
    {
        var stats = Stats(("Sea Creature Chance", 20), ("Fishing Speed", 100), ("Double Hook Chance", 4), ("Trophy Chance", 30));

        GearScoreFormulas.For(GearModifierTable.FishingWater, "Water Fishing")!.Score(stats).Should().Be(200 + 100 + 20);
        GearScoreFormulas.For(GearModifierTable.FishingLava, "Crimson Hotspot Fishing")!.Score(stats).Should().Be(320);
        GearScoreFormulas.For(GearModifierTable.FishingLava, "Crimson Fishing")!.Score(stats).Should().Be(350);
    }

    [Test]
    public void Combat_LogOfTheDamageProduct()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Dungeon, "F7")!;

        formula.IsLog.Should().BeTrue();
        formula.Score(Stats(("Damage", 95), ("Strength", 100), ("Crit Damage", 50))).Should().BeApproximately(Math.Log(100 * 2 * 1.5), 1e-9);
        formula.Empty.Should().BeApproximately(Math.Log(5), 1e-9);
        formula.Extend(formula.Empty, 0.2).Should().BeApproximately(Math.Log(6), 1e-9, "20 % more of the underlying product");
        GearScoreFormulas.For(GearModifierTable.Farming, Wheat)!.Extend(1000, 0.2).Should().Be(1200);
    }

    [Test]
    public void UnboundTasks_OnlyFoughtOnesGetTheCombatFormula()
    {
        GearScoreFormulas.For(GearModifierTable.Other, "Diana")!.Kind.Should().Be(GearScoreFormula.DamageProduct);
        GearScoreFormulas.For(GearModifierTable.Other, "Not A Task").Should().BeNull("an unbound task that is not fought has no score");
        GearScorer.For("Not A Task", GearModifierTable.Other).Should().BeNull();
    }

    [Test]
    public void Scorer_SumsLoreStatsOfEveryPieceAndThePet_MissingSlotsAreZero()
    {
        var scorer = Scorer(Wheat, GearModifierTable.Farming, []);
        var full = Gear(new GearPet { Type = "TEST_PET", Stats = Stats(("Farming Fortune", 30)) },
            (GearSnapshot.Tool, Piece("TEST_HOE", ("Farming Fortune", 200), ("Wheat Fortune", 50))),
            (GearSnapshot.Helmet, Piece("TEST_HELMET", ("Farming Fortune", 40))),
            (GearSnapshot.Necklace, Piece("TEST_NECKLACE", ("Farming Fortune", 10), ("Strength", 99))));

        scorer.Score(full).Should().Be(330);
        scorer.Score(Gear(null, (GearSnapshot.Tool, Piece("TEST_HOE", ("Farming Fortune", 200))))).Should().Be(200, "unknown equipment and pet count 0");
    }

    [Test]
    public void SetBonus_TierByWornPieceCount()
    {
        var scorer = Embedded(Wheat, GearModifierTable.Farming, "The Garden");
        ObservedGear Cropie(int pieces) => Gear(null, GearSnapshot.ArmorSlots.Take(pieces).Select(s => (s, Armor("CROPIE", s))).ToArray());

        scorer.Score(Cropie(1)).Should().Be(0);
        scorer.Score(Cropie(2)).Should().Be(15);
        scorer.Score(Cropie(4)).Should().Be(45);
        scorer.Score(TestGear(pet: null, equipment: false, toolFortune: 0)).Should().Be(75, "four Fermento pieces, +75 only");
    }

    [Test]
    public void PetPerk_LinearInLevel_FromTheMinimumRarity()
    {
        var scorer = Embedded(Wheat, GearModifierTable.Farming, "The Garden");

        scorer.PetContribution(new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 100 }).Should().Be(150);
        scorer.PetContribution(new GearPet { Type = "ELEPHANT", Tier = "MYTHIC", Level = 50 }).Should().Be(75);
        scorer.PetContribution(new GearPet { Type = "ELEPHANT", Tier = "EPIC", Level = 100 }).Should().Be(0, "Trunk Efficiency is Legendary+");
        scorer.PetContribution(new GearPet { Type = "ELEPHANT", Level = 100 }).Should().Be(0, "an unknown rarity triggers nothing");
    }

    [Test]
    public void PetPerk_OfSeveralRarities_TheHighestMatchingWins()
    {
        var scorer = Embedded("Water Fishing", GearModifierTable.FishingWater);

        scorer.PetContribution(new GearPet { Type = "FLYING_FISH", Tier = "LEGENDARY", Level = 100 }).Should().Be(80, "Quick Reel 80, not 60 + 75 + 80");
        scorer.PetContribution(new GearPet { Type = "FLYING_FISH", Tier = "EPIC", Level = 100 }).Should().Be(75);
    }

    [Test]
    public void ZoneCondition_AppliesOnlyWhenAllTaskLocationsAreInTheZones()
    {
        GearEffectTable.InZones(["Crimson Isle"], ["Stronghold", "Blazing Volcano", "Crimson Isle"]).Should().BeTrue();
        GearEffectTable.InZones(["Crystal Hollows"], ["Village", "Crystal Hollows"]).Should().BeFalse("the Village is on the Hub");
        GearEffectTable.InZones(["Magma Fields"], ["Crystal Hollows"]).Should().BeFalse("the task is not limited to the Magma Fields");
        GearEffectTable.InZones(["Jerry"], ["Jerry's Workshop", "Jerry Pond"]).Should().BeTrue();
        GearEffectTable.InZones(["Jerry"], []).Should().BeFalse();

        var kuudra = new GearPet { Type = "KUUDRA", Tier = "LEGENDARY", Level = 100 };
        Embedded("Crimson Fishing", GearModifierTable.FishingLava, "Stronghold", "Crimson Isle").PetContribution(kuudra).Should().Be(20, "Trophy Bait +20 on the Crimson Isle");
        Embedded("Crimson Fishing", GearModifierTable.FishingLava, "Hub").PetContribution(kuudra).Should().Be(0);
    }

    [Test]
    public void TaskLocations_ComeFromTheDetectionSignature()
    {
        GearCategories.LocationsOf("Crimson Fishing").Should().Contain("Stronghold");
        GearCategories.LocationsOf("Not A Task").Should().BeEmpty();
    }

    [Test]
    public void Multiplier_ScalesTheBuildTotals_AfterTheAdditiveEffects()
    {
        var yeti = new GearEffect
        {
            Id = "test-yeti", Kind = GearEffect.KindPet, Mode = GearEffect.Multiply, PetType = "BABY_YETI", MinRarity = "LEGENDARY",
            Stats = ["Fishing Speed", "Sea Creature Chance"], Value = 0.2, Source = "https://example.org"
        };
        var perk = new GearEffect
        {
            Id = "test-perk", Kind = GearEffect.KindPet, PetType = "BABY_YETI", MinRarity = "COMMON", Stats = ["Fishing Speed"], Value = 50,
            Source = "https://example.org"
        };
        var scorer = Scorer("Winter Fishing", GearModifierTable.FishingWater, ["Jerry's Workshop"], yeti, perk);
        var gear = Gear(new GearPet { Type = "BABY_YETI", Tier = "LEGENDARY", Level = 100 },
            (GearSnapshot.Tool, Piece("TEST_ROD", ("Fishing Speed", 150), ("Sea Creature Chance", 10))));

        var totals = scorer.Totals(gear);

        totals["Fishing Speed"].Should().BeApproximately((150 + 50) * 1.2, 1e-9);
        totals["Sea Creature Chance"].Should().BeApproximately(12, 1e-9);
    }

    [Test]
    public void PieceMultiplier_ScalesOnlyTheTargetPiecesStats()
    {
        var scorer = Embedded("Crimson Fishing", GearModifierTable.FishingLava, "Crimson Isle");
        var gear = Gear(new GearPet { Type = "FLYING_FISH", Tier = "MYTHIC", Level = 100 },
            (GearSnapshot.Helmet, Piece("MAGMA_LORD_HELMET", ("Sea Creature Chance", 4.5))),
            (GearSnapshot.Tool, Piece("HELLFIRE_ROD", ("Sea Creature Chance", 14))));

        scorer.Totals(gear)["Sea Creature Chance"].Should().BeApproximately(4.5 * 1.2 + 14, 1e-9, "Magmatic Diver: +20 % of the Magma Lord stats");
    }

    [Test]
    public void ToolTypeCondition_ScathaOnlyWithADrill()
    {
        var scorer = Embedded("Gemstone Mining", GearModifierTable.MiningGem);
        var scatha = new GearPet { Type = "SCATHA", Tier = "LEGENDARY", Level = 100 };
        ObservedGear With(string itemType) => Gear(scatha, (GearSnapshot.Tool, new GearPiece { Tag = "TEST_TOOL", ItemType = itemType }));

        scorer.Score(With("DRILL")).Should().Be(125);
        scorer.Score(With("GAUNTLET")).Should().Be(0);
    }

    [Test]
    public void SetShare_IsTheFullBonusSplitOverTheTiers_BeyondTheOnePieceTier()
    {
        var scorer = Embedded(Wheat, GearModifierTable.Farming, "The Garden");
        var mining = Embedded("Mithril Mining", GearModifierTable.MiningOre);

        scorer.SetShare(GearSnapshot.Helmet, Armor("FERMENTO", GearSnapshot.Helmet)).Should().Be(18.75);
        scorer.PieceContribution(GearSnapshot.Helmet, Armor("FERMENTO", GearSnapshot.Helmet)).Should().Be(0);
        mining.PieceContribution(GearSnapshot.Helmet, Armor("GLOSSY_MINERAL", GearSnapshot.Helmet)).Should().Be(75, "one Glossy piece: 150 spread × 0.5");
        mining.SetShare(GearSnapshot.Helmet, Armor("GLOSSY_MINERAL", GearSnapshot.Helmet)).Should().Be(0, "600 / 4 = the one piece tier");
    }

    [Test]
    public void EmbeddedTable_LoadsAndEveryRowHasASource()
    {
        var effects = GearEffectTable.Embedded.Effects;

        effects.Should().NotBeEmpty();
        effects.Should().OnlyContain(e => e.Source.StartsWith("https://") && e.Confidence != "" && e.Stats.Count > 0);
        effects.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }

    [TestCase("""{"effects":[{"id":"a","kind":"pet","petType":"X","minRarity":"RARE","stats":["Sweep"],"value":1}]}""", "no source")]
    [TestCase("""{"effects":[{"id":"a","kind":"aura","stats":["Sweep"],"source":"https://x"}]}""", "unknown kind")]
    [TestCase("""{"effects":[{"id":"a","kind":"set","stats":["Sweep"],"tags":["X"],"source":"https://x"}]}""", "tiers")]
    [TestCase("""{"effects":[{"id":"a","kind":"pet","petType":"X","minRarity":"SHINY","stats":["Sweep"],"source":"https://x"}]}""", "minRarity")]
    [TestCase("""{"effects":[{"id":"a","kind":"equipment","tags":["X"],"stats":["Sweep"],"source":"https://x"},{"id":"a","kind":"equipment","tags":["Y"],"stats":["Sweep"],"source":"https://x"}]}""", "twice")]
    public void Load_InvalidRows_Throw(string json, string problem)
    {
        var load = () => GearEffectTable.Load(json);

        load.Should().Throw<InvalidDataException>().WithMessage($"*{problem}*");
    }
}
