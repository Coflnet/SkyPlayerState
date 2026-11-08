using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services.Gear;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class InventoryLayoutTests
{
    private static System.Collections.Generic.List<Item> Numbered(int count)
        => Enumerable.Range(0, count).Select(i => new Item { Tag = "SLOT_" + i }).ToList();

    [TestCase(46, 9, 44)]
    [TestCase(45, 9, 44)]
    [TestCase(90, 54, 89)]
    [TestCase(63, 27, 62)]
    public void PlayerInventory_IsMainThenHotbar(int count, int first, int last)
    {
        var inventory = InventoryLayout.PlayerInventory(Numbered(count));

        inventory.Should().HaveCount(36);
        inventory[0].Tag.Should().Be("SLOT_" + first);
        inventory[^1].Tag.Should().Be("SLOT_" + last);
    }

    [Test]
    public void BareInventory_ArmorHotbarAndOffhand()
    {
        var items = Numbered(46);

        InventoryLayout.Armor(items).Select(i => i!.Tag).Should().Equal("SLOT_5", "SLOT_6", "SLOT_7", "SLOT_8");
        InventoryLayout.HotbarIndices(items).Should().Equal(Enumerable.Range(36, 9));
        InventoryLayout.Offhand(items)!.Tag.Should().Be("SLOT_45");
        InventoryLayout.Offhand(Numbered(45)).Should().BeNull();
    }

    [Test]
    public void ChestView_HasNoArmor_AndTheHotbarLast()
    {
        var items = Numbered(90);

        InventoryLayout.Armor(items).Should().BeEmpty();
        InventoryLayout.Offhand(items).Should().BeNull();
        InventoryLayout.HotbarIndices(items).Should().Equal(Enumerable.Range(81, 9));
    }

    /// <summary>
    /// Regression: InventoryChangeUpdate kept the last 36 slots, for a 46 slot (1.21) bare inventory that is 10-45:
    /// the first main slot (9) was dropped and the offhand kept. The armor (5-8) was dropped as well and never captured.
    /// </summary>
    [Test]
    public async Task BareInventoryUpload_KeepsMainSlot9_DropsOffhand_AndCapturesArmor()
    {
        var armor = new Item?[]
        {
            GearTestItems.Armor("FERMENTO_HELMET", "HELMET"), GearTestItems.Armor("FERMENTO_CHESTPLATE", "CHESTPLATE"),
            GearTestItems.Armor("FERMENTO_LEGGINGS", "LEGGINGS"), GearTestItems.Armor("FERMENTO_BOOTS", "BOOTS")
        };
        var items = GearTestItems.BareInventory(armor, [GearTestItems.Hoe()], offhand: new Item { Tag = "OFFHAND_ITEM" },
            firstMain: new Item { Tag = "FIRST_MAIN_SLOT" });
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { Chest = new ChestView { Name = "Crafting", Items = items } }
        };

        await new InventoryChangeUpdate().Process(args);
        await new GearSnapshotListener().Process(args);

        var inventory = args.currentState.Inventory;
        inventory.Should().HaveCount(36);
        inventory[0].Tag.Should().Be("FIRST_MAIN_SLOT");
        inventory.Should().NotContain(i => i.Tag == "OFFHAND_ITEM");
        inventory[27].Tag.Should().Be("THEORETICAL_HOE_WHEAT_3", "the hotbar stays the last 9 entries");
        var slots = args.currentState.ExtractedInfo.CurrentGear!.Slots;
        slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
        slots[GearSnapshot.Boots].Tag.Should().Be("FERMENTO_BOOTS");
        args.currentState.ExtractedInfo.CurrentGear.ToolCandidates.Should().ContainSingle().Which.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
    }

    [Test]
    public void AccessibleInventoryStart_DelegatesToTheLayout()
    {
        CollectionListener.AccessibleInventoryStart(Numbered(46)).Should().Be(9);
        CollectionListener.AccessibleInventoryStart(Numbered(90)).Should().Be(54);
    }
}
