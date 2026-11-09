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
        var formula = GearScoreFormulas.For(GearModifierTable.Other, "Diana")!;

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
        var mining = GearScorer.For("Coal Mining", GearModifierTable.MiningOre)!;

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

    /// <summary>Regression: dungeon mage builds (Intelligence and Ability Damage only) scored 0.</summary>
    [Test]
    public void Dungeon_TheHigherOfMeleeAndMage()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Dungeon, "F7")!;

        formula.Kind.Should().Be(GearScoreFormula.DamageOrMage);
        formula.IsLog.Should().BeTrue();
        formula.Score(Stats(("Intelligence", 1000), ("Ability Damage", 45))).Should().BeApproximately(Math.Log(1000 * 3 * 1.45), 1e-9);
        formula.Score(Stats(("Damage", 295), ("Strength", 900), ("Crit Damage", 400))).Should().BeApproximately(Math.Log(300 * 10 * 5), 1e-9);
        formula.Empty.Should().BeApproximately(Math.Log(5), 1e-9, "the mage branch only applies with Intelligence");
        GearScoreFormulas.For(GearModifierTable.Kuudra, "Kuudra T5")!.Kind.Should().Be(GearScoreFormula.DamageOrMage);
    }

    /// <summary>Regression: the mage branch's empty build (ln 1000) was a floor that flattened every weak melee build.</summary>
    [Test]
    public void Dungeon_WeakMeleeBuilds_KeepTheirGradient()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Dungeon, "F2")!;

        var weak = formula.Score(Stats(("Damage", 150), ("Strength", 100), ("Crit Damage", 50), ("Intelligence", 50)));
        var stronger = formula.Score(Stats(("Damage", 200), ("Strength", 150), ("Crit Damage", 80), ("Intelligence", 50)));

        weak.Should().BeApproximately(Math.Log(155 * 2 * 1.5), 1e-9, "Intelligence 50 from reforges gives a mage score of ln 150 = 5.0");
        weak.Should().BeLessThan(stronger);
        stronger.Should().BeLessThan(Math.Log(1150), "the gated floor max(melee, ln 1150) is gone");
    }

    /// <summary>Regression: with Intelligence 50 (reforges) the old mage branch floored melee builds at ln 1150 = 7.05.</summary>
    [Test]
    public void Dungeon_MeleeBuildWithALittleIntelligence_ScoresItsMeleeValue()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Dungeon, "M7")!;
        var melee = Stats(("Damage", 330), ("Strength", 600), ("Crit Damage", 400));

        var withIntelligence = formula.Score(Stats(("Damage", 330), ("Strength", 600), ("Crit Damage", 400), ("Intelligence", 50)));

        withIntelligence.Should().BeApproximately(formula.Score(melee), 1e-9);
        withIntelligence.Should().BeApproximately(Math.Log(335 * 7 * 5), 1e-9, "melee endgame about 9.4");
    }

    [Test]
    public void Dungeon_PureMageBuild_LandsInTheMeleeEndgameRange()
    {
        var scorer = new GearScorer(GearScoreFormulas.For(GearModifierTable.Dungeon, "M7")!, []);
        var build = Gear(new GearPet { Type = "TEST_SHEEP", Stats = Stats(("Intelligence", 100)) },
            (GearSnapshot.Tool, Piece("HYPERION", ("Damage", 260), ("Strength", 150), ("Intelligence", 500))),
            (GearSnapshot.Helmet, Piece("WITHER_GOGGLES", ("Intelligence", 400), ("Ability Damage", 45))),
            (GearSnapshot.Chestplate, Piece("WISE_WITHER_CHESTPLATE", ("Intelligence", 400))),
            (GearSnapshot.Leggings, Piece("WISE_WITHER_LEGGINGS", ("Intelligence", 400))),
            (GearSnapshot.Boots, Piece("WISE_WITHER_BOOTS", ("Intelligence", 300))));

        var score = scorer.Score(build);

        score.Should().BeApproximately(Math.Log(1000 * 0.3 * 21 * 1.45), 1e-9, "Intelligence 2100 and Ability Damage 45");
        score.Should().BeInRange(9, 9.5);
    }

    [Test]
    public void Dungeon_ScoreIsMonotonicInIntelligence()
    {
        var formula = GearScoreFormulas.For(GearModifierTable.Dungeon, "F7")!;
        var scores = Enumerable.Range(0, 61).Select(i => formula.Score(Stats(("Damage", 150), ("Strength", 100), ("Crit Damage", 50),
            ("Ability Damage", 45), ("Intelligence", 50 * i)))).ToList();

        scores.Zip(scores.Skip(1)).Should().OnlyContain(p => p.Second >= p.First - 1e-12);
        scores[^1].Should().BeGreaterThan(scores[0], "enough Intelligence makes the mage branch win");
    }

    [Test]
    public void Dungeon_MagePieces_ContributeScore_MeleePiecesTheirMeleeShare()
    {
        var scorer = new GearScorer(GearScoreFormulas.For(GearModifierTable.Dungeon, "F7")!, []);
        var wiseHelmet = Piece("WISE_WITHER_HELMET", ("Intelligence", 400));
        var necronHelmet = Piece("POWER_WITHER_HELMET", ("Strength", 100), ("Crit Damage", 50));

        scorer.PieceContribution(GearSnapshot.Helmet, wiseHelmet).Should().BeApproximately(Math.Log(1000 * 1.2) - Math.Log(5), 1e-9,
            "the mage score of Intelligence 400 above the empty build");
        scorer.PieceContribution(GearSnapshot.Helmet, necronHelmet).Should().BeApproximately(Math.Log(2 * 1.5), 1e-9,
            "its melee score above the empty build: no Intelligence, no mage branch");
    }

    [Test]
    public void Dungeon_Recommender_PicksAPureBranchBuild()
    {
        var scorer = new GearScorer(GearScoreFormulas.For(GearModifierTable.Dungeon, "F7")!, []);
        GearCatalogEntry Entry(string slot, GearPiece piece) => new($"{slot}:{piece.Tag}", slot, piece, null, 2,
            scorer.PieceContribution(slot, piece), 0, null, []);
        var catalog = new List<GearCatalogEntry>
        {
            Entry(GearSnapshot.Tool, Piece("HYPERION", ("Damage", 260), ("Strength", 150), ("Intelligence", 350))),
            Entry(GearSnapshot.Tool, Piece("TERMINATOR", ("Damage", 310), ("Strength", 50), ("Crit Damage", 250))),
            Entry(GearSnapshot.Helmet, Piece("WISE_WITHER_HELMET", ("Intelligence", 400))),
            Entry(GearSnapshot.Helmet, Piece("POWER_WITHER_HELMET", ("Strength", 100), ("Crit Damage", 50))),
            Entry(GearSnapshot.Chestplate, Piece("WISE_WITHER_CHESTPLATE", ("Intelligence", 250))),
            Entry(GearSnapshot.Chestplate, Piece("POWER_WITHER_CHESTPLATE", ("Strength", 120), ("Crit Damage", 60))),
        };

        var build = GearBuildRecommender.Recommend(catalog, scorer)!;
        var melee = Gear(null, (GearSnapshot.Tool, catalog[1].Piece!), (GearSnapshot.Helmet, catalog[3].Piece!), (GearSnapshot.Chestplate, catalog[5].Piece!));
        var mage = Gear(null, (GearSnapshot.Tool, catalog[0].Piece!), (GearSnapshot.Helmet, catalog[2].Piece!), (GearSnapshot.Chestplate, catalog[4].Piece!));

        build.Score.Should().BeApproximately(Math.Max(scorer.Score(melee), scorer.Score(mage)), 1e-9);
        build.Score.Should().BeGreaterThan(scorer.Score(Gear(null, (GearSnapshot.Tool, catalog[1].Piece!), (GearSnapshot.Helmet, catalog[2].Piece!),
            (GearSnapshot.Chestplate, catalog[4].Piece!))), "a melee tool with mage armor adds up to neither branch");
    }

    [Test]
    public void Dungeon_PublishedExpression_NamesBothBranches()
    {
        var info = GearModelPublisher.Formula(GearScoreFormulas.For(GearModifierTable.Dungeon, "M7")!, 0);

        info.Expression.Should().Be("max(ln((5 + Damage) × (1 + Strength / 100) × (1 + Crit Damage / 100)), "
            + "ln(1000 × 0.3 × Intelligence / 100 × (1 + Ability Damage / 100)) if Intelligence > 0)");
        info.Weights.Keys.Should().Contain(["Intelligence", "Ability Damage"]);
    }

    /// <summary>Regression: Mineral armor's only stat, Block Fortune, scored 0; Ore Fortune was unused.</summary>
    [TestCase("Coal Mining", "Ore Fortune")]
    [TestCase("Iron Mining", "Ore Fortune")]
    [TestCase("Gold Mining", "Ore Fortune")]
    [TestCase("Lapis Mining", "Ore Fortune")]
    [TestCase("Redstone Mining", "Ore Fortune")]
    [TestCase("Emerald Mining", "Ore Fortune")]
    [TestCase("Diamond Mining", "Ore Fortune")]
    [TestCase("Mithril Mining", "Dwarven Metal Fortune")]
    [TestCase("Umber Mining", "Dwarven Metal Fortune")]
    [TestCase("Tungsten Mining", "Dwarven Metal Fortune")]
    [TestCase("Glacite Mining", "Dwarven Metal Fortune")]
    [TestCase("Cobblestone Mining", "Block Fortune")]
    [TestCase("End Stone Mining", "Block Fortune")]
    [TestCase("Obsidian Mining", "Block Fortune")]
    public void MiningOre_TheTaskBlocksFortuneAddsToMiningFortune(string task, string fortune)
    {
        MiningBlocks.ForTask(task)!.FortuneStat.Should().Be(fortune);
        var formula = GearScoreFormulas.For(GearModifierTable.MiningOre, task)!;

        formula.Score(Stats(("Mining Fortune", 400), (fortune, 100), ("Mining Speed", 1000))).Should().Be(600);
        formula.Weights.Keys.Count(k => k.EndsWith(" Fortune") && k != "Mining Fortune").Should().Be(1, "only the task's own subtype");
    }

    [Test]
    public void MiningBlocks_EveryMiningOreTaskHasAKind_GemstonesNone()
    {
        var bound = new[] { "Coal Mining", "Redstone Mining", "Diamond Mining", "Gold Mining", "Iron Mining", "Lapis Mining", "Emerald Mining",
            "Cobblestone Mining", "End Stone Mining", "Obsidian Mining", "Mithril Mining", "Umber Mining", "Tungsten Mining", "Glacite Mining" };

        bound.Should().OnlyContain(t => GearCategories.ForTask(t) == GearModifierTable.MiningOre && MiningBlocks.ForTask(t) != null);
        MiningBlocks.ForTask("Ruby Mining").Should().BeNull();
        MiningBlocks.OfItem("ENCHANTED_GOLD_BLOCK").Should().Be(MiningBlocks.OreKind);
        MiningBlocks.OfItem("TITANIUM_ORE").Should().Be(MiningBlocks.DwarvenMetalKind);
        MiningBlocks.OfItem("ENCHANTED_ENDSTONE").Should().Be(MiningBlocks.BlockKind);
        MiningBlocks.Resolve("Hard Stone Mining", ["UNKNOWN_ITEM"]).Should().Be(MiningBlocks.BlockKind, "the name when the items say nothing");
    }

    [Test]
    public void MineralSets_MiningSpreadOnOresAndBlocks_NotOnDwarvenMetals()
    {
        GearPiece Mineral(string set, string slot) => Armor(set, slot);
        ObservedGear FullSet(string set) => Gear(null, GearSnapshot.ArmorSlots.Select(s => (s, Mineral(set, s))).ToArray());

        GearScorer.For("Coal Mining", GearModifierTable.MiningOre)!.Score(FullSet("MINERAL")).Should().Be(200, "400 spread x 0.5");
        GearScorer.For("Cobblestone Mining", GearModifierTable.MiningOre)!.Score(FullSet("GLOSSY_MINERAL")).Should().Be(300);
        GearScorer.For("Mithril Mining", GearModifierTable.MiningOre)!.Score(FullSet("GLOSSY_MINERAL")).Should().Be(0);
        var blockFortune = Gear(null, (GearSnapshot.Helmet, Piece("MINERAL_HELMET", ("Block Fortune", 25))));
        GearScorer.For("Cobblestone Mining", GearModifierTable.MiningOre)!.PieceContribution(GearSnapshot.Helmet, blockFortune.Slots[GearSnapshot.Helmet])
            .Should().Be(25 + 50, "Block Fortune 25 plus the one piece spread 100 x 0.5");
    }

    [Test]
    public void PetEffects_BabyYetiFromEpic_MonkeyByRarity_RoseDragonGardenPower()
    {
        var jerry = new GearScorer(GearScoreFormulas.For(GearModifierTable.FishingWater, "Jerry Fishing")!,
            GearEffectTable.Embedded.For(new GearTaskContext("Jerry Fishing", GearModifierTable.FishingWater, ["Jerry's Workshop"])));
        var rod = Gear(new GearPet { Type = "BABY_YETI", Tier = "EPIC", Level = 100 }, (GearSnapshot.Tool, Piece("ROD", ("Fishing Speed", 100))));
        jerry.Score(rod).Should().BeApproximately(120, 1e-9, "Cold Breeze x1.2 from Epic");
        rod.Pet!.Tier = "RARE";
        jerry.Score(rod).Should().BeApproximately(110, 1e-9);

        var foraging = Embedded("Oak Foraging", GearModifierTable.Foraging);
        new[] { "COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY" }
            .Select(t => foraging.PetContribution(new GearPet { Type = "MONKEY", Tier = t, Level = 100 }))
            .Should().Equal(40, 50, 50, 60, 60);

        var farming = Embedded(Wheat, GearModifierTable.Farming, "The Garden");
        farming.PetContribution(new GearPet { Type = "ROSE_DRAGON", Tier = "LEGENDARY", Level = 200 }).Should().Be(180, "3 per Farming level at 60");
        farming.PetContribution(new GearPet { Type = "ROSE_DRAGON", Tier = "LEGENDARY", Level = 101 }).Should().BeApproximately(90.9, 1e-9);
    }

    [Test]
    public void RanchersBoots_SixtyFarmingFortuneOnTheGarden()
    {
        var boots = Piece("RANCHERS_BOOTS");

        GearScorer.For(Wheat, GearModifierTable.Farming)!.PieceContribution(GearSnapshot.Boots, boots).Should().Be(60, "Farming level 60 assumed");
        Scorer("Hub Wheat", GearModifierTable.Farming, ["Hub"], GearEffectTable.Embedded.Effects.ToArray())
            .PieceContribution(GearSnapshot.Boots, boots).Should().Be(0, "only on the Garden");
    }
}
