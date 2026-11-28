using System;
using System.Collections.Generic;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Ender Nodes classification boundary against the other End tasks, with periods from production (2026-11-26..28) and
/// Bazaar sell prices of 2026-11-28. Before the task existed, node periods went to Zealots (FD) through their pearls.
/// </summary>
public class EnderNodesTaskTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static readonly Dictionary<string, double> Prices = new()
    {
        { "MITE_GEL", 1_800 }, { "ENCHANTED_ENDSTONE", 266 }, { "ENDER_STONE", 0.1 }, { "ENCHANTED_ENDER_PEARL", 117 },
        { "ENDER_PEARL", 3.2 }, { "GRAND_EXP_BOTTLE", 4_394 }, { "TITANIC_EXP_BOTTLE", 576_416 }, { "ENCHANTED_OBSIDIAN", 2_400 },
        { "OBSIDIAN", 10 }, { "SUMMONING_EYE", 1_200_000 }, { "NULL_SPHERE", 110_000 }, { "CRYSTAL_FRAGMENT", 186_000 },
        { "WISE_FRAGMENT", 13_000 }, { "PROTECTOR_FRAGMENT", 6_000 }, { "SHARD_ZEALOT", 1_000 }, { "SHARD_SPECIAL_ZEALOT", 40_000 },
        { "ENDSTONE_GEODE", 0 }, { "END_STONE_SHULKER", 0.1 }
    };

    private static string Classify(string zone, Dictionary<string, int> items, double minutes = 5)
        => Classifier.Classify(zone, items, minutes, prices: Prices)?.TaskName;

    // TeneStorm1, a dedicated node miner (~900 Mite Gel/h)
    private static Dictionary<string, int> NodePeriod() => new()
    {
        { "GRAND_EXP_BOTTLE", 20 }, { "ENCHANTED_ENDSTONE", 98 }, { "ENDER_STONE", 29 }, { "ENCHANTED_ENDER_PEARL", 37 },
        { "ENCHANTED_OBSIDIAN", 7 }, { "MITE_GEL", 89 }, { "ENDER_GAUNTLET", 1 }, { "END_STONE_SHULKER", 1 }, { "ENDER_PEARL", -4 }
    };

    [Test]
    [TestCase("The End")]
    [TestCase("Dragon's Nest")]
    [TestCase("Void Slate")]
    public void NodeMiningPeriod_IsEnderNodes_NotTheZealotsOrTheDragon(string zone)
        => Classify(zone, NodePeriod()).Should().Be("Ender Nodes");

    [Test]
    public void LightNodeMiningWithEndermanPearls_IsEnderNodes()
    {
        // production: was Zealots (FD) through 29 Enchanted End Stone and 13 pearls
        Classify("The End", new() { { "MITE_GEL", 29 }, { "ENCHANTED_ENDSTONE", 36 }, { "ENDER_PEARL", 3 }, { "ENCHANTED_ENDER_PEARL", 1 } })
            .Should().Be("Ender Nodes");
        Classify("The End", new() { { "ENDER_PEARL", 129 }, { "MITE_GEL", 24 }, { "ENCHANTED_ENDSTONE", 29 }, { "ENCHANTED_ENDER_PEARL", 8 } })
            .Should().Be("Ender Nodes");
    }

    [Test]
    public void StonkNodeMiningWithoutMiteGel_IsEnderNodes()
        => Classify("The End", new() { { "ENDER_STONE", 15 }, { "ENCHANTED_ENDER_PEARL", 10 }, { "ENCHANTED_ENDSTONE", 2 }, { "GRAND_EXP_BOTTLE", 5 } })
            .Should().Be("Ender Nodes");

    [Test]
    public void DragonFightWithAFewMiteGel_StaysEnderDragon()
    {
        Classify("Dragon's Nest", new() { { "ENDER_PEARL", 84 }, { "ENCHANTED_ENDSTONE", 4 }, { "WISE_FRAGMENT", 8 }, { "MITE_GEL", 3 } })
            .Should().Be("Ender Dragon");
        Classify("The End", new() { { "ENDER_PEARL", 48 }, { "ENCHANTED_ENDSTONE", 29 }, { "GRAND_EXP_BOTTLE", 2 }, { "MITE_GEL", 26 }, { "WISE_FRAGMENT", 4 } }, 10)
            .Should().Be("Ender Dragon");
    }

    [Test]
    public void ZealotFarmingWithAFewMiteGel_StaysWithTheZealots()
    {
        Classify("Dragon's Nest", new() { { "ENDER_PEARL", 400 }, { "SUMMONING_EYE", 1 }, { "MITE_GEL", 4 } }).Should().Be("Zealots (FD)");
        Classify("Dragon's Nest", new() { { "ENDER_PEARL", 1562 }, { "MITE_GEL", 4 }, { "SHARD_SPECIAL_ZEALOT", 3 }, { "SHARD_ZEALOT", 2 } })
            .Should().NotBe("Ender Nodes");
    }

    /// <summary>Review probes: one Mite Gel (1.8k) outvalued ~560 pearls of an eye-less, shard-less zealot period.</summary>
    [Test]
    public void PearlGrindingWithALittleMiteGel_IsNotEnderNodes()
    {
        Classify("Dragon's Nest", new() { { "ENDER_PEARL", 400 }, { "ENCHANTED_ENDER_PEARL", 3 }, { "MITE_GEL", 1 } }).Should().Be("Zealots (FD)");
        Classify("Dragon's Nest", new() { { "ENDER_PEARL", 1562 }, { "MITE_GEL", 4 } }).Should().Be("Zealots (FD)");
        Classify("The End", new() { { "ENCHANTED_ENDER_PEARL", 20 }, { "MITE_GEL", 2 } }).Should().Be("Zealots (FD)");
        Classify("Void Sepulture", new() { { "ENDER_PEARL", 200 }, { "MITE_GEL", 1 }, { "ENCHANTED_ENDSTONE", 3 } }).Should().Be("Zealots (FD)");
    }

    [Test]
    public void NodeMiningWithEndermanPearls_BelowTheRatio_StaysEnderNodes()
    {
        // production: 195 pearls + 4 Enchanted Ender Pearls next to 23 Mite Gel and 4 bottles = 10 raw pearls per node drop
        Classify("The End", new() { { "ENDER_PEARL", 195 }, { "ENCHANTED_ENDER_PEARL", 4 }, { "MITE_GEL", 23 }, { "GRAND_EXP_BOTTLE", 4 }, { "ENDER_GAUNTLET", 1 } })
            .Should().Be("Ender Nodes");
        // 62.5 raw pearls per node drop, but 40 Mite Gel in 5 minutes (240/h when rated over the 10 minute floor) is node mining
        Classify("The End", new() { { "ENCHANTED_ENDER_PEARL", 125 }, { "MITE_GEL", 40 } }).Should().Be("Ender Nodes");
    }

    [Test]
    public void StonkNodeMiningWithNodeOnlyLoot_StaysEnderNodes_DespiteItsPearls()
    {
        // production: 366 raw pearls per 4 bottles, but a geode and a Magical Rune only come from nodes
        Classify("The End", new()
        {
            { "ENCHANTED_ENDER_PEARL", 15 }, { "ENDER_PEARL", 66 }, { "ENCHANTED_OBSIDIAN", 4 }, { "ENDSTONE_GEODE", 1 },
            { "ENCHANTED_ENDSTONE", 3 }, { "RUNE_MAGIC", 1 }, { "GRAND_EXP_BOTTLE", 4 }
        }).Should().Be("Ender Nodes");
        Classify("Void Slate", new() { { "ENCHANTED_ENDSTONE", 6 }, { "ENCHANTED_OBSIDIAN", 4 }, { "ENDER_GAUNTLET", 1 }, { "GRAND_EXP_BOTTLE", 3 }, { "ENCHANTED_ENDER_PEARL", 21 } })
            .Should().Be("Ender Nodes");
    }

    /// <summary>Second review: Stonk node mining without rare loot went to Zealots (FD) through its Enchanted Ender Pearls.</summary>
    [Test]
    [TestCase("The End")]
    [TestCase("Void Slate")]
    public void StonkNodeMiningWithoutRareLoot_IsEnderNodes(string zone)
    {
        var items = new Dictionary<string, int>
        {
            { "ENCHANTED_ENDSTONE", 6 }, { "ENCHANTED_OBSIDIAN", 4 }, { "GRAND_EXP_BOTTLE", 3 }, { "ENCHANTED_ENDER_PEARL", 21 }
        };
        Classify(zone, items).Should().Be("Ender Nodes");
        EmptyChumBucketsTaskTests.ClassifyAsSession(Classifier, zone, items, Prices).Should().Be("Ender Nodes");
    }

    [Test]
    public void PearlGrindingSession_IsNotEnderNodes()
        => EmptyChumBucketsTaskTests.ClassifyAsSession(Classifier, "Void Sepulture",
            new() { { "ENDER_PEARL", 200 }, { "MITE_GEL", 1 }, { "ENCHANTED_ENDSTONE", 3 } }, Prices).Should().Be("Zealots (FD)");

    [Test]
    public void PersonalView_LeavesOutPearlGrinding()
    {
        Period Make(Dictionary<string, int> items) => new()
        {
            Location = "The End", ItemsCollected = items, PlayerUuid = "test",
            StartTime = new DateTime(2026, 11, 28, 12, 0, 0), EndTime = new DateTime(2026, 11, 28, 12, 5, 0)
        };
        var task = (MethodTask)Registry.GetByName("Ender Nodes");
        var matched = task.FindMatchingPeriodsForAggregation(new TaskParams
        {
            TestTime = new DateTime(2026, 11, 28, 12, 10, 0),
            LocationProfit = new() { { "The End", [Make(NodePeriod()), Make(new() { { "ENDER_PEARL", 400 }, { "MITE_GEL", 1 } })] } }
        });
        matched.Should().ContainSingle().Which.ItemsCollected.Should().ContainKey("ENDER_GAUNTLET");
    }

    [Test]
    public void VoidgloomFightWithAFewMiteGel_StaysT4Voidglooms()
        => Classify("Void Sepulture", new() { { "NULL_SPHERE", 30 }, { "ENDER_PEARL", 200 }, { "MITE_GEL", 3 } }).Should().Be("T4 Voidglooms");

    [Test]
    public void BulkBlockMiningWithAStrayNode_StaysBlockMining()
    {
        // nodes sit in the End Stone and Obsidian: their Mite Gel used to block the bulk claim of the block mining tasks
        Classify("The End", new() { { "ENCHANTED_ENDSTONE", 2000 }, { "MITE_GEL", 5 } }, 10).Should().Be("End Stone Mining");
        Classify("The End", new() { { "ENCHANTED_OBSIDIAN", 40 }, { "MITE_GEL", 4 }, { "ENDER_GAUNTLET", 1 } }, 10).Should().Be("Obsidian Mining");
    }

    [Test]
    public void FightDropVeto_IsLiftedOnlyByNodeMiningVolume()
    {
        // 60 Mite Gel in 10 minutes is node mining with one Ender Chest Zealot on the way, 3 are a node broken in a fight
        Classify("The End", new() { { "MITE_GEL", 60 }, { "CRYSTAL_FRAGMENT", 1 }, { "ENCHANTED_ENDER_PEARL", 5 } }, 10).Should().Be("Ender Nodes");
        Classify("The End", new() { { "MITE_GEL", 3 }, { "CRYSTAL_FRAGMENT", 1 }, { "ENCHANTED_ENDER_PEARL", 5 } }, 10).Should().NotBe("Ender Nodes");
    }

    [Test]
    public void NodeLootOutsideTheEnd_IsNotEnderNodes()
        => Classify("Your Island", new() { { "GRAND_EXP_BOTTLE", 3 }, { "TITANIC_EXP_BOTTLE", 1 } }).Should().NotBe("Ender Nodes");

    [Test]
    public void FormulaRate_IsAPlausibleNodeMiningRate()
    {
        var task = (MethodTask)Registry.GetByName("Ender Nodes");
        // 480x1,800 + 520x266 + 160x117 + 80x4,394 + 40x2,400 + 1.5x576,416 = ~2.33M/h
        TaskEstimator.FormulaRate(task, Prices).Should().BeInRange(2_000_000, 2_700_000);
    }
}
