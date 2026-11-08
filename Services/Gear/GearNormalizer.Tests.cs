using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearNormalizerTests
{
    [Test]
    public void Hoe_Farming_KeepsFarmingModifiers_AndOnlyTheTaskCropsTurbo()
    {
        var piece = GearNormalizer.Normalize(GearTestItems.Hoe(), GearModifierTable.Farming, "Wheat Farming")!;

        piece.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
        piece.Rarity.Should().Be("LEGENDARY");
        piece.ItemType.Should().Be("FARMING TOOL");
        piece.Reforge.Should().Be("blessed");
        piece.HotPotatoCount.Should().Be(15);
        piece.Recombobulated.Should().BeTrue();
        piece.Stars.Should().BeNull();
        piece.Enchants.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["harvesting"] = 6, ["cultivating"] = 8, ["dedication"] = 4, ["turbo_wheat"] = 5, ["replenish"] = 1
        });
        piece.Counters.Should().BeEquivalentTo(new Dictionary<string, long> { ["farmed_cultivating"] = 1_234_567, ["mined_crops"] = 905_311 });
    }

    [Test]
    public void Hoe_WithoutTask_KeepsEveryTurbo()
    {
        var piece = GearNormalizer.Normalize(GearTestItems.Hoe(), GearModifierTable.Farming, null)!;

        piece.Enchants.Should().ContainKeys("turbo_wheat", "turbo_carrot");
        piece.Enchants.Should().NotContainKey("experience");
    }

    [Test]
    public void Capture_KeepsTheUnion_AndFilterNarrowsIt()
    {
        var captured = GearNormalizer.Normalize(GearTestItems.Drill(compact: 1500))!;
        captured.Enchants.Should().ContainKeys("efficiency", "fortune", "ultimate_flowstate", "pristine");
        captured.Enchants.Should().NotContainKey("experience");

        var ore = GearNormalizer.Filter(captured, GearModifierTable.MiningOre, "Mithril Mining");
        ore.Enchants.Should().NotContainKey("pristine", "pristine only matters for gemstones");
        GearNormalizer.Filter(captured, GearModifierTable.MiningGem, "Ruby Mining").Enchants.Should().ContainKey("pristine");
    }

    [Test]
    public void Drill_PartsGemsAndCounters()
    {
        var piece = GearNormalizer.Normalize(GearTestItems.Drill(compact: 1500), GearModifierTable.MiningGem, "Ruby Mining")!;

        piece.Rarity.Should().Be("EPIC", "the pipeline's numeric tier 4 is Coflnet Tier.EPIC");
        piece.Parts.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["drill_part_engine"] = "MITHRIL_DRILL_ENGINE",
            ["drill_part_fuel_tank"] = "MITHRIL_FUEL_TANK",
            ["drill_part_upgrade_module"] = "GOBLIN_OMELETTE"
        });
        piece.Gems.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["JADE_0"] = "FINE_JADE_GEM",
            ["AMBER_0"] = "PERFECT_AMBER_GEM",
            ["MINING_0"] = "FLAWLESS_TOPAZ_GEM"
        });
        piece.Counters.Should().BeEquivalentTo(new Dictionary<string, long> { ["compact_blocks"] = 1500 });
    }

    [Test]
    public void Rod_PartsNestedOrFlattened_AndFishingEnchants()
    {
        var piece = GearNormalizer.Normalize(GearTestItems.Rod(), GearModifierTable.FishingWater, "Water Fishing")!;

        piece.Parts.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["line"] = "SPEEDY_LINE", ["hook"] = "COMMON_HOOK", ["sinker"] = "JUNK_SINKER"
        });
        piece.Enchants.Should().BeEquivalentTo(new Dictionary<string, int> { ["angler"] = 6, ["lure"] = 6, ["expertise"] = 7, ["blessing"] = 6 });
        piece.Counters.Should().BeEquivalentTo(new Dictionary<string, long> { ["expertise_kills"] = 3000 });
        piece.ItemType.Should().Be("FISHING ROD");
    }

    [Test]
    public void Combat_KeepsTagRarityReforgeAndStarsOnly()
    {
        var piece = GearNormalizer.Normalize(GearTestItems.Scythe(), GearModifierTable.Dungeon, "F7")!;

        piece.Stars.Should().Be(5);
        piece.Reforge.Should().Be("heroic");
        piece.Rarity.Should().Be("LEGENDARY");
        piece.Enchants.Should().BeEmpty();
        piece.Gems.Should().BeEmpty();
        piece.HotPotatoCount.Should().BeNull();
        piece.Counters.Should().BeEmpty();
    }

    [Test]
    public void KafkaJson_JTokenValues_AreRead()
    {
        // what Kafka's json deserialization leaves in ExtraAttributes: JObject compounds and JValue numbers
        const string json = """
        {"tag":"MITHRIL_DRILL_2","description":"\n§9§lRARE DRILL","extraAttributes":{"drill_part_engine":"mithril_drill_engine",
         "compact_blocks":12345,"tier":"RARE","rarity_upgrades":1,
         "gems":{"JADE_0":{"quality":"FINE","uuid":"x"},"unlocked_slots":["JADE_0"]}},
         "enchantments":{"efficiency":5}}
        """;
        var item = JsonConvert.DeserializeObject<Item>(json)!;

        var piece = GearNormalizer.Normalize(item, GearModifierTable.MiningGem, null)!;

        piece.Rarity.Should().Be("RARE");
        piece.Parts["drill_part_engine"].Should().Be("MITHRIL_DRILL_ENGINE");
        piece.Gems["JADE_0"].Should().Be("FINE_JADE_GEM");
        piece.Counters["compact_blocks"].Should().Be(12345);
        piece.Recombobulated.Should().BeTrue();
        piece.Enchants["efficiency"].Should().Be(5);
    }

    /// <summary>Regression: integer types other than long/int/short/byte (ushort from MessagePack's smallest encoding) were read as null.</summary>
    [Test]
    public void MessagePackRoundTrip_SmallIntegerTypes_AreRead()
    {
        var item = new Item
        {
            Tag = "MITHRIL_DRILL_2", Description = "\n§9§lRARE DRILL",
            ExtraAttributes = new() { ["compact_blocks"] = 1000, ["hot_potato_count"] = 10, ["farming_for_dummies_count"] = 5, ["tier"] = 3 }
        };

        var copy = MessagePack.MessagePackSerializer.Deserialize<Item>(MessagePack.MessagePackSerializer.Serialize(item));
        var piece = GearNormalizer.Normalize(copy)!;

        piece.Counters["compact_blocks"].Should().Be(1000);
        piece.HotPotatoCount.Should().Be(10);
        piece.Applied["farming_for_dummies_count"].Should().Be(5);
        piece.Rarity.Should().Be("RARE");
    }

    [TestCase((ushort)1000)]
    [TestCase((short)1000)]
    [TestCase((uint)1000)]
    [TestCase((ulong)1000)]
    [TestCase('\u03E8')]
    public void ToLong_ReadsEveryIntegerType(object value)
    {
        GearAttributes.ToLong(value).Should().Be(1000);
        GearAttributes.ToLong(new DateTime(2026, 1, 1)).Should().BeNull();
        GearAttributes.ToLong("not a number").Should().BeNull();
    }

    /// <summary>Regression: recombobulated footers (menu-samples) strip to "aLEGENDARY CLOAK a" and were not recognized.</summary>
    [TestCase("§6§l§ka§6§lLEGENDARY CLOAK §6§l§ka", "LEGENDARY", "CLOAK")]
    [TestCase("§d§l§ka§d§lMYTHIC DUNGEON BOOTS §d§l§ka", "MYTHIC", "BOOTS")]
    [TestCase("§c§l§ka§c§lVERY SPECIAL DUNGEON HELMET §c§l§ka", "VERY_SPECIAL", "HELMET")]
    [TestCase("§kk§r §d§lMYTHIC DUNGEON BOOTS §kk", "MYTHIC", "BOOTS")]
    [TestCase("§6§lLEGENDARY FARMING TOOL", "LEGENDARY", "FARMING TOOL")]
    [TestCase("EPIC SWORD", "EPIC", "SWORD")]
    [TestCase("COMMON PET", "COMMON", "PET")]
    public void Footer_RealSamples(string footer, string rarity, string type)
    {
        ItemFooter.Read(new Item { Tag = "X", Description = "§7Health: §c+5\n\n" + footer }).Should().Be((rarity, type));
    }

    [Test]
    public void RarityFallsBackToTheLoreFooter_AndRecombGlyphsAreIgnored()
    {
        var item = new Item { Tag = "HYPERION", Description = "x\n§d§l§ka§r §d§lMYTHIC DUNGEON SWORD §d§l§ka" };

        var piece = GearNormalizer.Normalize(item, GearModifierTable.Dungeon, "F7")!;

        piece.Rarity.Should().Be("MYTHIC");
        piece.ItemType.Should().Be("SWORD");
    }

    [Test]
    public void EmptySlot_IsNull()
    {
        GearNormalizer.Normalize(GearTestItems.Empty()).Should().BeNull();
        GearNormalizer.Normalize(null).Should().BeNull();
    }

    [Test]
    public void Pet_FromPetInfoCompound()
    {
        var item = new Item
        {
            Tag = "PET_GOLDEN_DRAGON", ItemName = "§7[Lvl 148] §6Golden Dragon",
            ExtraAttributes = new()
            {
                ["petInfo"] = new Dictionary<string, object> { ["type"] = "GOLDEN_DRAGON", ["tier"] = "LEGENDARY", ["heldItem"] = "DWARF_TURTLE_SHELMET", ["exp"] = 113786696.0 }
            }
        };

        var pet = GearNormalizer.NormalizePet(item)!;

        pet.Type.Should().Be("GOLDEN_DRAGON");
        pet.Tier.Should().Be("LEGENDARY");
        pet.Level.Should().Be(148);
        pet.HeldItem.Should().Be("DWARF_TURTLE_SHELMET");
    }
}
