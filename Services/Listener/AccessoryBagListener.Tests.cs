using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class AccessoryBagListenerTests
{
    [TestCase("Accessory Bag (2/3)", 2, 3)]
    [TestCase("Accessory Bag (1/2)", 1, 2)]
    [TestCase("Accessory Bag", 1, 1)]
    public void ParsesPageNames(string name, int page, int count)
    {
        AccessoryBagListener.ParsePageName(name).Should().Be((page, count));
    }

    [TestCase("Accessory Bag Thaumaturgy")]
    [TestCase("Ender Chest (1/9)")]
    [TestCase("")]
    [TestCase(null)]
    public void RejectsOtherNames(string name)
    {
        AccessoryBagListener.ParsePageName(name).Should().BeNull();
    }

    [Test]
    public void ParsesPowerBehindOtherStatLines()
    {
        AccessoryBagListener.ParsePower("§7Strength: §c+5\n§7Accessory Power: §6+12\n\nlore").Should().Be(12);
        AccessoryBagListener.ParsePower("no power here").Should().Be(0);
        AccessoryBagListener.ParsePower(null).Should().Be(0);
    }

    [Test]
    public void ExtractsAccessoriesAndSkipsNavigationAndPlayerInventory()
    {
        var items = BuildPage(45);
        items[0] = Accessory("WEDDING_RING_7", "§5Itchy Exquisite Ring of Love", 4, "itchy", "§7Accessory Power: §6+12");
        items[3] = Accessory("MAGNETIC_TALISMAN", "Magnetic Talisman", 2, "unpleasant", "§7Accessory Power: §6+5");
        items[36] = new Item { Tag = "NEXT_PAGE_ARROW", ItemName = "Next Page", Description = "Page 2" }; // navigation row
        items[45 + 2] = Accessory("HYPERION", "Hyperion", 5, null, "§7Accessory Power: §6+99"); // player inventory

        var result = AccessoryBagListener.ExtractAccessories(items, 2);

        result.Select(a => a.Tag).Should().Equal("WEDDING_RING_7", "MAGNETIC_TALISMAN");
        result[0].Should().BeEquivalentTo(new BagAccessory
        {
            Tag = "WEDDING_RING_7", ItemName = "Itchy Exquisite Ring of Love", Tier = 4, Modifier = "itchy",
            AccessoryPower = 12, Page = 2, Slot = 0
        });
        result[1].Slot.Should().Be(3);
    }

    /// <summary>Items in the trailing 36 player inventory slots (even with a tag) must never be stored</summary>
    [Test]
    public void PlayerInventoryPartIsExcluded()
    {
        var items = BuildPage(54);
        for (var i = 54; i < items.Count; i++)
            items[i] = Accessory("INV_" + i, "inv", 1, null, "§7Accessory Power: §6+1");

        AccessoryBagListener.ExtractAccessories(items, 1).Should().BeEmpty();
    }

    [Test]
    public void ReadsRecombobulationAndEnrichment()
    {
        var items = BuildPage(45);
        var item = Accessory("X", "X", 5, "fierce", "§7Accessory Power: §6+16");
        item.ExtraAttributes["rarity_upgrades"] = 1;
        item.ExtraAttributes["talisman_enrichment"] = "critical_damage";
        items[1] = item;

        var result = AccessoryBagListener.ExtractAccessories(items, 1).Single();

        result.Recombobulated.Should().BeTrue();
        result.Enrichment.Should().Be("critical_damage");
        result.Tier.Should().Be(5);
    }

    [Test]
    public void AssembleIgnoresPagesBeyondNewestPageCount()
    {
        var now = System.DateTime.UtcNow;
        var rows = new List<AccessoryBagService.BagPageRow>
        {
            Row(1, 2, now, "A"), Row(2, 2, now, "B"), Row(3, 3, now.AddDays(-1), "STALE")
        };

        var bag = AccessoryBagService.Assemble(rows);

        bag.PageCount.Should().Be(2);
        bag.SeenPages.Should().Equal(1, 2);
        bag.Accessories.Select(a => a.Tag).Should().Equal("A", "B");
    }

    [TestCase(4, 4)]
    [TestCase("4", 4)]
    [TestCase("LEGENDARY", 4)]
    [TestCase("legendary", 4)]
    [TestCase("MYTHIC", 5)]
    [TestCase("nonsense", 0)]
    [TestCase(null, 0)]
    public void ReadsTierAsIndexOrName(object tier, int expected)
    {
        var attributes = new Dictionary<string, object>();
        if (tier != null)
            attributes["tier"] = tier;
        AccessoryBagListener.ReadTier(attributes).Should().Be(expected);
    }

    [Test]
    public void DoesNotStoreForUnknownPlayerOrNonBagPage()
    {
        var page = ((int page, int pageCount)?)(1, 1);
        var items = BuildPage(45);
        AccessoryBagListener.ShouldStore(page, items, System.Guid.Empty).Should().BeFalse();
        AccessoryBagListener.ShouldStore(page, items, null).Should().BeFalse();
        AccessoryBagListener.ShouldStore(null, items, System.Guid.NewGuid()).Should().BeFalse();
        AccessoryBagListener.ShouldStore(page, items, System.Guid.NewGuid()).Should().BeTrue();
    }

    [Test]
    public void AssembleOfNothingIsEmptyBag()
    {
        var bag = AccessoryBagService.Assemble(new());
        bag.Accessories.Should().BeEmpty();
        bag.PageCount.Should().Be(0);
    }

    private static readonly System.Guid ProfileA = System.Guid.NewGuid();
    private static readonly System.Guid ProfileB = System.Guid.NewGuid();
    private static readonly System.DateTime Now = new(2026, 11, 4, 12, 0, 0, System.DateTimeKind.Utc);

    private static AccessoryBagService.BagPageRow ProfileRow(System.Guid profile, int page, System.DateTime at)
    {
        var row = Row(page, 1, at, "T");
        row.ProfileId = profile;
        return row;
    }

    [Test]
    public void SelectProfileUsesRequestedProfile()
    {
        var rows = new List<AccessoryBagService.BagPageRow> { ProfileRow(ProfileA, 1, Now), ProfileRow(ProfileB, 1, Now.AddHours(1)) };
        AccessoryBagService.SelectProfile(rows, ProfileA).Should().OnlyContain(r => r.ProfileId == ProfileA);
    }

    [Test]
    public void SelectProfileFallsBackToUnknownProfileGroup()
    {
        var rows = new List<AccessoryBagService.BagPageRow> { ProfileRow(ProfileB, 1, Now.AddHours(1)), ProfileRow(System.Guid.Empty, 1, Now) };
        AccessoryBagService.SelectProfile(rows, ProfileA).Should().OnlyContain(r => r.ProfileId == System.Guid.Empty);
    }

    [Test]
    public void SelectProfileIsEmptyWithoutMatchOrUnknownGroup()
    {
        var rows = new List<AccessoryBagService.BagPageRow> { ProfileRow(ProfileB, 1, Now) };
        AccessoryBagService.SelectProfile(rows, ProfileA).Should().BeEmpty();
    }

    [Test]
    public void SelectProfileWithoutRequestUsesNewestOpenedBag()
    {
        var rows = new List<AccessoryBagService.BagPageRow>
        {
            ProfileRow(ProfileA, 1, Now), ProfileRow(ProfileA, 2, Now.AddMinutes(5)),
            ProfileRow(ProfileB, 1, Now.AddMinutes(2))
        };
        AccessoryBagService.SelectProfile(rows, null).Should().OnlyContain(r => r.ProfileId == ProfileA);
        AccessoryBagService.SelectProfile(new(), null).Should().BeEmpty();
    }

    [Test]
    public void AssembleReportsProfileId()
    {
        AccessoryBagService.Assemble(new() { ProfileRow(ProfileA, 1, Now) }).ProfileId.Should().Be(ProfileA);
    }

    private static AccessoryBagService.BagPageRow Row(int page, int count, System.DateTime at, string tag) => new()
    {
        Page = page, PageCount = count, UpdatedAt = at,
        Accessories = Newtonsoft.Json.JsonConvert.SerializeObject(new List<BagAccessory> { new() { Tag = tag } })
    };

    /// <summary>chest slots followed by 36 inventory slots, all empty</summary>
    private static List<Item> BuildPage(int chestSlots)
        => Enumerable.Range(0, chestSlots + 36).Select(_ => new Item()).ToList();

    private static Item Accessory(string tag, string name, int tier, string modifier, string lore)
    {
        var attributes = new Dictionary<string, object> { ["tier"] = tier };
        if (modifier != null)
            attributes["modifier"] = modifier;
        return new Item { Tag = tag, ItemName = name, Description = lore, ExtraAttributes = attributes };
    }
}
