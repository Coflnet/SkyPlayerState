using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Moq;
using Newtonsoft.Json;
using Coflnet.Sky.PlayerState.Tests;
using FluentAssertions;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Services;

public class ItemIdAssignUpdateTest
{
    private StateObject currentState = new();
    private Mock<IItemsService> itemsService;
    private List<Item>? calledWith;
    private Item sampleItem = new()
    {
        ItemName = "Lapis Helmet",
        Enchantments = new Dictionary<string, byte>() { { "protection", 1 } },
        ExtraAttributes = new Dictionary<string, object>() { { "uuid", "96606179-dc64-4184-a356-6758856f593b" }, { "tier", 5 } }
    };
    [Test]
    public async Task HigherEnchantIsNew()
    {
        var listener = new ItemIdAssignUpdate();
        var changedSample = new Item(sampleItem);
        changedSample.Enchantments!["protection"] = 2;

        await listener.Process(CreateArgs(changedSample));
        Assert.That(calledWith, Is.Not.Null);
        Assert.That(1, Is.EqualTo(calledWith.Count), JsonConvert.SerializeObject(calledWith));
        itemsService.Verify(s => s.FindOrCreate(It.Is<IEnumerable<Item>>(i => i.Count() == 1)), Times.Once);
    }

    [Test]
    public async Task SameNoLookup()
    {
        var listener = new ItemIdAssignUpdate();
        var matchingSample = new Item(sampleItem);

        await listener.Process(CreateArgs(matchingSample));
        Assert.That(calledWith, Is.Null);
        Assert.That(1, Is.EqualTo(matchingSample.Id));
        itemsService.Verify(s => s.FindOrCreate(It.IsAny<IEnumerable<Item>>()), Times.Never);
    }

    [Test]
    public async Task BoosterCookie()
    {
        var json =
        """
        {"Id":null,"ItemName":"§6Booster Cookie","Tag":"BOOSTER_COOKIE","ExtraAttributes":{"uid":"4bee3a354fb4","uuid":"19746077-7ecb-46a8-a220-4bee3a354fb4","timestamp":"9/24/20 3:19 PM","tier":5},"Enchantments":null,"Color":null,
        "Description":"§7Consume to gain the §dCookie\n§dBuff §7for §b4 §7days:\n\n§8‣ §7Ability to gain §bBits§7!\n§8‣ §3+25☯ §7Insta-sell your Material stash to the §6Bazaar\n\n§6§lLEGENDARY","Count":1}
        """;
        var existing = JsonConvert.DeserializeObject<Item>(json);
        existing.Id = 1;
        currentState.RecentViews.Enqueue(new()
        {
            Items = new List<Item>(){
                existing
            }
        });
        var listener = new ItemIdAssignUpdate();
        var matchingSample = JsonConvert.DeserializeObject<Item>(json);
        await listener.Process(CreateArgs(matchingSample));
        Assert.That(calledWith, Is.Null);
        Assert.That(1, Is.EqualTo(matchingSample.Id));
    }

    [Test]
    public async Task PetCassandraToKafkaConversion()
    {
        // This simulates real data flow: item stored in Cassandra with id, then consumed from Kafka without id
        var kafkaJsonString = """
        {"Id":null,"ItemName":"§7[Lvl 100] §6Hound","Tag":"PET_HOUND","ExtraAttributes":{"petInfo":{"type":"HOUND","active":false,"exp":44349889.503324755,"tier":"LEGENDARY","hideInfo":false,"heldItem":"DWARF_TURTLE_SHELMET","candyUsed":0,"uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","uniqueId":"63f70e93-1b40-4b38-a2d2-a4bc316cd4bd","hideRightClick":false,"noMove":false,"extraData":{},"petSoulbound":false},"uid":"59d50a6a72e9","uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","timestamp":1767870006897,"tier":5},"Enchantments":null,"Color":null,"Description":"§8Combat Pet\n\n§7True Defense: §f+10\n§7Strength: §c+40\n§7Bonus Attack Speed: §e+25%\n§7Ferocity: §c+5\n§7Speed: §f+10\n\n§6Scavenger\n§7§7Gain §a+100 §7coins per monster kill\n\n§6Finder\n§7§7Increases the chance for monsters\n§7to drop their armor by §a30%§7.\n\n§6Pack Slayer\n§7§7Gain §b1.5x §7Combat XP §7against §aWolves§7.\n\n§6Held Item: §9Dwarf Turtle Shelmet\n§7Grants §f+10❂ True Defense §7and\n§7makes the pet's owner immune to\n§7knockback.\n\n§b§lMAX LEVEL\n§8▸ 44,349,889 XP\n\n§7§eRight-click to add this pet to your\n§epet menu!\n\n§6§lLEGENDARY","Count":1}
        """;

        var kafkaItem = JsonConvert.DeserializeObject<Item>(kafkaJsonString)!;
        var cassandraItem = new CassandraItem(kafkaItem);
        cassandraItem.Id = Random.Shared.Next(1000000, 9999999);
        
        var retrievedFromDb = cassandraItem.ToTransfer();
        
        // Normalize both items: convert JTokens to native types
        var normalized = new Item(retrievedFromDb);
        foreach (var kvp in retrievedFromDb.ExtraAttributes!.Keys.ToList())
        {
            if (retrievedFromDb.ExtraAttributes[kvp] is JToken token)
                normalized.ExtraAttributes![kvp] = CassandraItem.ConvertJTokenToNative(token);
        }
        
        // Also normalize the new Kafka item to convert JObjects to Dictionaries
        var newKafkaItem = JsonConvert.DeserializeObject<Item>(kafkaJsonString)!;
        if (newKafkaItem.ExtraAttributes != null)
        {
            foreach (var kvp in newKafkaItem.ExtraAttributes.Keys.ToList())
            {
                if (newKafkaItem.ExtraAttributes[kvp] is JToken token)
                    newKafkaItem.ExtraAttributes[kvp] = CassandraItem.ConvertJTokenToNative(token);
            }
        }
        
        var comparer = new ItemCompare();
        comparer.Equals(newKafkaItem, normalized).Should().BeTrue("Pet should match after Cassandra->Kafka conversion");

        var listener = new ItemIdAssignUpdate();
        var result = listener.Join([newKafkaItem], [normalized]).First();
        result.Id.Should().Be(cassandraItem.Id, "Pet should receive the stored ID from Cassandra");
    }

