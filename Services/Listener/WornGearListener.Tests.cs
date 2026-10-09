using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Slot layouts taken from production samples of "Stats &amp; Equipment" and "Loadouts" (tags kept, names anonymised).
/// </summary>
public class WornGearListenerTests
{
    private static List<Item> Menu(Dictionary<int, string> tagged, Dictionary<int, string>? buttons = null)
    {
        var items = Enumerable.Range(0, 90).Select(_ => new Item { Tag = null, ItemName = "" }).ToList();
        foreach (var (slot, tag) in tagged)
            items[slot] = new Item { Tag = tag, ItemName = tag };
        foreach (var (slot, name) in buttons ?? [])
            items[slot] = new Item { Tag = null, ItemName = name };
        return items;
    }

    private static MockedUpdateArgs Args(string title, List<Item> items)
        => new()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { Chest = new ChestView { Name = title, Items = items } }
        };

    private static readonly Dictionary<int, string> StatsEquipment = new()
    {
        [2] = "DUNGEONBREAKER", // a tool in the menu, not worn gear
        [10] = "STARRED_BONE_NECKLACE", [19] = "STARRED_SHADOW_ASSASSIN_CLOAK", [28] = "STARRED_ADAPTIVE_BELT", [37] = "SOULWEAVER_GLOVES",
        [11] = "STARRED_SPIRIT_MASK", [20] = "WISE_WITHER_CHESTPLATE", [29] = "WISE_WITHER_LEGGINGS", [38] = "WISE_WITHER_BOOTS",
        [47] = "PET_PHOENIX"
    };

    [Test]
    public async Task StatsAndEquipment_RecordsWornEquipmentArmorAndPet_NotOtherTaggedSlots()
    {
        var args = Args("Stats & Equipment", Menu(StatsEquipment, new() { [49] = "Close" }));

        await new WornGearListener().Process(args);

        args.currentState.ExtractedInfo.WornGearTags.Should().BeEquivalentTo(
            "STARRED_BONE_NECKLACE", "STARRED_SHADOW_ASSASSIN_CLOAK", "STARRED_ADAPTIVE_BELT", "SOULWEAVER_GLOVES",
            "STARRED_SPIRIT_MASK", "WISE_WITHER_CHESTPLATE", "WISE_WITHER_LEGGINGS", "WISE_WITHER_BOOTS", "PET_PHOENIX");
    }

    [Test]
    public async Task Loadouts_FirstPage_ReadsWornSlotsAndPetSlot21_IgnoringTheOtherLoadoutIcons()
    {
        var args = Args("(1/2) Loadouts", Menu(new()
        {
            [10] = "BACKWATER_NECKLACE", [19] = "BACKWATER_CLOAK", [28] = "BACKWATER_BELT", [37] = "BACKWATER_GLOVES",
            [11] = "SHARK_SCALE_HELMET", [20] = "SHARK_SCALE_CHESTPLATE", [29] = "SHARK_SCALE_LEGGINGS", [38] = "SHARK_SCALE_BOOTS",
            [21] = "PET_AMMONITE", [14] = "HELIANTHUS_HELMET", [25] = "SNOW_SUIT_CHESTPLATE", [47] = "PET_SHOULD_NOT_COUNT"
        }));

        await new WornGearListener().Process(args);

        args.currentState.ExtractedInfo.WornGearTags.Should().BeEquivalentTo(
            "BACKWATER_NECKLACE", "BACKWATER_CLOAK", "BACKWATER_BELT", "BACKWATER_GLOVES",
            "SHARK_SCALE_HELMET", "SHARK_SCALE_CHESTPLATE", "SHARK_SCALE_LEGGINGS", "SHARK_SCALE_BOOTS", "PET_AMMONITE");
    }

    [TestCase("(2/2) Loadouts")]
    [TestCase("Catacombs Gate")]
    [TestCase("Pets")]
    public async Task OtherMenus_AreIgnored(string title)
    {
        var args = Args(title, Menu(StatsEquipment));

        await new WornGearListener().Process(args);

        args.currentState.ExtractedInfo.WornGearTags.Should().BeEmpty();
    }

    [Test]
    public async Task Tags_AccumulateWithoutDuplicates_AndAreCapped()
    {
        var args = Args("Stats & Equipment", Menu(StatsEquipment));
        var listener = new WornGearListener();
        await listener.Process(args);
        await listener.Process(args);
        args.currentState.ExtractedInfo.WornGearTags.Should().HaveCount(9);

        args.currentState.ExtractedInfo.WornGearTags.AddRange(Enumerable.Range(0, 200).Select(i => $"OLD_{i}"));
        await listener.Process(args);
        args.currentState.ExtractedInfo.WornGearTags.Count.Should().BeLessThanOrEqualTo(WornGearListener.MaxTags);
    }

    [Test]
    public void WornGear_CountsAsOwned_IncludingTheUnstarredTagOfAStarredPiece()
    {
        var state = new StateObject();
        state.ExtractedInfo.WornGearTags = ["STARRED_SPIRIT_MASK", "PET_PHOENIX", "WISE_WITHER_CHESTPLATE"];

        var owned = GearOwnership.BuildOwnedTags(state);

        owned.Should().Contain(["STARRED_SPIRIT_MASK", "SPIRIT_MASK", "PET_PHOENIX", "WISE_WITHER_CHESTPLATE"]);
        var item = new RequiredItem { ItemTag = "SPIRIT_MASK", EstimatedPrice = 1 };
        GearOwnership.IsOwned(item, owned).Should().BeTrue();
    }
}
