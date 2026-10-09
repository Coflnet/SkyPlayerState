using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Crafts.Client.Model;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ItemCategory = Coflnet.Sky.Items.Client.Model.ItemCategory;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Production: Storm's Chestplate (WISE_WITHER_CHESTPLATE) is an upgrade of WITHER_CHESTPLATE, not a reforge, and
/// has to count as owning it.
/// </summary>
public class ItemUpgradeMapTests
{
    private static readonly Dictionary<string, ItemCategory> Categories = new()
    {
        ["WITHER_CHESTPLATE"] = ItemCategory.CHESTPLATE,
        ["WISE_WITHER_CHESTPLATE"] = ItemCategory.CHESTPLATE,
        ["POWER_WITHER_CHESTPLATE"] = ItemCategory.CHESTPLATE,
        ["TANK_WITHER_CHESTPLATE"] = ItemCategory.CHESTPLATE,
        ["SPEED_WITHER_CHESTPLATE"] = ItemCategory.CHESTPLATE,
        ["WITHER_HELMET"] = ItemCategory.HELMET,
        ["GIANT_FRAGMENT_LASER"] = ItemCategory.FRAGMENT,
        ["ENCHANTED_DIAMOND"] = ItemCategory.UNKNOWN,
        ["DIAMOND_SWORD_LIKE"] = ItemCategory.SWORD,
        ["ZOMBIE_TALISMAN"] = ItemCategory.ACCESSORY,
        ["ZOMBIE_RING"] = ItemCategory.ACCESSORY,
        ["ZOMBIE_ARTIFACT"] = ItemCategory.ACCESSORY,
        ["ENCHANTED_BOOK_THING"] = ItemCategory.CONSUMABLE,
        ["BIGGER_BOOK_THING"] = ItemCategory.CONSUMABLE,
    };

    private static List<(string, IEnumerable<string>)> Recipes() =>
    [
        ("WISE_WITHER_CHESTPLATE", ["GIANT_FRAGMENT_LASER", "WITHER_CHESTPLATE"]),
        ("POWER_WITHER_CHESTPLATE", ["GIANT_FRAGMENT_LASER", "WITHER_CHESTPLATE"]),
        ("TANK_WITHER_CHESTPLATE", ["GIANT_FRAGMENT_LASER", "WITHER_CHESTPLATE"]),
        ("SPEED_WITHER_CHESTPLATE", ["GIANT_FRAGMENT_LASER", "WITHER_CHESTPLATE"]),
        ("ZOMBIE_RING", ["ZOMBIE_TALISMAN"]),
        ("ZOMBIE_ARTIFACT", ["ENCHANTED_DIAMOND", "ZOMBIE_RING"]),
        // crafted from unrelated materials / a piece of another slot: not upgrades
        ("DIAMOND_SWORD_LIKE", ["ENCHANTED_DIAMOND"]),
        ("WITHER_HELMET", ["WITHER_CHESTPLATE"]),
        ("BIGGER_BOOK_THING", ["ENCHANTED_BOOK_THING"]),
    ];

    private static Dictionary<string, HashSet<string>> Bases() => ItemUpgradeMap.Build(Recipes(), Categories);

    [TestCase("WISE_WITHER_CHESTPLATE")]
    [TestCase("POWER_WITHER_CHESTPLATE")]
    [TestCase("TANK_WITHER_CHESTPLATE")]
    [TestCase("SPEED_WITHER_CHESTPLATE")]
    public void WitherChestplateUpgrades_CountAsTheBaseChestplate(string upgrade)
    {
        Bases()[upgrade].Should().BeEquivalentTo(["WITHER_CHESTPLATE"], "the fragment is not gear");

        var owned = GearOwnership.BuildOwnedTags(new StateObject { Inventory = [new Item { Tag = upgrade }] }, null, Bases());

        owned.Should().Contain("WITHER_CHESTPLATE");
        GearOwnership.IsOwned(new RequiredItem { ItemTag = "WITHER_CHESTPLATE", EstimatedPrice = 1 }, owned).Should().BeTrue();
    }