    [Test]
    public async Task StoredDarkClaymore()
    {
        var json = """
        {
        "id": 0,
        "itemName": "§f§f§dWithered Dark Claymore §6✪✪✪✪✪§c➋",
        "tag": "DARK_CLAYMORE",
        "extraAttributes": {
        "runes": {
            "BLOOD_2": 3
        },
        "gems": {
            "COMBAT_0": "FLAWED",
            "unlocked_slots": [
            "COMBAT_0",
            "COMBAT_1"
            ],
            "COMBAT_1_gem": "JASPER",
            "COMBAT_1": "FLAWED",
            "COMBAT_0_gem": "JASPER"
        },
        "necromancer_souls": [
        {
          "mob_id": "MASTER_CRYPT_TANK_ZOMBIE_60",
          "dropped_instance_id": "master_catacombs_floor_one",
          "dropped_mode_id": "dungeon"
        }],
        "champion_combat_xp": 4243008.1234105695,
        "modifier": "withered",
        "upgrade_level": 7,
        "uid": "b2643b46dc17",
        "uuid": "800a6181-7c2e-48e9-8ccb-b2643b46dc17",
        "timestamp": 1738720261216,
        "tier": 8
        },
        "enchantments": {
        "champion": 10,
        "ultimate_swarm": 3,
        "vampirism": 6,
        "venomous": 5
        },
        "color": null,
        "description": null,
        "count": 1
        }
        """;

        var existing = new CassandraItem(JsonConvert.DeserializeObject<Item>(json)!).ToTransfer();
        var copy = new Item(existing);
        foreach (var kvp in existing.ExtraAttributes!.Keys)
        {
            if (existing.ExtraAttributes[kvp] is JToken token)
                copy.ExtraAttributes![kvp] = CassandraItem.ConvertJTokenToNative(token);
        }
        var newItem = MessagePackSerializer.Deserialize<Item>(MessagePackSerializer.Serialize(copy));
        existing.Id = Random.Shared.Next(1, 1000000);
        var comparer = new ItemCompare();
        comparer.GetHashCode(newItem).Should().Be(comparer.GetHashCode(existing));
        comparer.Equals(newItem, existing).Should().BeTrue();

        var listener = new ItemIdAssignUpdate();
        var sum = listener.Join([newItem], [existing]).First();
        Assert.That(sum.Id, Is.EqualTo(existing.Id));
    }


    private bool AreDictEqual(Dictionary<string, object>? d1, Dictionary<string, object>? d2)
    {
        if (d1 == null && d2 == null) return true;
        if (d1 == null || d2 == null) return false;
        if (d1.Count != d2.Count) return false;
        
        foreach (var key in d1.Keys)
        {
            if (!d2.ContainsKey(key)) return false;
            
            var v1 = d1[key];
            var v2 = d2[key];
            
            if (v1 is Dictionary<string, object> nestedD1 && v2 is Dictionary<string, object> nestedD2)
            {
                if (!AreDictEqual(nestedD1, nestedD2)) return false;
            }
            else if (!object.Equals(v1, v2))
            {
                if (v1 != null && v2 != null)
                {
                    var v1Str = $"{v1} ({v1.GetType().Name})";
                    var v2Str = $"{v2} ({v2.GetType().Name})";
                    Console.WriteLine($"Dict value mismatch at key '{key}': {v1Str} vs {v2Str}");
                }
                return false;
            }
        }
        return true;
    }

    private MockedUpdateArgs CreateArgs(params Item[] items)
    {
        var sampleWithId = new Item(sampleItem);
        sampleWithId.Id = 1;
        currentState.RecentViews.Enqueue(new()
        {
            Items = new List<Item>(){
                sampleWithId
            }
        });
        var args = new MockedUpdateArgs()
        {
            currentState = currentState,
            msg = new UpdateMessage()
            {
                Chest = new()
                {
                    Items = items.ToList()
                }
            }
        };
        // args.AddService<ITransactionService>(transactionService.Object);
        itemsService = new Mock<IItemsService>();
        itemsService.Setup(s => s.FindOrCreate(It.IsAny<IEnumerable<Item>>())).Callback<IEnumerable<Item>>((v) =>
        {
            calledWith = v.ToList();
        }).ReturnsAsync(items.ToList());
        args.AddService<IItemsService>(itemsService.Object);
        calledWith = null;

        return args;
    }
}