using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;
public class ItemServiceTests
{
    [Test]
    public void FindBadItems()
    {
        var attribJson = """
        {"attributes":{
            "fisherman": 5,
            "trophy_hunter": 5
        },
        "modifier": "pitchin",
        "tier": 3,
        "timestamp": "9/17/23 6:46 PM",
        "uid": "661c8c47dd65",
        "uuid": "40e86d04-e6f4-4bb7-a7d0-661c8c47dd65"}
        """;
        var badItems = ItemsService.FindBadItems(new List<Models.CassandraItem>() {
            new(){Tag="BOOSTER_COOKIE",ExtraAttributesJson=attribJson},
            new(){Tag="BOOSTER_COOKIE",ExtraAttributesJson=attribJson},
            new(){Tag="BOOSTER_COOKIE",ExtraAttributesJson=attribJson},
            new(){Tag="BOOSTER_COOKIE",ExtraAttributesJson=attribJson},
            new(){Tag="BOOSTER_COOKIE",ExtraAttributesJson=attribJson},
        });
        Assert.AreEqual(2, badItems.matchingIds.Count);
    }


    [Test]
    public void FindDupplicateItemsLarge()
    {
        var data = System.IO.File.ReadAllText("Mock/dupplicateItems.json");
        var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.Item>>(data);
        var badItems = ItemsService.FindBadItems(items.Select(i=>new CassandraItem(i)).ToList());
        // bigger = better
        Assert.AreEqual(202, badItems.matchingIds.Count);
    }
    [Test]
    public void FindDupplicateItemsGlacialScythe()
    {
        var data = System.IO.File.ReadAllText("Mock/glacialScythe.json");
        var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.Item>>(data);
        var badItems = ItemsService.FindBadItems(items.Select(i=>new CassandraItem(i)).ToList());
        // bigger = better
        Assert.AreEqual(251, badItems.matchingIds.Count);
    }
}