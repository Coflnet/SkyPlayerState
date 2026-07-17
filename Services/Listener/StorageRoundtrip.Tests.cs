using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Newtonsoft.Json;
using AwesomeAssertions;

namespace Coflnet.Sky.PlayerState.Services;

public class StorageRoundtripTests
{
    // A trimmed but representative slice of the "Large Backpack (Slot #9)" view from the
    // SpectChicken report: a nav button, a simple item (primitive ExtraAttributes), an empty
    // slot, and a potion whose ExtraAttributes contain a nested array of nested objects.
    private const string ChestJson = """
    {
      "items": [
        {"id": null, "itemName": "Close", "tag": null, "extraAttributes": {}, "enchantments": null, "color": null, "description": "", "count": 1},
        {"id": 1427898554042657, "itemName": "Chocolate Dye", "tag": "DYE_CHOCOLATE", "extraAttributes": {"uuid": "e41db0bf-f4bd-4964-82a5-0fc8d63f33a7", "uid": "0fc8d63f33a7", "timestamp": 1783724721459, "tier": 4}, "enchantments": null, "color": null, "description": "EPIC DYE", "count": 1},
        {"id": null, "itemName": null, "tag": null, "extraAttributes": null, "enchantments": {}, "color": null, "description": null, "count": 0},
        {"id": null, "itemName": "Dungeon VII Potion", "tag": "POTION_regeneration", "extraAttributes": {"potion_name": "Dungeon", "splash": 0, "potion_level": 7, "dungeon_potion": 1, "effects": [{"effect": "regeneration", "duration_ticks": 48000, "level": 4}, {"effect": "strength", "duration_ticks": 48000, "level": 5}], "potion_type": "POTION", "potion": "regeneration", "tier": 4}, "enchantments": null, "color": null, "description": "EPIC", "count": 1}
      ],
      "name": "Large Backpack (Slot #9)",
      "position": null,
      "openedAt": "2026-07-16T19:29:46.742"
    }
    """;

    [Test]
    public void StorageRoundtrip_PreservesNestedExtraAttributes()
    {
        // Kafka delivers the update as JSON, so nested ExtraAttributes are JObject/JArray/JValue.
        var chest = JsonConvert.DeserializeObject<ChestView>(ChestJson)!;
        var potion = chest.Items[3];
        potion.Tag.Should().Be("POTION_regeneration");
        potion.ExtraAttributes!["effects"].Should().NotBeNull();

        // What StorageService does when saving/reading the item (MessagePack Standard + Lz4).
        var stored = new StorageService.StorageItem { Items = chest.Items };
        var roundTripped = stored.Items;

        var potionOut = roundTripped[3];
        potionOut.Tag.Should().Be("POTION_regeneration", "tag must survive storage");
        potionOut.ExtraAttributes.Should().NotBeNull("ExtraAttributes must survive storage");
        potionOut.ExtraAttributes!.Should().ContainKey("effects");
        potionOut.ExtraAttributes!.Should().ContainKey("potion_level");

        // the simple item's attributes and the nav button must round-trip too
        roundTripped[1].Tag.Should().Be("DYE_CHOCOLATE");
        roundTripped[1].ExtraAttributes!["uid"].Should().Be("0fc8d63f33a7");
        roundTripped[0].ItemName.Should().Be("Close");
    }
}
