using Coflnet.Sky.PlayerState.Models;
using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class CassandraCompareTests
{
    [Test]
    public void UpgradeEnchant()
    {
        var compare = new CassandraItemCompare();
        var item = new Item()
        {
            Tag = "ASPECT_OF_THE_END",
            Enchantments = new Dictionary<string, byte>() { { "sharpness", 1 } },
            ExtraAttributes = new Dictionary<string, object>() { { "uuid", "96606179-dc64-4184-a356-6758856f593b" } }
        };
        var cassandraItem = new CassandraItem(item);
        var cassandraItem2 = new CassandraItem(item);
        cassandraItem.Enchantments!["sharpness"] = 2;
        Assert.That(!compare.Equals(cassandraItem, cassandraItem2));
    }

    [Test]
    public void IgnoreCompactBlocks()
    {
        var compare = new CassandraItemCompare() as IEqualityComparer<CassandraItem>;
        var item = new Item()
        {
            Tag = "ASPECT_OF_THE_END",
            Enchantments = new Dictionary<string, byte>() { { "sharpness", 1 } },
            ExtraAttributes = new Dictionary<string, object>() { { "uuid", "96606179-dc64-4184-a356-6758856f593b" }, { "compact_blocks", 1 } }
        };
        var cassandraItem = new CassandraItem(item);
        item.ExtraAttributes["compact_blocks"] = 20000;
        var cassandraItem2 = new CassandraItem(item);
        Assert.That(compare.Equals(cassandraItem, cassandraItem2));
        // hashcode
        Assert.That(compare.GetHashCode(cassandraItem), Is.EqualTo(compare.GetHashCode(cassandraItem2)));
    }

    [Test]
    public void RemovesHighFloats()
    {
        var compare = new CassandraItemCompare() as IEqualityComparer<CassandraItem>;
        var item = new Item()
        {
            Tag = "ASPECT_OF_THE_END",
            ExtraAttributes = new Dictionary<string, object>() { { "uuid", "96606179-dc64-4184-a356-6758856f593b" }, { "champion_combat_xp", 53415000075.308676436 } }
        };
        var cassandraItem = new CassandraItem(item);
        item.ExtraAttributes["champion_combat_xp"] = 20000.0f;
        var cassandraItem2 = new CassandraItem(item);
        Assert.That(compare.Equals(cassandraItem, cassandraItem2));
        // hashcode
        Assert.That(compare.GetHashCode(cassandraItem), Is.EqualTo(compare.GetHashCode(cassandraItem2)));
    }

    [Test]
    public void Match()
    {
        var compare = new CassandraItemCompare();
        var item = new Item()
        {
            Tag = "ASPECT_OF_THE_END",
            Enchantments = new Dictionary<string, byte>() { { "sharpness", 1 }, { "growth", 4 }, { "protection", 4 } },
            ExtraAttributes = new Dictionary<string, object>() { { "uuid", "96606179-dc64-4184-a356-6758856f593b" } }
        };
        var cassandraItem = new CassandraItem(item);
        var cassandraItem2 = JsonConvert.DeserializeObject<CassandraItem>(JsonConvert.SerializeObject(new CassandraItem(item)));
        Assert.That(compare.Equals(cassandraItem, cassandraItem2));
    }

    [Test]
    public void PetWithDifferentPetInfo()
    {
        var compare = new CassandraItemCompare() as IEqualityComparer<CassandraItem>;
        var petInfo = new
        {
            type = "TIGER",
            active = false,
            exp = 16970753.571252737,
            tier = "LEGENDARY",
            hideInfo = false,
            heldItem = "CROCHET_TIGER_PLUSHIE",
            candyUsed = 0,
            uuid = "2329d640-e2a5-403e-b340-aa744ae561a9",
            uniqueId = "18f993b4-775b-418b-83b3-29a3909aeb9b",
            hideRightClick = false,
            noMove = false,
            extraData = new { },
            petSoulbound = false
        };
        var item = new Item()
        {
            Tag = "PET_TIGER",
            ExtraAttributes = new Dictionary<string, object>() { 
                { "uuid", "2329d640-e2a5-403e-b340-aa744ae561a9" },
                { "uid", "aa744ae561a9" },
                { "petInfo", petInfo },
                { "timestamp", 1767815196139 },
                { "tier", 5 }
            }
        };
        var cassandraItem1 = new CassandraItem(item);

        // Create a second pet with different volatile petInfo fields
        var petInfo2 = new
        {
            type = "TIGER",
            active = true,  // Different
            exp = 17000000.0,  // Different
            tier = "LEGENDARY",
            hideInfo = true,  // Different
            heldItem = "CROCHET_TIGER_PLUSHIE",
            candyUsed = 0,
            uuid = "2329d640-e2a5-403e-b340-aa744ae561a9",
            uniqueId = "different-id",  // Different
            hideRightClick = true,  // Different
            noMove = true,  // Different
            extraData = new { },
            petSoulbound = false
        };
        var item2 = new Item()
        {
            Tag = "PET_TIGER",
            ExtraAttributes = new Dictionary<string, object>() { 
                { "uuid", "2329d640-e2a5-403e-b340-aa744ae561a9" },
                { "uid", "aa744ae561a9" },
                { "petInfo", petInfo2 },
                { "timestamp", 1767815196139 },
                { "tier", 5 }
            }
        };
        var cassandraItem2 = new CassandraItem(item2);
        
        // Should be equal despite different petInfo volatile fields
        Assert.That(compare.Equals(cassandraItem1, cassandraItem2), "Pets with same UUID/Tag but different petInfo volatile fields should be equal");
        // Hashcodes should also match
        Assert.That(compare.GetHashCode(cassandraItem1), Is.EqualTo(compare.GetHashCode(cassandraItem2)));
    }
}