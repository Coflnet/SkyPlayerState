using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Records the worn equipment, armor and the active pet as owned. Worn gear is not in the inventory view, but
/// the "Stats &amp; Equipment" menu and the first page of "Loadouts" show it with real item tags in fixed
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
    private static readonly Regex LoadoutsFirstPage = new(@"^\(1/\d+\) Loadouts$", RegexOptions.Compiled);

    public override Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest?.Name == null || chest.Items == null)
            return Task.CompletedTask;
        var name = Services.MenuTitleNormalizer.StripColors(chest.Name).Trim();
        int petSlot;
        if (name == "Stats & Equipment")
            petSlot = 47;
        else if (LoadoutsFirstPage.IsMatch(name))
            petSlot = 21;
        else
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
