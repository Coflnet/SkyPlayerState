using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class OptimalToolTests
{
    private static System.Collections.Generic.List<GearPiece> Candidates(params Item[] hotbar)
        => OptimalTool.Candidates(GearTestItems.BareInventory(hotbar: hotbar));

    [Test]
    public void Combat_AspectOfTheDragons_BeatsTheAspectOfTheEnd()
    {
        // lore from ekwav-state.json: Damage +225 vs +100; the AOTE comes first in the hotbar
        var tool = OptimalTool.Pick(Candidates(GearTestItems.AspectOfTheEnd(), GearTestItems.AspectOfTheDragons()), "F7");

        tool!.Tag.Should().Be("ASPECT_OF_THE_DRAGON");
        tool.Stats["Damage"].Should().Be(225);
    }

    [Test]
    public void Combat_HyperionBeatsGiantsSword_AndUtilitySwordsOnlyWithoutAWeapon()
    {
        // NotEnoughUpdates-REPO lore: Hyperion Damage +260 Strength +150, Giant's Sword Damage +500, AOTV Damage +120 Strength +100
        var hyperion = Weapon("HYPERION", "§7Damage: §c+260\n§7Strength: §c+150\n§7Ferocity: §c+30\n§7Intelligence: §a+350", "LEGENDARY DUNGEON SWORD");
        var giants = Weapon("GIANTS_SWORD", "§7Damage: §c+500", "LEGENDARY DUNGEON LONGSWORD");
        var aotv = Weapon("ASPECT_OF_THE_VOID", "§7Damage: §c+120\n§7Strength: §c+100", "EPIC SWORD");

        OptimalTool.Pick(Candidates(giants, hyperion), "F7")!.Tag.Should().Be("HYPERION", "265 x 2.5 beats 505 x 1");
        OptimalTool.Pick(Candidates(aotv, GearTestItems.AspectOfTheEnd()), "F7")!.Tag.Should().Be("ASPECT_OF_THE_VOID", "without a real weapon the best utility sword");
        OptimalTool.Pick(Candidates(aotv, Weapon("ROGUE_SWORD", "§7Damage: §c+20", "COMMON SWORD")), "F7")!.Tag
            .Should().Be("ROGUE_SWORD", "any real weapon beats a utility sword");
    }

    private static Item Weapon(string tag, string lore, string footer)
        => new() { Tag = tag, Description = lore + "\n\n" + footer, ExtraAttributes = new() { ["uuid"] = "w-" + tag } };

    [Test]
    public void Fishing_LavaAndWaterRodsAreSeparate_SeaCreatureChanceFirst()
    {
        // NotEnoughUpdates-REPO lore: Hellfire Rod (lava) FS +60 SCC +14%, Rod of the Sea FS +80 SCC +8%, Auger Rod FS +110
        var hellfire = new Item { Tag = "HELLFIRE_ROD", Description = "§7Fishing Speed: §a+60\n§7Sea Creature Chance: §c+14%\n\n§6§lLEGENDARY FISHING ROD", ExtraAttributes = new() { ["uuid"] = "hellfire" } };
        var auger = new Item { Tag = "AUGER_ROD", Description = "§7Fishing Speed: §a+110\n\n§6§lLEGENDARY FISHING ROD", ExtraAttributes = new() { ["uuid"] = "auger" } };
        var candidates = Candidates(hellfire, auger, GearTestItems.Rod());

        OptimalTool.Pick(candidates, "Water Fishing")!.Tag.Should().Be("ROD_OF_THE_SEA", "Sea Creature Chance first, the lava rod is no water rod");
        OptimalTool.Pick(candidates, "Crimson Fishing")!.Tag.Should().Be("HELLFIRE_ROD");
        OptimalTool.Pick(Candidates(auger, GearTestItems.Rod()), "Crimson Fishing").Should().BeNull("no lava rod");
    }

    [Test]
    public void ItemsWithoutPrimaryStat_AreNoTool_AndAxesAreNoFarmingTool()
    {
        var noStats = GearTestItems.FarmingTool("TEST_HOE_WITHOUT_STATS", "§7Speed: §f+5");
        var axe = new Item { Tag = "JUNGLE_AXE", Description = "§7Damage: §c+20\n\n§a§lUNCOMMON AXE", ExtraAttributes = new() { ["uuid"] = "axe" } };

        OptimalTool.Pick(Candidates(noStats, axe), "Wheat Farming").Should().BeNull();
    }

    /// <summary>Regression: the cap took the first 10 tools in slot order, a full hotbar pushed the best one out.</summary>
    [Test]
    public void Candidates_KeepTheBestPerGroup_RegardlessOfSlotOrder()
    {
        var weak = Enumerable.Range(0, 8).Select(i => Weapon("WEAK_SWORD_" + i, "§7Damage: §c+" + (10 + i), "COMMON SWORD")).ToArray();
        var items = GearTestItems.BareInventory(hotbar: weak, firstMain: GearTestItems.AspectOfTheDragons());
        items[10] = GearTestItems.Rod();

        var candidates = OptimalTool.Candidates(items);

        candidates.Count(c => c.ItemType == "SWORD").Should().Be(OptimalTool.PerGroup);
        candidates.Select(c => c.Tag).Should().Contain("ASPECT_OF_THE_DRAGON").And.Contain("ROD_OF_THE_SEA");
        OptimalTool.Pick(candidates, "F7")!.Tag.Should().Be("ASPECT_OF_THE_DRAGON");
    }

    [Test]
    public void Farming_TheCropsOwnTool_BeatsAGenericHoeWithMoreFortune()
    {
        var candidates = Candidates(
            GearTestItems.FarmingTool("ADVANCED_GARDENING_HOE", "§7Farming Fortune: §6+15"),
            GearTestItems.FarmingTool("MELON_DICER_3", "§7Melon Slice Fortune: §6+4\n§7Farming Wisdom: §3+1"),
            GearTestItems.Hoe());

        OptimalTool.Pick(candidates, "Melon Farming")!.Tag.Should().Be("MELON_DICER_3");
        OptimalTool.Pick(candidates, "Wheat Farming")!.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3");
        OptimalTool.Pick(candidates, "Cactus Farming")!.Tag.Should().Be("THEORETICAL_HOE_WHEAT_3", "without a cactus tool the most Farming Fortune wins");
    }

    [Test]
    public void Mining_MostMiningSpeed_AndNoNonMiningTool()
    {
        var titanium = new Item
        {
            Tag = "TITANIUM_DRILL_1", ExtraAttributes = new() { ["uuid"] = "titanium" },
            Description = "§8Breaking Power 7\n\n§7Damage: §c+75\n§7Mining Speed: §a+700\n§7Mining Fortune: §a+25\n\n§5§lEPIC DRILL" // NEU lore
        };

        var tool = OptimalTool.Pick(Candidates(GearTestItems.AspectOfTheDragons(), GearTestItems.Drill(), titanium), "Mithril Mining");

        tool!.Tag.Should().Be("TITANIUM_DRILL_1");
        tool.Stats["Breaking Power"].Should().Be(7);
    }

    [Test]
    public void Fishing_TheRod_AndUnknownTaskOrNoToolGivesNothing()
    {
        var candidates = Candidates(GearTestItems.AspectOfTheDragons(), GearTestItems.Rod());

        OptimalTool.Pick(candidates, "Water Fishing")!.Tag.Should().Be("ROD_OF_THE_SEA");
        OptimalTool.Pick(candidates, null).Should().BeNull();
        OptimalTool.Pick(candidates, "Wheat Farming").Should().BeNull();
    }

    /// <summary>
    /// Regression: unbound tasks fall back to the "other" (combat) spec, so a gathering task without a gear profile
    /// picked a sword as its optimal tool. Only combat tasks may get a weapon; the rest stay ambiguous.
    /// </summary>
    [Test]
    public void Other_UnboundGatheringTask_GetsNoTool_UnboundCombatTaskGetsTheWeapon()
    {
        var candidates = Candidates(GearTestItems.AspectOfTheDragons(), GearTestItems.Drill());

        GearCategories.ForTask("Flint Mining").Should().Be(GearModifierTable.Other, "the precondition: no profile is bound");
        OptimalTool.Pick(candidates, "Flint Mining").Should().BeNull("a mining task is not done with a sword");
        OptimalTool.Pick(candidates, "Not A Task").Should().BeNull();
        GearCategories.ForTask("Zealots (FD)").Should().Be(GearModifierTable.Other);
        OptimalTool.Pick(candidates, "Zealots (FD)")!.Tag.Should().Be("ASPECT_OF_THE_DRAGON");
    }

    [Test]
    public void IsCombatTask_CombatCategories_SlayersAndTheAllowlistedKillTasks()
    {
        var slayers = new Tasks.TaskRegistry().MethodTasks.Select(t => t.GetDetectionSignature())
            .Where(s => s.Category == "Slayer").Select(s => s.MethodName).ToList();

        slayers.Should().HaveCountGreaterThan(3).And.OnlyContain(name => GearCategories.IsCombatTask(name));
        foreach (var task in new[] { "Zealots (FD)", "Mage Outlaw", "Diana", "Zealot (Hunting)", "Ghost (Hunting)", "Crimson Isle Mobs" })
            GearCategories.IsCombatTask(task).Should().BeTrue(task);
        foreach (var task in new[] { "Lunar Moth Trapping", "Pest (Hunting)", "Hunting Trap", "Galatea Contests", "Flint Mining", "Wheat Farming", "Not A Task" })
            GearCategories.IsCombatTask(task).Should().BeFalse(task);
    }

    [Test]
    public void Candidates_HotbarFirst_ThenMain_ToolTypesOnly()
    {
        var items = GearTestItems.BareInventory(hotbar: [GearTestItems.Bait("WORM_BAIT"), GearTestItems.Rod()], firstMain: GearTestItems.Drill());

        OptimalTool.Candidates(items).Select(c => c.Tag).Should().Equal("ROD_OF_THE_SEA", "MITHRIL_DRILL_2");
    }

    [Test]
    public void Evidence_HeldSlotOnlyWhenTheOptimalToolIsHeld()
    {
        var tool = OptimalTool.Pick(Candidates(GearTestItems.Rod()), "Water Fishing");

        OptimalTool.Evidence(tool, GearFingerprint.PieceKey(tool!)).Should().Be(GearToolEvidence.HeldSlot);
        OptimalTool.Evidence(tool, "something else").Should().Be(GearToolEvidence.Optimal);
        OptimalTool.Evidence(tool, null).Should().Be(GearToolEvidence.Optimal);
        OptimalTool.Evidence(null, null).Should().Be(GearToolEvidence.Ambiguous);
    }

    [Test]
    public async Task Listener_PicksTheCurrentTasksTool_AndMarksItHeld()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentTask = "F7";
        var items = GearTestItems.BareInventory(hotbar: [GearTestItems.AspectOfTheEnd(), GearTestItems.AspectOfTheDragons(), GearTestItems.Rod()]);
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Chest = new ChestView { Name = "Crafting", Items = items, HeldSlot = 37 } }
        };

        await new GearSnapshotListener().Process(args);

        var gear = state.ExtractedInfo.CurrentGear!;
        gear.ToolPiece!.Tag.Should().Be("ASPECT_OF_THE_DRAGON");
        gear.ToolEvidence.Should().Be(GearToolEvidence.HeldSlot);
        gear.ToolCandidates.Should().HaveCount(3);
    }
}
