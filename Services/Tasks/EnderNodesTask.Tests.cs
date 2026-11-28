using System.Collections.Generic;
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
