using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Item fixtures in the shapes the pipeline delivers (ExtraAttributes after SkyApi's GetReducedExtra: enchantments moved
/// to <see cref="Item.Enchantments"/>, "tier" added). Drill and scythe attributes are copied from ItemCompare.Tests and
/// Mock/glacialScythe.json, the NBT keys of the others from dev/Data/NBT.cs and the research dossier.
/// </summary>
internal static class GearTestItems
{
    public const string HoeUuid = "5e1a0f3c-1111-4a7c-bcc3-0516172d4e55";
    public const string DrillUuid = "b528528d-ce3d-4daa-9ad8-9b43ab629e84";

    public static Item Hoe(long cultivated = 1_234_567) => new()
    {
        Tag = "THEORETICAL_HOE_WHEAT_3",
        ItemName = "§6Blessed Euclid's Wheat Hoe Tier III",
        Description = "§7Farming Fortune: §6+60\n\n§6§lLEGENDARY FARMING TOOL",
        ExtraAttributes = new()
        {
            ["modifier"] = "blessed", ["farmed_cultivating"] = cultivated, ["mined_crops"] = 905_311L,
            ["hot_potato_count"] = 15, ["rarity_upgrades"] = 1, ["uuid"] = HoeUuid, ["uid"] = "0516172d4e55",
            ["timestamp"] = 1766804897464L, ["tier"] = 5
        },
        Enchantments = new() { ["harvesting"] = 6, ["cultivating"] = 8, ["dedication"] = 4, ["turbo_wheat"] = 5, ["turbo_carrot"] = 3, ["replenish"] = 1, ["experience"] = 3 }
    };

    public static Item Drill(long compact = 0) => new()
    {
        Tag = "MITHRIL_DRILL_2",
        ItemName = "§9Fleet Mithril Drill SX-R326",
        Description = "§8Breaking Power 6\n\n§7Damage: §c+65\n§7Mining Speed: §a+600\n§7Mining Fortune: §a+20\n\n§9§lRARE DRILL", // NEU lore
        ExtraAttributes = new()
        {
            ["rarity_upgrades"] = 1, ["drill_part_upgrade_module"] = "goblin_omelette", ["drill_part_engine"] = "mithril_drill_engine",
            ["drill_fuel"] = 6464, ["modifier"] = "fleet", ["uid"] = "9b43ab629e84", ["drill_part_fuel_tank"] = "mithril_fuel_tank",
            ["uuid"] = DrillUuid, ["timestamp"] = 1766804897464L, ["tier"] = 4, ["compact_blocks"] = compact,
            ["gems"] = new Dictionary<string, object>
            {
                ["JADE_0"] = "FINE",
                ["AMBER_0"] = new Dictionary<string, object> { ["quality"] = "PERFECT", ["uuid"] = "11111111-2222-3333-4444-555555555555" },
                ["MINING_0"] = "FLAWLESS", ["MINING_0_gem"] = "TOPAZ",
                ["unlocked_slots"] = new List<object> { "JADE_0", "AMBER_0", "MINING_0" }
            }
        },
        Enchantments = new() { ["efficiency"] = 5, ["fortune"] = 3, ["ultimate_flowstate"] = 3, ["experience"] = 3, ["pristine"] = 2 }
    };

    public static Item Rod() => new()
    {
        Tag = "ROD_OF_THE_SEA",
        ItemName = "§6Pitchin' Rod of the Sea",
        Description = "§7Damage: §c+150\n§7Strength: §c+150\n§7Fishing Speed: §a+80\n§7Sea Creature Chance: §c+8%\n\n§6§lLEGENDARY FISHING ROD", // NEU lore
        ExtraAttributes = new()
        {
            ["modifier"] = "pitchin", ["expertise_kills"] = 3_000L, ["tier"] = 5, ["uuid"] = "6c7e7f6a-0000-4b7c-9cc3-123456789abc",
            ["line"] = new Dictionary<string, object> { ["part"] = "speedy_line", ["donated_museum"] = 0 },
            ["hook"] = new Dictionary<string, object> { ["part"] = "common_hook" },
            ["sinker.part"] = "junk_sinker"
        },
        Enchantments = new() { ["angler"] = 6, ["lure"] = 6, ["expertise"] = 7, ["blessing"] = 6, ["magnet"] = 6 }
    };

    /// <summary>Mock/glacialScythe.json, first entry.</summary>
    public static Item Scythe() => new()
    {
        Tag = "GLACIAL_SCYTHE",
        ItemName = "§6Heroic Glacial Scythe §6✪§6✪§6✪§6✪§6✪",
        ExtraAttributes = new()
        {
            ["rarity_upgrades"] = 1, ["stats_book"] = 56474, ["runes"] = new Dictionary<string, object> { ["SPIRIT"] = 3 },
            ["modifier"] = "heroic", ["upgrade_level"] = 5, ["power_ability_scroll"] = "SAPPHIRE_POWER_SCROLL",
            ["uuid"] = "3063fae7-42f4-49ad-98db-2c9b33f50c0c", ["hot_potato_count"] = 15,
            ["gems"] = new Dictionary<string, object> { ["SAPPHIRE_0"] = "FINE" }, ["dungeon_item"] = 1,
            ["champion_combat_xp"] = 4386786.6403302485, ["uid"] = "2c9b33f50c0c", ["tier"] = 5
        },
        Enchantments = new() { ["champion"] = 10, ["cleave"] = 5, ["critical"] = 6, ["sharpness"] = 5, ["ultimate_wise"] = 5 }
    };

    public static Item Armor(string tag, string type, string? reforge = null) => new()
    {
        Tag = tag,
        ItemName = tag,
        Description = $"§7Farming Fortune: §a+30\n\n§6§lLEGENDARY {type}",
        ExtraAttributes = new() { ["modifier"] = reforge ?? "mossy", ["tier"] = 5, ["uuid"] = "armor-" + tag },
        Enchantments = new() { ["pesterminator"] = 5, ["protection"] = 5 }
    };

    public static Item Sword() => new()
    {
        Tag = "ASPECT_OF_THE_END",
        Description = "\n§9§lRARE SWORD",
        ExtraAttributes = new() { ["uuid"] = "9f0e0000-0000-0000-0000-000000000001", ["tier"] = 3 }
    };

    /// <summary>ekwav-state.json, hotbar slot 1 (lore trimmed to the stat lines and the footer).</summary>
    public static Item AspectOfTheDragons() => new()
    {
        Tag = "ASPECT_OF_THE_DRAGON", ItemName = "Legendary Aspect of the Dragons",
        Description = "§7Damage: §c+225\n§7Strength: §c+125 §9(+25)\n§7Crit Chance: §9+15% §9(+15%)\n§7Crit Damage: §9+78% §9(+28%)\n§7Attack Speed: §e+10% §9(+10%)\n§7Intelligence: §b+25 §9(+25)\n§7Gemstones: §8[§7§8]\n\n§8§l* §8Co-op Soulbound §8§l*\nLEGENDARY SWORD",
        ExtraAttributes = new() { ["modifier"] = "legendary", ["uuid"] = "b615aa58-8df0-4895-ba30-71ec248f569b", ["tier"] = 5 },
        Enchantments = new() { ["sharpness"] = 5, ["giant_killer"] = 5 }
    };

    /// <summary>ekwav-state.json, hotbar slot 2.</summary>
    public static Item AspectOfTheEnd() => new()
    {
        Tag = "ASPECT_OF_THE_END", ItemName = "Aspect of the End",
        Description = "§7Damage: §c+100\n§7Strength: §c+100\n§7Crit Damage: §9+50%\n§7Gemstones: §8[§7§8]\n\nThis item can be reforged!\nRARE SWORD",
        ExtraAttributes = new() { ["uuid"] = "47fd114b-284a-4120-bf20-3576b3f62ac1", ["tier"] = 3 },
        Enchantments = new() { ["critical"] = 5, ["sharpness"] = 5 }
    };

    /// <summary>A farming tool with the given lore stat lines.</summary>
    public static Item FarmingTool(string tag, string statLines) => new()
    {
        Tag = tag, Description = statLines + "\n\n§5§lEPIC FARMING TOOL", ExtraAttributes = new() { ["uuid"] = "tool-" + tag, ["tier"] = 4 }
    };

    public static Item Bait(string tag) => new() { Tag = tag, Count = 64, Description = "§7Grants Fishing Speed\n\n§f§lCOMMON BAIT" };

    public static Item Empty() => new() { Tag = null!, ItemName = "" };

    /// <summary>A 46 slot (1.21) bare inventory: armor in 5-8, the given hotbar items from slot 36, the offhand in 45.</summary>
    public static List<Item> BareInventory(Item?[]? armor = null, Item?[]? hotbar = null, Item? offhand = null, Item? firstMain = null)
    {
        var items = Enumerable.Range(0, 46).Select(_ => Empty()).ToList();
        for (var i = 0; i < (armor?.Length ?? 0); i++)
            items[5 + i] = armor![i] ?? Empty();
        for (var i = 0; i < (hotbar?.Length ?? 0); i++)
            items[36 + i] = hotbar![i] ?? Empty();
        items[44] = new Item { Tag = "SKYBLOCK_MENU", ItemName = "§aSkyBlock Menu" };
        if (offhand != null)
            items[45] = offhand;
        if (firstMain != null)
            items[9] = firstMain;
        return items;
    }

    /// <summary>A 54 slot chest menu plus the 36 player slots, hotbar items last.</summary>
    public static List<Item> ChestView(Dictionary<int, Item>? menu = null, Item?[]? hotbar = null)
    {
        var items = Enumerable.Range(0, 90).Select(_ => Empty()).ToList();
        foreach (var (slot, item) in menu ?? new())
            items[slot] = item;
        for (var i = 0; i < (hotbar?.Length ?? 0); i++)
            items[81 + i] = hotbar![i] ?? Empty();
        return items;
    }
}
