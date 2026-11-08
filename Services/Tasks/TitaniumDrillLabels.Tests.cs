using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: Mithril and Glacite Mining labelled TITANIUM_DRILL_1 as "DR-X555 / Breaking Power 9", but that tag
/// is the DR-X355 (Breaking Power 7), and Glacite (block strength 6000) needs Breaking Power 9.
/// </summary>
public class TitaniumDrillLabelsTests
{
    [Test]
    public void MithrilMining_RequiresTheDrX355()
    {
        var drill = new MithrilMiningTask().RequiredItemsForTest.Single();

        drill.ItemTag.Should().Be("TITANIUM_DRILL_1");
        drill.Name.Should().Be("Titanium Drill DR-X355");
        drill.Reason.Should().Contain("Breaking Power 7");
        new MithrilMiningTask().StepsForTest.Should().Contain(s => s.Text.Contains("DR-X355 (Breaking Power 7"));
    }

    [Test]
    public void GlaciteMining_RequiresABreakingPower9Drill()
    {
        var drill = new GlaciteMiningTask().RequiredItemsForTest.Single();

        drill.ItemTag.Should().Be("TITANIUM_DRILL_3");
        drill.Name.Should().Be("Titanium Drill DR-X555");
        new GlaciteMiningTask().StepsForTest.Should().NotContain(s => s.Text.Contains("DR-X355"));
    }

    // Breaking Power requirements: any drill with at least that power counts
    [TestCase("TITANIUM_DRILL_3", "DIVAN_DRILL", true)]
    [TestCase("TITANIUM_DRILL_3", "TITANIUM_DRILL_4", true)]
    [TestCase("TITANIUM_DRILL_3", "GEMSTONE_DRILL_3", true)]  // regression: Topaz Drill KGR-12 is Breaking Power 9
    [TestCase("TITANIUM_DRILL_3", "GEMSTONE_DRILL_4", true)]  // regression: Jasper Drill X is Breaking Power 9
    [TestCase("TITANIUM_DRILL_3", "GEMSTONE_DRILL_2", false)] // Breaking Power 8
    [TestCase("TITANIUM_DRILL_3", "TITANIUM_DRILL_1", false)]
    [TestCase("TITANIUM_DRILL_1", "DIVAN_DRILL", true)]
    [TestCase("TITANIUM_DRILL_1", "GEMSTONE_DRILL_1", true)]
    [TestCase("TITANIUM_DRILL_1", "GEMSTONE_GAUNTLET", true)]  // Breaking Power 8
    [TestCase("TITANIUM_DRILL_3", "GEMSTONE_GAUNTLET", false)]
    [TestCase("TITANIUM_DRILL_1", "PICKONIMBUS", false)]       // breaks after its uses
    // one-directional: a titanium drill never stands in for a gemstone tool or a Divan's Drill
    [TestCase("DIVAN_DRILL", "TITANIUM_DRILL_1", false)]
    [TestCase("DIVAN_DRILL", "TITANIUM_DRILL_4", false)]
    [TestCase("GEMSTONE_DRILL_3", "TITANIUM_DRILL_3", false)]
    [TestCase("GEMSTONE_GAUNTLET", "TITANIUM_DRILL_4", false)]
    public void Drill_Ownership(string required, string owned, bool expected)
    {
        GearOwnership.IsOwned(new RequiredItem { ItemTag = required }, new HashSet<string> { owned }).Should().Be(expected);
    }

    [Test]
    public void Labels_MatchTheNotEnoughUpdatesItemNames()
    {
        var itemsDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "NotEnoughUpdates-REPO", "items"));
        if (!Directory.Exists(itemsDir))
        {
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir}");
            return;
        }
        foreach (var drill in new MithrilMiningTask().RequiredItemsForTest.Concat(new GlaciteMiningTask().RequiredItemsForTest))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(itemsDir, drill.ItemTag + ".json")));
            var displayName = json.RootElement.GetProperty("displayname").GetString()!;
            displayName.Should().EndWith(drill.Name, $"{drill.ItemTag} is named {displayName} in NEU");
        }
    }
}
