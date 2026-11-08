using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Records the worn equipment, armor and the active pet as owned. Equipment is in no inventory view (the bare inventory
/// upload has the armor, see GearSnapshotListener), but
/// the "Stats &amp; Equipment" menu and every page of "Loadouts" show it with real item tags in fixed
/// slots (checked against production samples of both menus):
/// equipment (necklace, cloak, belt, gloves) in slots 10, 19, 28, 37, armor (helmet, chestplate, leggings, boots)
/// in 11, 20, 29, 38, the active pet in slot 47 (Stats &amp; Equipment) or 21 (Loadouts).
/// The other tagged slots of those menus (the other loadouts' icons) are not worn and are ignored.
/// The tags are kept as they appear (STARRED_SPIRIT_MASK, WISE_WITHER_CHESTPLATE), see GearOwnership for the matching.
/// </summary>
public class WornGearListener : UpdateListener
{
    /// <summary>Gear is optional bookkeeping for the ownership display, a failure must not stop the state save.</summary>
    public override bool Optional => true;

    public const int MaxTags = 100;
    private static readonly int[] EquipmentAndArmorSlots = [10, 19, 28, 37, 11, 20, 29, 38];
    private static readonly Regex LoadoutsPage = new(@"^\(\d+/\d+\) Loadouts$", RegexOptions.Compiled);

    public override Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest?.Name == null || chest.Items == null)
            return Task.CompletedTask;
        if (WornGearPetSlot(chest.Name) is not { } petSlot)
            return Task.CompletedTask;

        var tags = ReadWornTags(chest.Items, petSlot);
        if (tags.Count == 0)
            return Task.CompletedTask;
        var known = args.currentState.ExtractedInfo.WornGearTags ??= new();
        foreach (var tag in tags.Where(t => !known.Contains(t)))
            known.Add(tag);
        if (known.Count > MaxTags)
            known.RemoveRange(0, known.Count - MaxTags);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The active pet's slot when the view shows the worn gear (47 in "Stats &amp; Equipment", 21 on every "Loadouts" page:
    /// production samples of pages 1-3 all show the worn column in the same slots), null for every other view.
    /// </summary>
    public static int? WornGearPetSlot(string? title)
    {
        if (title == null)
            return null;
        var name = MenuTitleNormalizer.StripColors(title).Trim();
        if (name == "Stats & Equipment")
            return 47;
        return LoadoutsPage.IsMatch(name) ? 21 : null;
    }

    /// <summary>Equipment slots (necklace, cloak, belt, gloves) of the worn gear menus.</summary>
    public static readonly int[] EquipmentMenuSlots = [10, 19, 28, 37];
    /// <summary>Armor slots (helmet, chestplate, leggings, boots) of the worn gear menus.</summary>
    public static readonly int[] ArmorMenuSlots = [11, 20, 29, 38];

    /// <summary>The tags in the fixed worn slots, the pet only when its tag is a PET_ tag.</summary>
    public static List<string> ReadWornTags(IReadOnlyList<Item> items, int petSlot)
    {
        var tags = new List<string>();
        foreach (var slot in EquipmentAndArmorSlots)
            if (slot < items.Count && !string.IsNullOrEmpty(items[slot]?.Tag))
                tags.Add(items[slot].Tag!);
        if (petSlot < items.Count && items[petSlot]?.Tag is { } pet && pet.StartsWith("PET_", StringComparison.Ordinal))
            tags.Add(pet);
        return tags;
    }
}
