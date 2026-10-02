using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class RecipeUpdateTests
{
    private const string NotUnlockedLore = " §c✖ §c0§c/4 §9Enchanted Red Sand Cube\n §c✖ §c0§c/50 §aBurning Eye\n §c✖ §e1§c/40 §8(-39) §aMagma Chunk\n  §81x from §eInventory\n\nRecipe not unlocked!\n";

    /// <summary>
    /// The "Berserker Leggings" recipe view as uploaded in production on 2026-10-02.
    /// </summary>
    private static ChestView BerserkerLeggings(string buttonName, string requirementLine)
    {
        var items = Enumerable.Range(0, 90).Select(_ => new Item()).ToList();
        void Put(int slot, string tag, int count) => items[slot] = new Item { Tag = tag, ItemName = tag, Count = count };
        Put(10, "ENCHANTED_RED_SAND_CUBE", 1);
        Put(11, "BURNING_EYE", 50);
        Put(12, "ENCHANTED_RED_SAND_CUBE", 1);
        Put(19, "MAGMA_CHUNK", 20);
        Put(21, "MAGMA_CHUNK", 20);
        Put(28, "ENCHANTED_RED_SAND_CUBE", 1);
        Put(30, "ENCHANTED_RED_SAND_CUBE", 1);
        items[23] = new Item { ItemName = "Crafting Table", Count = 1, Description = "Craft this recipe by using a crafting\ntable or Supercraft." };
        Put(25, "BERSERKER_LEGGINGS", 1);
        items[32] = new Item { ItemName = buttonName, Count = 1, Description = NotUnlockedLore + requirementLine };
        return new ChestView { Name = "Berserker Leggings", Items = items };
    }

    /// <summary>
    /// No recipe was recorded from 2026-06-25 on: the mod for current Minecraft versions uploads the
    /// button as "Supercraft" and the requirement without the leading "§7".
    /// </summary>
    [Test]
    public void ReadsTheRecipeFromTheCurrentModFormat()
    {
        var recipe = RecipeUpdate.ReadRecipe(BerserkerLeggings("Supercraft", "§4❣ §cRequires §aRed Sand Collection VII§c."));

        recipe.Should().NotBeNull();
        recipe.Tag.Should().Be("BERSERKER_LEGGINGS");
        recipe.ResultCount.Should().Be(1);
        recipe.Ingredients.Should().Equal(
            new KeyValuePair<string, int>("ENCHANTED_RED_SAND_CUBE", 1), new KeyValuePair<string, int>("BURNING_EYE", 50), new KeyValuePair<string, int>("ENCHANTED_RED_SAND_CUBE", 1),
            new KeyValuePair<string, int>("MAGMA_CHUNK", 20), new KeyValuePair<string, int>(null, 0), new KeyValuePair<string, int>("MAGMA_CHUNK", 20),
            new KeyValuePair<string, int>("ENCHANTED_RED_SAND_CUBE", 1), new KeyValuePair<string, int>(null, 0), new KeyValuePair<string, int>("ENCHANTED_RED_SAND_CUBE", 1));
        // as stored by the older mod versions
        recipe.Requirements.Should().Equal("§7§4❣ §cRequires §aRed Sand Collection VII§c.");
    }

    [Test]
    public void BothModFormatsAreTheSameStoredRecipe()
    {
        var current = RecipeUpdate.ReadRecipe(BerserkerLeggings("Supercraft", "§4❣ §cRequires §aRed Sand Collection VII§c."));
        var older = RecipeUpdate.ReadRecipe(BerserkerLeggings("§aSupercraft", "§7§4❣ §cRequires §aRed Sand Collection VII§c."));

        older.Should().NotBeNull();
        current.ComparisonKey.Should().Be(older.ComparisonKey);
    }

    [Test]
    public void RecipeWithoutRequirementHasNone()
    {
        var view = BerserkerLeggings("Supercraft", "");
        view.Items[32].Description = " §a✔ §a477§a/160 §aCompost\n\n§aCrafting §21 §aitem into your inventory!\n\nClick to craft!";

        RecipeUpdate.ReadRecipe(view).Requirements.Should().BeEmpty();
    }

    [Test]
    public void OtherViewsAreNoRecipe()
    {
        var noButton = BerserkerLeggings("Close", "");
        var noDescription = BerserkerLeggings("Supercraft", "");
        noDescription.Items[32].Description = null;
        var noResult = BerserkerLeggings("Supercraft", "");
        noResult.Items[25] = new Item();

        RecipeUpdate.ReadRecipe(noButton).Should().BeNull();
        RecipeUpdate.ReadRecipe(noDescription).Should().BeNull();
        RecipeUpdate.ReadRecipe(noResult).Should().BeNull();
        RecipeUpdate.ReadRecipe(new ChestView { Name = "Large Chest", Items = Enumerable.Range(0, 90).Select(_ => new Item()).ToList() }).Should().BeNull();
        RecipeUpdate.ReadRecipe(null).Should().BeNull();
    }

    /// <summary>
    /// No npc cost was recorded from 2026-06-25 on: the mod for current Minecraft versions uploads
    /// "Cost" and single style cost lines without color code. Lore from the "Trades" view as
    /// uploaded in production on 2026-10-02.
    /// </summary>
    [TestCase("Placing this item on your private\n§7island will create a portal to §7the §bHub§7\nfor you to use whenever you want!\n\nThe portal will spawn facing you on\nthe block you click on.\n\nCOMMON PORTAL\n\nCost\n97 Coins\n\nClick to trade!", "Coins", 97)]
    [TestCase("COMMON\n\nCost\nOak Sapling\n\nClick to trade!\nRight-click for more trading options!", "Oak Sapling", 1)]
    [TestCase("Collection Item\n\nCOMMON\n\nCost\n§fSeeds §8x12\n\nClick to trade!\nRight-click for more trading options!", "Seeds", 12)]
    public void ReadsTheCostFromTheCurrentModFormat(string description, string costName, int amount)
    {
        RecipeUpdate.ReadCosts(description).Should().Equal(new Dictionary<string, int> { { costName, amount } });
    }

    /// <summary>
    /// Lore as stored from the older mod versions.
    /// </summary>
    [TestCase("§f§lCOMMON\n\n§7Cost\n§fOak Sapling\n\n§eClick to trade!\n§7§eRight-click for more trading options!", "Oak Sapling", 1)]
    [TestCase("§7§8Collection Item\n\n§f§lCOMMON\n\n§7Cost\n§fSeeds §8x12\n\n§eClick to trade!\n§7§eRight-click for more trading options!", "Seeds", 12)]
    [TestCase("§7§8Collection Item\n\n§f§lCOMMON\n\n§7Cost\n§610 Coins\n\n§7Stock\n§6640 §7remaining\n\n§eClick to trade!\n§7§eRight-click for more trading options!", "Coins", 10)]
    [TestCase("§8§l* §8Soulbound §8§l*\n§f§lCOMMON CLOAK\n\n§7Cost\n§650,000 Coins\n\n§eClick to trade!", "Coins", 50_000)]
    // two leading color codes were stored as "250 Pelts": 1
    [TestCase("§7§8This item can be reforged!\n§5§lEPIC BELT\n\n§7Cost\n§r§5250 Pelts\n\n§eClick to trade!", "Pelts", 250)]
    // the "Mana Cost" line of an ability is not the cost header
    [TestCase("§6Ability: Lightning Strike  §e§lRIGHT CLICK\n§7Strikes lightning up to §f10 blocks §7away, dealing\n§7§c46 §7damage to the nearest monster.\n§8Mana Cost: §370\n\n§a§lUNCOMMON WAND\n\n§7Cost\n§65,000 Coins\n\n§eClick to trade!", "Coins", 5000)]
    public void ReadsTheCostFromTheOlderModFormat(string description, string costName, int amount)
    {
        RecipeUpdate.ReadCosts(description).Should().Equal(new Dictionary<string, int> { { costName, amount } });
    }

    [Test]
    public void ReadsEveryCostLineInBothModFormats()
    {
        var expected = new Dictionary<string, int> { { "Sand", 2 }, { "Fermented Spider Eye", 1 } };

        RecipeUpdate.ReadCosts("COMMON\n\nCost\n§fSand §8x2\nFermented Spider Eye\n\nClick to trade!\nRight-click for more trading options!").Should().Equal(expected);
        RecipeUpdate.ReadCosts("§f§lCOMMON\n\n§7Cost\n§fSand §8x2\n§fFermented Spider Eye\n\n§eClick to trade!\n§7§eRight-click for more trading options!").Should().Equal(expected);
    }

    [Test]
    public void StockIsNoCost()
    {
        var description = "§7Use at §bKat §7to upgrade §eWitch Pets\n§e§7from §aUncommon §7to §9Rare.\n\n§9§lRARE PET ITEM\n\n§7Cost\n§9Waterbourne Lice §8x64\n§9Console Panel §8x64\n§9Jawbreaker §8x64\n\n§7Annual Stock §8Year 488\n§61 §7remaining\n\n§eClick to trade!";

        RecipeUpdate.ReadCosts(description).Should().Equal(new Dictionary<string, int> { { "Waterbourne Lice", 64 }, { "Console Panel", 64 }, { "Jawbreaker", 64 } });
        RecipeUpdate.ReadStock(description).Should().Be(1);
    }

    [TestCase("§7§8Collection Item\n\n§f§lCOMMON\n\n§7Cost\n§610 Coins\n\n§7Stock\n§6640 §7remaining\n\n§eClick to trade!", 640)]
    // the same lore without the color code of single style lines; not seen in production yet
    [TestCase("Collection Item\n\nCOMMON\n\nCost\n10 Coins\n\nStock\n§6640 §7remaining\n\nClick to trade!", 640)]
    [TestCase("Collection Item\n\nCOMMON\n\nCost\n10 Coins\n\nStock\n640 remaining\n\nClick to trade!", 640)]
    [TestCase("COMMON\n\nCost\n97 Coins\n\nClick to trade!", 0)]
    public void ReadsTheStock(string description, int stock)
    {
        RecipeUpdate.ReadStock(description).Should().Be(stock);
    }

    /// <summary>
    /// A partial coin price is a discounted one ("Water Bucket" in the "Trades" view, 2026-10-02).
    /// </summary>
    [Test]
    public void PartialCoinsAreNoCost()
    {
        RecipeUpdate.ReadCosts("COMMON\n\nCost\n11.6 Coins\n\nClick to trade!").Should().BeEmpty();
    }

    /// <summary>
    /// The fields missing in production profiles on 2026-10-02 ("Required property ... not found" for
    /// 138 of 139 shop views). The failed read counted as "has the Seal of the Family", so no npc
    /// cost was extracted for these players.
    /// </summary>
    private const string ProfileWithoutRequiredFields = "{\"rift\":{\"inventory\":{}},\"currencies\":{\"essence\":{\"UNDEAD\":{}}},\"pets_data\":{\"autopet\":{},\"pets\":[{}]}}";

    [Test]
    public void ReadsAProfileWithoutTheFieldsTheGeneratedModelRequires()
    {
        FluentActions.Invoking(() => Newtonsoft.Json.JsonConvert.DeserializeObject<Api.Client.Model.Member>(ProfileWithoutRequiredFields))
            .Should().Throw<Newtonsoft.Json.JsonSerializationException>().WithMessage("Required property*");

        var profile = RecipeUpdate.ReadProfile(ProfileWithoutRequiredFields);

        profile.Should().NotBeNull();
        profile.Rift.Inventory.Should().NotBeNull();
        profile.PetsData.Pets.Should().HaveCount(1);
        // it is sent on to the prices api
        FluentActions.Invoking(() => Newtonsoft.Json.JsonConvert.SerializeObject(profile)).Should().NotThrow();
    }

    /// <summary>
    /// With the required fields optional, 3 of 5 production profiles still failed with "Unexpected
    /// token Float when parsing enum. Path 'pets_data.pets[53].extra.blaze_kills'": the generated
    /// model expects an enum where the pet holds a number. The inventories must survive it.
    /// </summary>
    [Test]
    public void ReadsAProfileWithValuesOfAnotherTypeThanTheGeneratedModelExpects()
    {
        var json = "{\"pets_data\":{\"pets\":[{\"type\":\"FROST_WISP\",\"exp\":312541.184,\"active\":false,\"tier\":\"RARE\",\"heldItem\":null,\"candyUsed\":0,\"petSoulbound\":false,\"skin\":null,\"extra\":{\"blaze_kills\":147.0}}]},"
            + "\"inventory\":{\"bag_contents\":{\"talisman_bag\":{\"type\":0,\"data\":\"H4sIAAAAAAAA\"}}}}";

        var profile = RecipeUpdate.ReadProfile(json);

        profile.PetsData.Pets.Should().HaveCount(1);
        profile.PetsData.Pets[0].Type.Should().Be("FROST_WISP");
        profile.Inventory.BagContents.TalismanBag.Data.Should().Be("H4sIAAAAAAAA");
    }

    [Test]
    public void LoreWithoutCostHeaderHasNoCost()
    {
        RecipeUpdate.ReadCosts("§6Ability: Fire Freeze  §e§lRIGHT CLICK\n§8Mana Cost: §b250\n§8Cooldown: §a10s\n\nEPIC SWORD").Should().BeEmpty();
    }
}
