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
        // This simulates the REAL data flow:
        // 1. Pet comes from Kafka with ID (first time)
        // 2. Pet is stored in Cassandra with ID
        // 3. Later, pet comes from Kafka again (different exp, different timestamp) WITHOUT ID
        // 4. System should match the new pet with stored pet and assign the ID
        
        // Original pet JSON (simulating first arrival via Kafka)
        var originalPetJson = """
        {"Id":null,"ItemName":"§7[Lvl 100] §6Hound","Tag":"PET_HOUND","ExtraAttributes":{"petInfo":{"type":"HOUND","active":false,"exp":44349889.503324755,"tier":"LEGENDARY","hideInfo":false,"heldItem":"DWARF_TURTLE_SHELMET","candyUsed":0,"uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","uniqueId":"63f70e93-1b40-4b38-a2d2-a4bc316cd4bd","hideRightClick":false,"noMove":false,"extraData":{},"petSoulbound":false},"uid":"59d50a6a72e9","uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","timestamp":1767870006897,"tier":5},"Enchantments":null,"Color":null,"Description":"§8Combat Pet...","Count":1}
        """;

        // Later pet JSON (simulating later arrival via Kafka with different exp/timestamp but same pet)
        var laterPetJson = """
        {"Id":null,"ItemName":"§7[Lvl 100] §6Hound","Tag":"PET_HOUND","ExtraAttributes":{"petInfo":{"type":"HOUND","active":true,"exp":44350000.0,"tier":"LEGENDARY","hideInfo":true,"heldItem":"DWARF_TURTLE_SHELMET","candyUsed":0,"uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","uniqueId":"different-unique-id","hideRightClick":true,"noMove":true,"extraData":{},"petSoulbound":false},"uid":"59d50a6a72e9","uuid":"7652a510-0c0d-4d88-af53-59d50a6a72e9","timestamp":9999999999999,"tier":5},"Enchantments":null,"Color":null,"Description":"§8Combat Pet...","Count":1}
        """;

        // Step 1: Simulate original pet arriving - JSON deserialization creates JObjects
        var originalPet = JsonConvert.DeserializeObject<Item>(originalPetJson)!;
        
        // IMPORTANT: In real Kafka flow, items are normalized BEFORE MessagePack serialization
        // because MessagePack can't serialize JObject/JProperty
        NormalizeItemForTest(originalPet);
        
        // Simulate MessagePack serialization/deserialization (which happens in Kafka consumer)
        var originalPetBytes = MessagePackSerializer.Serialize(originalPet);
        var originalPetAfterKafka = MessagePackSerializer.Deserialize<Item>(originalPetBytes);
        
        // Store in Cassandra
        var cassandraItem = new CassandraItem(originalPetAfterKafka);
        var storedId = Random.Shared.Next(1000000, 9999999);
        cassandraItem.Id = storedId;
        
        // Step 2: Simulate retrieving the stored pet from Cassandra
        var retrievedFromDb = cassandraItem.ToTransfer();
        Assert.That(retrievedFromDb.Id, Is.EqualTo(storedId), "Retrieved item should have the stored ID");
        
        // Step 3: Simulate a NEW pet arriving from Kafka (different exp, different volatile fields)
        var newPet = JsonConvert.DeserializeObject<Item>(laterPetJson)!;
        
        // Normalize before MessagePack
        NormalizeItemForTest(newPet);
        
        // Simulate MessagePack serialization/deserialization
        var newPetBytes = MessagePackSerializer.Serialize(newPet);
        var newPetAfterKafka = MessagePackSerializer.Deserialize<Item>(newPetBytes);
        
        // Step 4: Test comparison
        var comparer = new ItemCompare();
        
        // Debug: Print normalized attribute types for both items
        Console.WriteLine("=== New Pet ExtraAttributes ===");
        foreach (var kvp in newPetAfterKafka.ExtraAttributes ?? new Dictionary<string, object>())
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value?.GetType().FullName}");
            if (kvp.Value is Dictionary<string, object> dict)
            {
                foreach (var inner in dict)
                    Console.WriteLine($"    {inner.Key}: {inner.Value?.GetType().Name} = {inner.Value}");
            }
        }
        
        Console.WriteLine("=== Retrieved Pet ExtraAttributes ===");
        foreach (var kvp in retrievedFromDb.ExtraAttributes ?? new Dictionary<string, object>())
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value?.GetType().FullName}");
            if (kvp.Value is Dictionary<string, object> dict)
            {
                foreach (var inner in dict)
                    Console.WriteLine($"    {inner.Key}: {inner.Value?.GetType().Name} = {inner.Value}");
            }
        }
        
        // Check hash codes match (required for dictionary lookups)
        var hash1 = comparer.GetHashCode(newPetAfterKafka);
        var hash2 = comparer.GetHashCode(retrievedFromDb);
        Assert.That(hash1, Is.EqualTo(hash2), $"Hash codes should match for same pet. NewPet hash: {hash1}, StoredPet hash: {hash2}");
        
        // Check tags match
        Assert.That(newPetAfterKafka.Tag, Is.EqualTo(retrievedFromDb.Tag), "Tags should match");
        
        // Test the comparison
        var areEqual = comparer.Equals(newPetAfterKafka, retrievedFromDb);
        Assert.That(areEqual, Is.True, "Pets should be equal after normalization");

        // Step 5: Test the full Join method
        var listener = new ItemIdAssignUpdate();
        var result = listener.Join([newPetAfterKafka], [retrievedFromDb]).First();
        Assert.That(result.Id, Is.EqualTo(storedId), $"Pet should receive the stored ID {storedId}");
    }

    private static void NormalizeItemForTest(Item item)
    {
        if (item.ExtraAttributes == null) return;
        
        foreach (var key in item.ExtraAttributes.Keys.ToList())
        {
            if (item.ExtraAttributes[key] is JToken token)
            {
                item.ExtraAttributes[key] = CassandraItem.ConvertJTokenToNative(token);
            }
        }
    }

    [Test]
    public async Task EndermanPetFromUserReport()
    {
        // This is the exact pet JSON from the user report
        var endermanPetJson = """
        {"Id":null,"ItemName":"§7[Lvl 58] §6Enderman","Tag":"PET_ENDERMAN","ExtraAttributes":{"petInfo":{"type":"ENDERMAN","active":false,"exp":1145890.0442327096,"tier":"LEGENDARY","hideInfo":false,"heldItem":"PET_ITEM_COMBAT_SKILL_BOOST_RARE","candyUsed":0,"uuid":"f965c6c0-4fae-455a-9f1c-56906d77a27e","uniqueId":"39a86cfa-c4ad-4ab2-b45b-3063feff3833","hideRightClick":false,"noMove":false,"extraData":{},"petSoulbound":false},"uid":"56906d77a27e","uuid":"f965c6c0-4fae-455a-9f1c-56906d77a27e","timestamp":1767878617047,"tier":5},"Enchantments":null,"Color":null,"Description":"§8Combat Pet...","Count":1}
        """;
        
        // Simulate full flow
        var pet = JsonConvert.DeserializeObject<Item>(endermanPetJson)!;
        NormalizeItemForTest(pet);  // Normalize before MessagePack
        var petBytes = MessagePackSerializer.Serialize(pet);
        var petAfterKafka = MessagePackSerializer.Deserialize<Item>(petBytes);
        
        // Store in Cassandra
        var cassandraItem = new CassandraItem(petAfterKafka);
        var storedId = 12345678L;
        cassandraItem.Id = storedId;
        
        // Retrieve from Cassandra
        var retrievedFromDb = cassandraItem.ToTransfer();
        
        // Simulate NEW arrival of same pet with different volatile fields
        var laterPetJson = """
        {"Id":null,"ItemName":"§7[Lvl 58] §6Enderman","Tag":"PET_ENDERMAN","ExtraAttributes":{"petInfo":{"type":"ENDERMAN","active":true,"exp":1145999.0,"tier":"LEGENDARY","hideInfo":true,"heldItem":"PET_ITEM_COMBAT_SKILL_BOOST_RARE","candyUsed":0,"uuid":"f965c6c0-4fae-455a-9f1c-56906d77a27e","uniqueId":"different-id","hideRightClick":true,"noMove":true,"extraData":{},"petSoulbound":false},"uid":"56906d77a27e","uuid":"f965c6c0-4fae-455a-9f1c-56906d77a27e","timestamp":9999999999,"tier":5},"Enchantments":null,"Color":null,"Description":"§8Combat Pet...","Count":1}
        """;
        
        var newPet = JsonConvert.DeserializeObject<Item>(laterPetJson)!;
        NormalizeItemForTest(newPet);  // Normalize before MessagePack
        var newPetBytes = MessagePackSerializer.Serialize(newPet);
        var newPetAfterKafka = MessagePackSerializer.Deserialize<Item>(newPetBytes);
        
        var comparer = new ItemCompare();
        
        // Test hash equality (required for dictionary lookups)
        Assert.That(comparer.GetHashCode(newPetAfterKafka), Is.EqualTo(comparer.GetHashCode(retrievedFromDb)), 
            "Hash codes must match for same UUID");
        
        // Test Equals
        Assert.That(comparer.Equals(newPetAfterKafka, retrievedFromDb), Is.True, 
            "Same pet with different volatile fields should be equal");
        
        // Test Join
        var listener = new ItemIdAssignUpdate();
        var result = listener.Join([newPetAfterKafka], [retrievedFromDb]).First();
        Assert.That(result.Id, Is.EqualTo(storedId), "Pet should get the stored ID");
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