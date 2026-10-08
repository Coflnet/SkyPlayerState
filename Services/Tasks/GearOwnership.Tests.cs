using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class GearOwnershipTests
{
    private static Item Tag(string tag) => new() { Tag = tag };

    private static TaskResult Result(params RequiredItem[] items) =>
        new() { Breakdown = new MethodBreakdown { RequiredItems = items.ToList() } };

    private static RequiredItem Req(string tag, long price = 0, string category = null) =>
        new() { ItemTag = tag, EstimatedPrice = price, Category = category };

    private static TaskParams Params(StateObject state, IEnumerable<IEnumerable<Item>> storage = null, bool incomplete = false) =>
        new() { OwnedItemTags = state == null ? null : GearOwnership.BuildOwnedTags(state, storage), OwnedItemsIncomplete = incomplete };

    [Test]
    public void ItemInInventory_IsOwned()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("GEMSTONE_GAUNTLET")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeTrue();
        result.Breakdown.GearOwned.Should().BeTrue();
        result.Breakdown.MissingItemsCost.Should().Be(0);
    }

    [Test]
    public void ItemInStorageContainer_IsOwned()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000));
        var state = new StateObject();
        GearOwnership.Annotate(result, Params(state, [new List<Item> { Tag("GEMSTONE_GAUNTLET") }]));
        result.Breakdown.RequiredItems[0].Owned.Should().BeTrue();
        result.Breakdown.GearOwned.Should().BeTrue();
    }

    [Test]
    public void ItemInStateStorageAndHuntingToolkit_IsOwned()
    {
        var state = new StateObject { Storage = [[Tag("JUNGLE_PICKAXE")]] };
        state.ExtractedInfo.HuntingToolkitItems = [Tag("FLINT_SHOVEL")];
        var result = Result(Req("JUNGLE_PICKAXE"), Req("FLINT_SHOVEL"));
        GearOwnership.Annotate(result, Params(state));
        result.Breakdown.GearOwned.Should().BeTrue();
    }

    [Test]
    public void PetFromPetList_IsOwned()
    {
        var state = new StateObject();
        state.ExtractedInfo.Pets = [new ExtractedInfo.PetState { Type = "GRIFFIN" }];
        var result = Result(Req("PET_GRIFFIN"));
        GearOwnership.Annotate(result, Params(state));
        result.Breakdown.RequiredItems[0].Owned.Should().BeTrue();
    }

    [Test]
    public void AbsentItem_IsNotOwned_AndCostsItsPrice()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("DIRT")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeFalse();
        result.Breakdown.MissingItemsCost.Should().Be(5_000_000);
        result.Breakdown.GearOwned.Should().BeFalse();
    }

    [Test]
    public void AbsentItem_WithUnreadableStorage_StaysUnknown()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000));
        GearOwnership.Annotate(result, Params(new StateObject(), incomplete: true));
        result.Breakdown.RequiredItems[0].Owned.Should().BeNull();
        result.Breakdown.GearOwned.Should().BeNull();
        result.Breakdown.MissingItemsCost.Should().BeNull();
    }

    [Test]
    public void NoState_EverythingIsNull()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000), Req("TERMINATOR", 1));
        GearOwnership.Annotate(result, Params(null));
        result.Breakdown.RequiredItems.Should().OnlyContain(r => r.Owned == null);
        result.Breakdown.GearOwned.Should().BeNull();
        result.Breakdown.MissingItemsCost.Should().BeNull();
    }

    [Test]
    public void MixedItems_SumsOnlyTheMissingOnes()
    {
        var result = Result(Req("GEMSTONE_GAUNTLET", 5_000_000), Req("JUNGLE_PICKAXE", 300_000), Req("FLINT_SHOVEL", 2_000));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("JUNGLE_PICKAXE")] }));
        result.Breakdown.RequiredItems.Select(r => r.Owned).Should().Equal(false, true, false);
        result.Breakdown.MissingItemsCost.Should().Be(5_002_000);
        result.Breakdown.GearOwned.Should().BeFalse();
    }

    [Test]
    public void NoRequiredItems_GearOwnedIsNull()
    {
        var result = Result();
        GearOwnership.Annotate(result, Params(new StateObject()));
        result.Breakdown.GearOwned.Should().BeNull();
        result.Breakdown.MissingItemsCost.Should().BeNull();
    }

    [Test]
    public void BetterWeaponThanExampleAotd_CountsAsOwned()
    {
        var result = Result(Req("ASPECT_OF_THE_DRAGON", 100_000));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("HYPERION")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeTrue();
        result.Breakdown.GearOwned.Should().BeTrue();
    }

    [Test]
    public void ExampleWeaponAbsent_IsUnknownNotMissing()
    {
        var result = Result(Req("ASPECT_OF_THE_DRAGON", 100_000));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("SOME_OTHER_SWORD")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeNull();
        result.Breakdown.GearOwned.Should().BeNull();
        result.Breakdown.MissingItemsCost.Should().BeNull();
    }

    [Test]
    public void HuntWeaponCategory_SatisfiedByHuntingLadderItem()
    {
        var result = Result(Req("ASPECT_OF_THE_DRAGON", 100_000, "HUNT_WEAPON"));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("LUNGE_AXE")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeTrue();
    }

    [Test]
    public void HigherToolTier_SatisfiesLowerTierRequirement()
    {
        var result = Result(Req("THEORETICAL_HOE_WHEAT_1", 1), Req("PUMPKIN_DICER", 1), Req("TITANIUM_DRILL_1", 1));
        var state = new StateObject { Inventory = [Tag("THEORETICAL_HOE_WHEAT_3"), Tag("PUMPKIN_DICER_2"), Tag("DIVAN_DRILL")] };
        GearOwnership.Annotate(result, Params(state));
        result.Breakdown.RequiredItems.Select(r => r.Owned).Should().Equal(true, true, true);
    }

    [Test]
    public void LowerTier_DoesNotSatisfyHigherRequirement()
    {
        var result = Result(Req("THEORETICAL_HOE_WHEAT_2", 1));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("THEORETICAL_HOE_WHEAT_1")] }));
        result.Breakdown.RequiredItems[0].Owned.Should().BeFalse();
    }

    [Test]
    public void WornArmorConsumablesAndPets_StayUnknownWhenAbsent()
    {
        var result = Result(Req("WITHER_CHESTPLATE"), Req("KISMET_FEATHER"), Req("SHARD_VIPER"), Req("PET_GRIFFIN"));
        GearOwnership.Annotate(result, Params(new StateObject()));
        result.Breakdown.RequiredItems.Should().OnlyContain(r => r.Owned == null);
    }

    [Test]
    public void AlternativeVoidgloomWeapon_SatisfiesTerminatorEntry()
    {
        var result = Result(Req("TERMINATOR", 1), Req("VOID_SWORD", 1));
        GearOwnership.Annotate(result, Params(new StateObject { Inventory = [Tag("VOID_SWORD")] }));
        result.Breakdown.GearOwned.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteOne_FillsOwnershipOnRealTask_PricesTheMissingItem_AndKeepsProfitUnchanged()
    {
        var service = new TaskExecutionService(null, null, null, null, null, new TaskRegistry(), null, null, null);
        TaskParams Make(HashSet<string> owned) => new()
        {
            TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            LocationProfit = new Dictionary<string, Services.TrackedProfitService.Period[]>(),
            CleanPrices = new Dictionary<string, long>
            {
                ["THEORETICAL_HOE_WHEAT_1"] = 7_000_000, ["ENCHANTED_WHEAT"] = 100, ["ENCHANTED_SEEDS"] = 100
            },
            BazaarPrices = [],
            Names = new Dictionary<string, string>(),
            OwnedItemTags = owned
        };
        var unknown = await service.ExecuteOne(Make(null), "Wheat Farming");
        var without = await service.ExecuteOne(Make([]), "Wheat Farming");
        var with = await service.ExecuteOne(Make(["THEORETICAL_HOE_WHEAT_1"]), "Wheat Farming");

        unknown.Breakdown.RequiredItems.Should().ContainSingle().Which.EstimatedPrice.Should().Be(7_000_000);
        unknown.Breakdown.RequiredItems[0].Owned.Should().BeNull();
        unknown.Breakdown.GearOwned.Should().BeNull();
        without.Breakdown.RequiredItems[0].Owned.Should().BeFalse();
        without.Breakdown.MissingItemsCost.Should().Be(7_000_000);
        without.Breakdown.GearOwned.Should().BeFalse();
        with.Breakdown.GearOwned.Should().BeTrue();
        with.Breakdown.MissingItemsCost.Should().Be(0);
        without.ProfitPerHour.Should().Be(unknown.ProfitPerHour).And.Be(with.ProfitPerHour);
        without.IsAccessible.Should().Be(unknown.IsAccessible);
    }
}
