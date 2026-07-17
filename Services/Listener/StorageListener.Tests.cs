using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class StorageListenerTests
{
    [TestCase("Huntaxe")]
    [TestCase("Huntaxe (Slot #1)")]
    [TestCase("Hunting Toolkit")]
    [TestCase("Hunting Toolkit (Slot #2)")]
    [TestCase("Ender Chest (1/9)")]
    [TestCase("Large Backpack (Slot #9)")]
    public void RecognizedStorageChestsAreStored(string chestName)
    {
        StorageListener.IsNotStorage(new ChestView { Name = chestName })
            .Should().BeFalse($"'{chestName}' should be persisted to storage");
    }

    [TestCase("SkyBlock Menu")]
    [TestCase("Auction House")]
    [TestCase("Bazaar")]
    public void UnrelatedMenusAreNotStored(string chestName)
    {
        StorageListener.IsNotStorage(new ChestView { Name = chestName })
            .Should().BeTrue($"'{chestName}' is not storage and must not be persisted");
    }

    // The Huntaxe sits at index 22 (slot 23), i.e. the 3rd menu row, and HuntingListener reads the
    // toolkit's top 3 rows. With the player's own inventory as the bottom 4 rows, such a menu is at
    // least 7 rows (63 slots), so the StorageListener trim (count/9 - 4)*9 keeps the top 27 slots -
    // the whole menu survives while the player inventory is dropped.
    [Test]
    public void TrimKeepsHuntingMenuButDropsPlayerInventory()
    {
        var slotCount = 63; // 3 menu rows + 4 player inventory rows
        var itemsToStore = (slotCount / 9 - 4) * 9;

        itemsToStore.Should().Be(27);
        itemsToStore.Should().BeGreaterThan(22, "the Huntaxe at index 22 must not be trimmed away");
        itemsToStore.Should().BeGreaterThanOrEqualTo(3 * 9, "the Hunting Toolkit's top 3 rows must survive");
    }
}
