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
}