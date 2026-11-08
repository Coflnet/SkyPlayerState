using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using MessagePack;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>GearSnapshotListener, TabStatsListener and the raw menu stats of PlayerStatsListener on whole uploads.</summary>
public class GearListenersTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static MockedUpdateArgs Inventory(StateObject state, string title, List<Item> items, int? heldSlot = null) => new()
    {
        currentState = state,
        msg = new UpdateMessage { ReceivedAt = T0, Chest = new ChestView { Name = title, Items = items, HeldSlot = heldSlot } }
    };

    private static MockedUpdateArgs Tab(StateObject state, params string[] lines) => new()
    {
        currentState = state,
        msg = new UpdateMessage { ReceivedAt = T0, Kind = UpdateMessage.UpdateKind.Tab, Tab = lines }
    };

    private static Dictionary<int, Item> StatsMenu() => new()
    {
        [10] = new Item { Tag = "LOTUS_NECKLACE", Description = "\n§6§lLEGENDARY NECKLACE", ExtraAttributes = new() { ["modifier"] = "rooted", ["tier"] = 5 }, Enchantments = new() { ["green_thumb"] = 5 } },
        [11] = GearTestItems.Armor("FERMENTO_HELMET", "HELMET"),
        [20] = GearTestItems.Armor("FERMENTO_CHESTPLATE", "CHESTPLATE"),
        [29] = GearTestItems.Armor("FERMENTO_LEGGINGS", "LEGGINGS"),
        [38] = GearTestItems.Armor("FERMENTO_BOOTS", "BOOTS"),
        [47] = new Item
        {
            Tag = "PET_ELEPHANT", ItemName = "§7[Lvl 100] §6Elephant",
            ExtraAttributes = new() { ["petInfo"] = new Dictionary<string, object> { ["type"] = "ELEPHANT", ["tier"] = "LEGENDARY", ["heldItem"] = "GREEN_BANDANA" } }
        },
        [15] = new Item { ItemName = "§aFarming Stats", Description = "Stats\n\n §6 Farming Fortune §f1,450\n §6 Wheat Fortune §f120\n §3☯ Farming Wisdom §f60" }
    };

    [Test]
    public async Task StatsAndEquipment_CapturesEquipmentArmorPetTool_RawStats_AndBaseStats()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentTask = "Wheat Farming";
        var args = Inventory(state, "Stats & Equipment", GearTestItems.ChestView(StatsMenu(), [GearTestItems.Hoe(), GearTestItems.Sword()]), heldSlot: 81);

        await new GearSnapshotListener().Process(args);
        await new PlayerStatsListener().Process(args);

        var info = state.ExtractedInfo;
        var gear = info.CurrentGear!;
        gear.Slots[GearSnapshot.Necklace].Enchants["green_thumb"].Should().Be(5);
        gear.Slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
        gear.Slots.Should().NotContainKey(GearSnapshot.Cloak);
        gear.Pet!.Type.Should().Be("ELEPHANT");
        gear.Pet.Level.Should().Be(100);
        gear.ToolEvidence.Should().Be(GearToolEvidence.HeldSlot);
        gear.EquipmentAt.Should().Be(T0);
        // 1,450 - 4 x 30 (armor) - 60 (the held hoe: slot 2 is empty in this fixture, the mod's held slot names it)
        info.BaseStats!.Stats.Should().BeEquivalentTo(new Dictionary<string, double> { ["Farming Fortune"] = 1270, ["Wheat Fortune"] = 120 });
        info.BaseStats.At.Should().Be(T0);
        info.BaseStats.HeldStats.Should().BeEquivalentTo(new Dictionary<string, double> { ["Farming Fortune"] = 60 });
        info.BaseStats.Pet!.Type.Should().Be("ELEPHANT");
        info.PlayerStats["Farming Wisdom"].Should().Be(60, "the max-merged stats keep every stat as before");
    }

    [Test]
    public async Task MenuSlotWithoutAttributes_DoesNotDowngradeTheKnownPiece()
    {
        var state = new StateObject();
        await new GearSnapshotListener().Process(Inventory(state, "Crafting", GearTestItems.BareInventory(
            [GearTestItems.Armor("FERMENTO_HELMET", "HELMET")])));
        var menu = new Dictionary<int, Item> { [11] = new Item { Tag = "FERMENTO_HELMET", ItemName = "Helmet" } };

        await new GearSnapshotListener().Process(Inventory(state, "(1/3) Loadouts", GearTestItems.ChestView(menu)));

        state.ExtractedInfo.CurrentGear!.Slots[GearSnapshot.Helmet].Reforge.Should().Be("mossy");
    }

    [Test]
    public async Task PetsMenu_SetsTheActivePet()
    {
        var state = new StateObject();
        state.ExtractedInfo.Pets = [new ExtractedInfo.PetState { Type = "MOOSHROOM_COW", Tier = "LEGENDARY", Level = 100, IsActive = true, HeldItem = "MINOS_RELIC" }];

        await new GearSnapshotListener().Process(Inventory(state, "Pets", GearTestItems.ChestView()));

        var pet = state.ExtractedInfo.CurrentGear!.Pet!;
        pet.Type.Should().Be("MOOSHROOM_COW");
        pet.HeldItem.Should().Be("MINOS_RELIC");
    }

    [Test]
    public async Task Bait_IsCollectedFromTheInventory()
    {
        var state = new StateObject();
        var items = GearTestItems.ChestView(hotbar: [GearTestItems.Rod(), GearTestItems.Bait("WHALE_BAIT"), GearTestItems.Bait("WORM_BAIT"), GearTestItems.Bait("WHALE_BAIT")]);

        await new GearSnapshotListener().Process(Inventory(state, "Some Chest", items));

        state.ExtractedInfo.CurrentGear!.Bait.Should().Equal("WHALE_BAIT", "WORM_BAIT");
    }

    [Test]
    public async Task OneRowContainer_WithFortyFiveSlots_IsNotReadAsArmor()
    {
        var state = new StateObject();
        var items = GearTestItems.BareInventory();
        items.RemoveAt(45);
        items[5] = GearTestItems.Sword();

        await new GearSnapshotListener().Process(Inventory(state, "Dispenser", items));

        state.ExtractedInfo.CurrentGear!.Slots.Should().NotContainKey(GearSnapshot.Helmet);
        state.ExtractedInfo.CurrentGear.ArmorAt.Should().Be(default);
    }

    [Test]
    public async Task TabStats_LiveStats_AndASessionSampleWithTheSessionsTool()
    {
        var state = new StateObject();
        var info = state.ExtractedInfo;
        info.CurrentTask = "Wheat Farming";
        await new GearSnapshotListener().Process(Inventory(state, "Crafting", GearTestItems.BareInventory(hotbar: [GearTestItems.Hoe()]), heldSlot: 36));
        info.CurrentSession = new TaskSession { DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(info.CurrentSession, info.CurrentGear, T0);

        await new TabStatsListener().Process(Tab(state, " Farming Fortune: 1,500", " Wheat Fortune: 300", " Alive: 2", " [Lvl 100] Elephant"));

        info.LiveStats.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["Farming Fortune"] = 1500, ["Wheat Fortune"] = 300, [TabStatsParser.PestsAliveStat] = 2, [TabStatsParser.PestsPenaltyStat] = 0
        });
        info.LiveStatsAt.Should().Be(T0);
        info.CurrentSession.StatSamples["Farming Fortune"].Should().Be(1);
        info.CurrentSession.StatSums["Wheat Fortune"].Should().Be(300);
        info.LivePet!.Type.Should().Be("ELEPHANT");
        info.CurrentGear!.Pet!.Type.Should().Be("ELEPHANT");
    }

    [Test]
    public async Task TabSample_CountsForTheRunningSession()
    {
        var state = new StateObject();
        var info = state.ExtractedInfo;
        info.CurrentTask = "Wheat Farming";
        await new GearSnapshotListener().Process(Inventory(state, "Crafting", GearTestItems.BareInventory(hotbar: [GearTestItems.Hoe(), GearTestItems.Sword()])));
        info.CurrentSession = new TaskSession { DetectedTask = "Wheat Farming" };
        GearSessionTracker.Track(info.CurrentSession, info.CurrentGear, T0);

        await new TabStatsListener().Process(Tab(state, " Farming Fortune: \uE0511,500"));

        info.CurrentGear!.ToolPiece!.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3", "the optimal farming tool, not the sword");
        info.CurrentGear.ToolEvidence.Should().Be(GearToolEvidence.Optimal);
        info.CurrentSession.StatSamples["Farming Fortune"].Should().Be(1);
    }

    [Test]
    public async Task FortyFiveSlotView_WithEmptyArmorSlots_LeavesTheArmorUnknown()
    {
        var state = new StateObject();
        await new GearSnapshotListener().Process(Inventory(state, "Crafting", GearTestItems.BareInventory(
            [GearTestItems.Armor("FERMENTO_HELMET", "HELMET")])));
        var container = GearTestItems.BareInventory();
        container.RemoveAt(45);
        var later = Inventory(state, "Dispenser", container);
        later.msg.ReceivedAt = T0.AddMinutes(5);

        await new GearSnapshotListener().Process(later);

        var gear = state.ExtractedInfo.CurrentGear!;
        gear.Slots[GearSnapshot.Helmet].Tag.Should().Be("FERMENTO_HELMET");
        gear.ArmorAt.Should().Be(T0);
    }

    [Test]
    public async Task Offhand_IsCapturedForOwnership_AndStarredGearCountsAsItsBaseItem()
    {
        var state = new StateObject();
        var items = GearTestItems.BareInventory([GearTestItems.Armor("STARRED_SPIRIT_MASK", "HELMET")], offhand: new Item { Tag = "SOME_OFFHAND_ITEM" });

        await new InventoryChangeUpdate().Process(Inventory(state, "Crafting", items));
        await new GearSnapshotListener().Process(Inventory(state, "Crafting", items));

        var owned = Tasks.GearOwnership.BuildOwnedTags(state);
        owned.Should().Contain("SOME_OFFHAND_ITEM").And.Contain("STARRED_SPIRIT_MASK").And.Contain("SPIRIT_MASK");
        state.Inventory.Should().NotContain(i => i.Tag == "SOME_OFFHAND_ITEM");
    }

    [Test]
    public async Task TabPet_DisplayNameMatchingAMenuPet_KeepsTheMenuType()
    {
        var state = new StateObject();
        var info = state.ExtractedInfo;
        // hypothetical display name that does not spell the petInfo type
        info.Pets = [new ExtractedInfo.PetState { Name = "[Lvl 100] Rock Dog", Type = "ROCK", Tier = "EPIC", Level = 100 }];
        info.CurrentGear = new GearSnapshot { Pet = new GearPet { Type = "ROCK", Tier = "EPIC", Level = 100 }, PetAt = T0.AddDays(-1) };

        await new TabStatsListener().Process(Tab(state, " [Lvl 100] Rock Dog"));

        info.LivePet!.Type.Should().Be("ROCK");
        info.CurrentGear.Pet!.Type.Should().Be("ROCK");
        info.CurrentGear.Pet.Tier.Should().Be("EPIC");
        info.CurrentGear.PetAt.Should().Be(T0);
    }

    [Test]
    public async Task TabPet_SpellingTheGearPetsType_ConfirmsIt()
    {
        var state = new StateObject();
        var info = state.ExtractedInfo;
        info.CurrentGear = new GearSnapshot { Pet = new GearPet { Type = "GOLDEN_DRAGON", Tier = "LEGENDARY", Level = 99 } };

        // the derived type "GOLDEN-DRAGON" differs, the letters do not
        await new TabStatsListener().Process(Tab(state, " [Lvl 100] Golden-Dragon"));

        info.CurrentGear.Pet!.Type.Should().Be("GOLDEN_DRAGON");
        info.CurrentGear.Pet.Tier.Should().Be("LEGENDARY");
        info.CurrentGear.Pet.Level.Should().Be(100);
    }

    [Test]
    public async Task TabPet_TakesTierAndHeldItemFromThePetsMenu_AndConfirmsTheGearPet()
    {
        var state = new StateObject();
        var info = state.ExtractedInfo;
        info.Pets = [new ExtractedInfo.PetState { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 100, HeldItem = "GREEN_BANDANA" }];
        info.CurrentGear = new GearSnapshot { Pet = new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 99 }, PetAt = T0.AddDays(-1) };

        await new TabStatsListener().Process(Tab(state, " [Lvl 100] Elephant"));

        info.LivePet!.Tier.Should().Be("LEGENDARY");
        info.LivePet.HeldItem.Should().Be("GREEN_BANDANA");
        info.CurrentGear.Pet!.Level.Should().Be(100);
        info.CurrentGear.PetAt.Should().Be(T0);
        info.LiveStats.Should().BeNull("no stat line was shown");
    }

    [Test]
    public void NewStateFields_SurviveMessagePackAndCopy()
    {
        var gear = GearSessionTrackerTests.Gear(GearNormalizer.Normalize(GearTestItems.Hoe())!, pet: new GearPet { Type = "ELEPHANT", Level = 100, Stats = new() { ["Strength"] = 5 } });
        gear.ToolCandidates = [GearNormalizer.Normalize(GearTestItems.Rod())!];
        gear.HeldKey = "held";
        gear.Offhand = new GearPiece { Tag = "OFFHAND" };
        var info = new ExtractedInfo
        {
            CurrentGear = gear, LiveStats = new() { ["Farming Fortune"] = 1 }, LiveStatsAt = T0, LivePet = new GearPet { Type = "ELEPHANT" },
            BaseStats = new BaseStatsReading
            {
                Stats = new() { ["Farming Fortune"] = 900 }, At = T0, ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                HeldStats = new() { ["Farming Fortune"] = 60 }, Pet = new GearPet { Type = "ELEPHANT", Stats = new() { ["Strength"] = 1 } }
            },
            CurrentSession = new TaskSession { Gear = gear, GearFingerprint = "f", GearChanged = true, StatSums = new() { ["x"] = 1 }, StatSamples = new() { ["x"] = 1 }, LastStatSampleAt = T0, GearSince = T0 }
        };

        foreach (var copy in new[] { MessagePackSerializer.Deserialize<ExtractedInfo>(MessagePackSerializer.Serialize(info)), new ExtractedInfo(info) })
        {
            copy.CurrentGear!.ToolPiece!.Enchants["harvesting"].Should().Be(6);
            copy.CurrentGear.ToolPiece.Stats["Farming Fortune"].Should().Be(60);
            copy.CurrentGear.Pet!.Stats["Strength"].Should().Be(5);
            copy.CurrentGear.ToolCandidates.Should().ContainSingle().Which.Tag.Should().Be("ROD_OF_THE_SEA");
            copy.CurrentGear.HeldKey.Should().Be("held");
            copy.CurrentGear.Offhand!.Tag.Should().Be("OFFHAND");
            copy.LiveStats!["Farming Fortune"].Should().Be(1);
            copy.LivePet!.Type.Should().Be("ELEPHANT");
            copy.BaseStats!.Stats["Farming Fortune"].Should().Be(900);
            copy.BaseStats.At.Should().Be(T0);
            copy.BaseStats.ProfileId.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
            copy.BaseStats.HeldStats["Farming Fortune"].Should().Be(60);
            copy.BaseStats.Pet!.Stats["Strength"].Should().Be(1);
            copy.CurrentSession!.GearChanged.Should().BeTrue();
            copy.CurrentSession.GearSince.Should().Be(T0);
            copy.CurrentSession.Gear!.Slots.Should().ContainKey(GearSnapshot.Tool);
            copy.CurrentSession.StatSamples["x"].Should().Be(1);
        }
        new ExtractedInfo(info).CurrentGear.Should().NotBeSameAs(info.CurrentGear);
    }
}
