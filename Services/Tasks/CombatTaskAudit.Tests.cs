using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Services;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Current wiki mechanics checked against the actual collection parser and registered classifier.</summary>
public class CombatTaskAuditTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);
    private static string Steps(string name) => string.Join(" ", Registry.GetByName(name).StepsForTest.Select(s => s.Text));

    [TestCase("Lumisquid", "Fishing Net")]
    [TestCase("Shellwise", "Fishing Net")]
    [TestCase("Joydive", "Fishing Net")]
    [TestCase("Coralot", "warning sign")]
    [TestCase("Spike", "Turbo Fishing Net")]
    [TestCase("Bambuleaf", "40–44 Bamboo")]
    [TestCase("Mochibear", "40–44 Bamboo")]
    [TestCase("Mossybit", "click a Mossybit")]
    [TestCase("Hideonleaf", "bullet back three times")]
    [TestCase("Dreadwing", "Lasso")]
    public void CritterGuidance_ExplainsTheActualCaptureMechanic(string task, string mechanic)
    {
        Steps(task).Should().Contain(mechanic);
        ((MethodTask)Registry.GetByName(task)).RequiredItemsForTest.Should().NotContain(i => i.ItemTag == "ASPECT_OF_THE_DRAGON");
    }

    [TestCase("Seer", "Dragon's Nest")]
    [TestCase("Star Sentry", "Dwarven Mines")]
    [TestCase("Golden Ghoul", "Crypts")]
    [TestCase("Dreadwing", "Moonglade Marsh")]
    public void MobGuidance_LinksTheVerifiedMobAndNamesItsLocation(string task, string location)
    {
        Steps(task).Should().Contain(location);
        Registry.GetByName(task).WikiUrlForTest.Should().EndWith("/" + task.Replace(" ", "_"));
    }

    // These are the collection listener's capture notification format. Verify normalization before classification,
    // rather than injecting invented SHARD_* names directly into the classifier.
    [TestCase("You caught a Coralot and gained a Coralot Shard!", "Murkwater Loch", "Coralot")]
    [TestCase("You caught a Shellwise and gained a Shellwise Shard!", "Murkwater Depths", "Shellwise")]
    [TestCase("You caught a Seer and gained a Seer Shard!", "Dragon's Nest", "Seer")]
    [TestCase("You caught a Star Sentry and gained a Star Sentry Shard!", "Royal Mines", "Star Sentry")]
    public void CaptureNotification_ReachesRegisteredTask(string line, string zone, string task)
    {
        CollectionListener.TryParseShardGain(line, out var tag, out var count).Should().BeTrue();
        (Classifier.Classify(zone, new() { [tag] = count }, 10)?.TaskName).Should().Be(task);
    }

    [TestCase("Crypts", "ROTTEN_FLESH", "Revenant Slayer")]
    [TestCase("Crypts", "REVENANT_VISCERA", "Revenant Slayer")]
    [TestCase("Arachne's Burrow", "TARANTULA_SILK", "Tarantula Slayer")]
    [TestCase("Burning Desert", "BURNING_EYE", "Tarantula Slayer (Crimson Isle)")]
    [TestCase("Smoldering Tomb", "ENCHANTMENT_SMOLDERING_1", "Blaze Slayer")]
    public void OrdinaryMobLootAndCraftedMaterials_DoNotProveASlayerQuest(string zone, string tag, string task)
        => (Classifier.Classify(zone, new() { [tag] = 10 }, 10)?.TaskName).Should().NotBe(task);

    [TestCase("Dragon's Nest")]
    [TestCase("Zealot Bruiser Hideout")]
    public void ZealotEyesAlone_DoNotProveVoidgloom(string zone)
        => (Classifier.Classify(zone, new() { ["SUMMONING_EYE"] = 1 }, 10)?.Category).Should().NotBe("Slayer");

    [TestCase("Crypts", "WARDEN_HEART", "Revenant Slayer")]
    [TestCase("Howling Cave", "OVERFLUX_CAPACITOR", "Sven Slayer")]
    [TestCase("Forest", "ENCHANTMENT_ULTIMATE_CHIMERA_1", "Diana")]
    public void VerifiedRareRewards_IdentifyTheirActivity(string zone, string tag, string task)
        => (Classifier.Classify(zone, new() { [tag] = 1 }, 10)?.TaskName).Should().Be(task);

    [Test]
    public void OrdinaryMobLoot_RemainsSupportingEvidenceNextToBossRewards()
    {
        (Classifier.Classify("Crypts", new() { ["REVENANT_FLESH"] = 5, ["ROTTEN_FLESH"] = 10 }, 10)?.TaskName).Should().Be("Revenant Slayer");
        (Classifier.Classify("Burning Desert", new() { ["TARANTULA_WEB"] = 5, ["BURNING_EYE"] = 10 }, 10)?.TaskName).Should().Be("Tarantula Slayer (Crimson Isle)");
        (Classifier.Classify("Smoldering Tomb", new() { ["DERELICT_ASHE"] = 5, ["ENCHANTMENT_SMOLDERING_1"] = 1 }, 10)?.TaskName).Should().Be("Blaze Slayer");
    }

    [Test]
    public async Task Diana_IsNotRejectedDuringJerryWhoCanActivateTheRitual()
    {
        var parameters = new TaskParams
        {
            TestTime = DateTime.UtcNow, ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(), Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            LocationProfit = [], CleanPrices = [], BazaarPrices = [], Names = []
        };
        parameters.CurrentMayor = "jerry";
        (await new DianaTask().Execute(parameters)).IsAccessible.Should().BeTrue();
    }

    [Test]
    public void BlazeGuidance_UsesAttunedDaggers_AndAnUnlockableBatphone()
    {
        foreach (var task in new[] { "Blaze Slayer", "Inferno Demonlord" })
        {
            Steps(task).Should().Contain("daggers").And.Contain("attunement").And.Contain("Wolf Slayer III");
            Steps(task).Should().NotContain("buy a Maddox Batphone").And.NotContain("Juju Shortbow");
        }
    }

    [TestCase("Stronghold")]
    [TestCase("Mage Outpost")]
    [TestCase("The Dukedom")]
    public void SharedMagmaUrchinAlone_DoesNotProveBarbarianDukeX(string zone)
        => (Classifier.Classify(zone, new() { ["MAGMA_URCHIN"] = 1 }, 10)?.TaskName).Should().NotBe("Barbarian Duke X");

    [Test]
    public void DukeClothAlongsideMagmaUrchin_ProvesTheDukeKill()
        => (Classifier.Classify("The Dukedom", new() { ["LEATHER_CLOTH"] = 1, ["MAGMA_URCHIN"] = 1 }, 10)?.TaskName)
            .Should().Be("Barbarian Duke X");

    [Test]
    public void KuudraFormula_DoesNotPriceAnUnspecifiedAttributeShard()
    {
        foreach (var task in Registry.MethodTasks.OfType<BaseKuudraTask>())
            task.FormulaDropsForTest.Should().NotContain(d => d.ItemTag == "ATTRIBUTE_SHARD");
    }
}
