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
}
