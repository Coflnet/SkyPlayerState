using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class GatheringTaskAuditTests
{
    private static readonly TaskRegistry Registry = new();
    private static string Text(string name) => string.Join(" ", Registry.GetByName(name).StepsForTest.Select(s => s.Text));

    [TestCase("Coal Mine", "ENCHANTED_COAL_BLOCK", "Coal Mining")]
    [TestCase("Diamond Reserve", "ENCHANTED_DIAMOND_BLOCK", "Diamond Mining")]
    [TestCase("Pigmen's Den", "ENCHANTED_REDSTONE_BLOCK", "Redstone Mining")]
    [TestCase("Gold Mine", "ENCHANTED_IRON_BLOCK", "Iron Mining")]
    [TestCase("Lapis Quarry", "ENCHANTED_LAPIS_LAZULI_BLOCK", "Lapis Mining")]
    [TestCase("Slimehill", "ENCHANTED_EMERALD_BLOCK", "Emerald Mining")]
    public void CompactedOreAlone_StillIdentifiesMining(string zone, string tag, string expected)
    {
        var classifier = new TaskClassifier(Registry);
        classifier.Classify(zone, new Dictionary<string, int> { [tag] = 1 }, 10)!.TaskName.Should().Be(expected);
        (classifier.Classify("Village", new Dictionary<string, int> { [tag] = 1 }, 10)?.TaskName).Should().NotBe(expected);
    }

    [TestCase("Ruby Mining", 6)]
    [TestCase("Thyst Mining", 7)]
    [TestCase("Amber Mining", 7)]
    [TestCase("Sapphire Mining", 7)]
    [TestCase("Jade Mining", 7)]
    [TestCase("Topaz Mining", 8)]
    [TestCase("Jasper Mining", 9)]
    [TestCase("Peridot Mining", 9)]
    [TestCase("Aquamarine Mining", 9)]
    [TestCase("Citrine Mining", 9)]
    [TestCase("Onyx Mining", 9)]
    public void GemstoneGuidance_UsesTheVeinsBreakingPower(string task, int power)
    {
        Text(task).Should().Contain($"Breaking Power {power}").And.NotContain("only tool");
        Registry.GetByName(task).WikiUrlForTest.Should().Be("https://hypixelskyblock.minecraft.wiki/w/Gemstone");
        ((MethodTask)Registry.GetByName(task)).RequiredItemsForTest.Should().NotContain(i => i.ItemTag == "GEMSTONE_GAUNTLET");
    }

    [TestCase("Peridot Mining")]
    [TestCase("Aquamarine Mining")]
    [TestCase("Citrine Mining")]
    [TestCase("Onyx Mining")]
    public void GlaciteGemstones_ExplainTheRealAccessRoute(string task)
    {
        Text(task).Should().Contain("Mountain tier 7").And.Contain("Dulin").And.Contain("Secret Railroad")
            .And.Contain("Glacite Tunnels").And.NotContain("Crystal Hollows");
    }

    [TestCase("Ruby Mining")]
    [TestCase("Sludge Mining")]
    [TestCase("Sludge Mining (Gem Mixture)")]
    public void HollowsGuidance_UsesTheCurrentEntryTier(string task)
        => Text(task).Should().Contain("Mountain tier 4").And.NotContain("3 or 4");

    [Test]
    public void PestGuidance_UsesARealVacuumAndExplainsItsControls()
    {
        var task = new PestTask();
        task.RequiredItemsForTest.Single().ItemTag.Should().Be("SKYMART_VACUUM");
        task.WikiUrlForTest.Should().EndWith("/Pests");
        Text("Pest").Should().Contain("hold right-click").And.Contain("only be damaged by vacuums").And.NotContain("Pesterminator vacuum");
    }

    [Test]
    public void SludgeGuidance_DistinguishesDirectDropsAndConcurrentForgeSlots()
    {
        Text("Sludge Mining").Should().Contain("3% chance").And.NotContain("turns into Sludge");
        Text("Sludge Mining (Gem Mixture)").Should().Contain("1.25 Gemstone Mixtures per hour");
    }

    [Test]
    public void FlowerGuidance_ExplainsTheDayAndNightHarvests()
    {
        Text("Sunflower Farming").Should().Contain("during the day");
        Text("Moonflower Farming").Should().Contain("Sunflower").And.Contain("at night");
    }

    [Test]
    public void GardenVisitors_RecognizesTheActualSpacemanReward()
    {
        var classifier = new TaskClassifier(Registry);
        classifier.Classify("The Garden", new Dictionary<string, int> { ["DCTR_SPACE_HELM"] = 1 }, 10)!.TaskName
            .Should().Be("Garden Visitors");
        new GardenVisitorsTask().GetDetectionSignature().DetectionItems.Should().NotContain("SPACE_HELMET");
    }

    [Test]
    public void MushroomFarming_UsesTheRealRedMushroomTag()
    {
        var items = new MushroomFarmingTask().GetDetectionSignature().DetectionItems;
        items.Should().Contain("RED_MUSHROOM").And.NotContain("BUILDER_RED_MUSHROOM");
        PestEvidence.Crops.Should().NotContain("BUILDER_RED_MUSHROOM");
        new TaskClassifier(Registry).Classify("The Garden", new Dictionary<string, int> { ["RED_MUSHROOM"] = 10 }, 10)!.TaskName
            .Should().Be("Mushroom Farming");
    }

    [TestCase("Jungle Island")]
    [TestCase("Viking Longhouse")]
    [TestCase("Spirit Cave")]
    [TestCase("Soul Cave")]
    [TestCase("Trials of Fire")]
    public void ParkIslandTracker_CoversAllParkActivities(string zone)
        => new TheParkTask().CoversLocation(zone).Should().BeTrue();
}
