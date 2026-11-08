using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Slot layout of an uploaded view, decided by the slot count (the bare inventory title "Crafting" is localized).
/// <list type="bullet">
/// <item>1.21 bare inventory, 46 slots: 0 craft result, 1-4 crafting grid, 5-8 armor (helmet to boots), 9-35 main, 36-44 hotbar, 45 offhand.</item>
/// <item>1.8 bare inventory, 45 slots: the same without the offhand.</item>
/// <item>Chest views: the container slots, then the 36 player slots (27 main, 9 hotbar). No armor.</item>
/// </list>
/// A 45 slot view can also be a one row container; armor reads guard against that with the lore item type.
/// </summary>
public static class InventoryLayout
{
    public const int BareWithOffhandSlots = 46;
    public const int BareLegacySlots = 45;
    public const int PlayerSlots = 36;
    public const int HotbarSlots = 9;
    public const int FirstArmorSlot = 5;
    public const int OffhandSlot = 45;

    public static bool IsBareInventory(int count) => count is BareWithOffhandSlots or BareLegacySlots;

    /// <summary>
    /// Index of the first of the 36 accessible player slots: the last 36 slots, except the 1.21 bare inventory whose
    /// offhand comes after them.
    /// </summary>
    public static int AccessibleStart(IReadOnlyCollection<Item> items)
    {
        var trailing = items.Count == BareWithOffhandSlots ? 1 : 0;
        return Math.Max(0, items.Count - PlayerSlots - trailing);
    }

    /// <summary>The 36 player slots, main inventory first, hotbar last (what <see cref="StateObject.Inventory"/> holds).</summary>
    public static List<Item> PlayerInventory(IReadOnlyList<Item> items)
        => items.Skip(AccessibleStart(items)).Take(PlayerSlots).ToList();

    /// <summary>Indices of the 9 hotbar slots, empty when the view is too small to contain the player inventory.</summary>
    public static IEnumerable<int> HotbarIndices(IReadOnlyList<Item> items)
    {
        if (items.Count < PlayerSlots)
            return [];
        var first = AccessibleStart(items) + PlayerSlots - HotbarSlots;
        return Enumerable.Range(first, HotbarSlots);
    }

    /// <summary>Armor slots helmet, chestplate, leggings, boots of a bare inventory, empty for other views.</summary>
    public static IReadOnlyList<Item?> Armor(IReadOnlyList<Item> items)
    {
        if (!IsBareInventory(items.Count))
            return [];
        return Enumerable.Range(FirstArmorSlot, 4).Select(i => items[i]).ToList();
    }

    /// <summary>The offhand item of a 1.21 bare inventory, null otherwise.</summary>
    public static Item? Offhand(IReadOnlyList<Item> items)
        => items.Count == BareWithOffhandSlots ? items[OffhandSlot] : null;
}