    [Test]
    public void UpgradeOfAnUpgrade_CountsAsEveryLinkOfTheChain()
    {
        Bases()["ZOMBIE_ARTIFACT"].Should().BeEquivalentTo(["ZOMBIE_RING", "ZOMBIE_TALISMAN"]);
    }

    [Test]
    public void OnlyTheUpgradeIsNotTheOtherWayAround_AndItemsThatMerelyConsumeSomethingAreNoUpgrades()
    {
        var bases = Bases();
        bases.Should().NotContainKey("WITHER_CHESTPLATE", "the base does not count as its upgrade");
        bases.Should().NotContainKey("DIAMOND_SWORD_LIKE", "a sword crafted from enchanted diamonds");
        bases.Should().NotContainKey("WITHER_HELMET", "a helmet whose recipe takes a chestplate is another slot");
        bases.Should().NotContainKey("BIGGER_BOOK_THING", "consumables never form upgrade chains");

        var owned = GearOwnership.BuildOwnedTags(new StateObject { Inventory = [new Item { Tag = "WITHER_CHESTPLATE" }] }, null, bases);
        owned.Should().NotContain("WISE_WITHER_CHESTPLATE", "owning the base says nothing about the upgrade");
        GearOwnership.IsOwned(new RequiredItem { ItemTag = "WISE_WITHER_CHESTPLATE", EstimatedPrice = 1 }, owned).Should().BeFalse();
    }

    [Test]
    public void UnknownCategoriesAndCyclicRecipes_AreIgnored()
    {
        var categories = new Dictionary<string, ItemCategory> { ["A"] = ItemCategory.SWORD, ["B"] = ItemCategory.SWORD };
        var bases = ItemUpgradeMap.Build([("A", ["B", "UNLISTED"]), ("B", ["A"])], categories);

        bases["A"].Should().BeEquivalentTo(["B"]);
        bases["B"].Should().BeEquivalentTo(["A"]);
        ItemUpgradeMap.Build(null, categories).Should().BeEmpty();
    }

    [Test]
    public void WithoutAMap_OwnershipWorksAsBefore()
    {
        var state = new StateObject { Inventory = [new Item { Tag = "WISE_WITHER_CHESTPLATE" }] };

        var owned = GearOwnership.BuildOwnedTags(state);

        owned.Should().BeEquivalentTo(["WISE_WITHER_CHESTPLATE"]);
    }

    [Test]
    public async Task Service_BuildsTheMapFromCraftsAndItems_AndCachesIt()
    {
        var crafts = new Mock<ICraftsApi>();
        crafts.Setup(c => c.GetAllAsync(0, It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync([
            new ProfitableCraft { ItemId = "WISE_WITHER_CHESTPLATE", Ingredients = [new Ingredient { ItemId = "WITHER_CHESTPLATE" }] }]);
        var items = new Mock<IItemsApi>();
        items.Setup(i => i.ItemsGetAsync(0, It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(Categories.Select(c =>
            new Coflnet.Sky.Items.Client.Model.Item { Tag = c.Key, Category = c.Value }).ToList());
        var service = new ItemUpgradeService(crafts.Object, items.Object, NullLogger<ItemUpgradeService>.Instance);

        var first = await service.GetBases();
        await service.GetBases();

        first["WISE_WITHER_CHESTPLATE"].Should().BeEquivalentTo(["WITHER_CHESTPLATE"]);
        crafts.Verify(c => c.GetAllAsync(0, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Service_WhenTheServicesAreDown_ReturnsAnEmptyMapInsteadOfThrowing()
    {
        var crafts = new Mock<ICraftsApi>();
        crafts.Setup(c => c.GetAllAsync(0, It.IsAny<System.Threading.CancellationToken>())).ThrowsAsync(new System.Exception("down"));
        var service = new ItemUpgradeService(crafts.Object, new Mock<IItemsApi>().Object, NullLogger<ItemUpgradeService>.Instance);

        var bases = await service.GetBases();

        bases.Should().BeEmpty();
        var owned = GearOwnership.BuildOwnedTags(new StateObject { Inventory = [new Item { Tag = "WISE_WITHER_CHESTPLATE" }] }, null, bases);
        owned.Should().BeEquivalentTo(["WISE_WITHER_CHESTPLATE"]);
    }
}
