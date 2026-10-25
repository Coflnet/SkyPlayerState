using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Where/HowTo of the tasks whose production zone usage (replay 2026-10-22..25) did not match their text:
/// the named places must be real places of the task (declared Locations, directly or via their island) and
/// must be what a player is told.
/// </summary>
public class TaskHowToLocationsTests
{
    private static readonly TaskRegistry Registry = new();

    private static MethodTask Get(string name) => (MethodTask)Registry.GetByName(name);

    private static string Text(MethodTask task) => string.Join(" ", task.StepsForTest.Select(s => s.Text));

    // task, Where, zones the text must name
    private static readonly (string Task, string Where, string[] Named)[] Cases =
    [
        ("Gold Mining", "Mines of Divan", ["Mines of Divan", "Crystal Hollows"]),
        ("Coal Mining", "Magma Fields", ["Magma Fields", "Crystal Hollows"]),
        ("Obsidian Mining", "The End", ["The End", "Dragon's Nest", "Breaking Power 4"]),
        ("Crimson Fishing", "Stronghold", ["Stronghold"]),
        ("Crimson Fishing (Hunting)", "Stronghold", ["Stronghold"]),
        ("T4 Voidglooms", "Void Sepulture", ["Void Sepulture"]),
        ("Fig Foraging", "Moonglade Marsh", ["Evergreen Plateau"]),
        ("Helix Foraging", "Torrhus Canyon", ["Torrhus Canyon"]),
        ("Sven Slayer", "Howling Cave", ["Ruins", "Soul Cave", "Spirit Cave"]),
        ("Winter Fishing", "Hot Springs", ["Hot Springs"]),
        ("Voracious Spider", "Arachne's Burrow", ["Arachne's Burrow"]),
        ("Diamond Mining", "Royal Mines", ["Royal Mines"]),
        ("Red Mushroom", "Glowing Mushroom Cave", ["Glowing Mushroom Cave"]),
        ("Water Fishing", "Village", ["Village", "Spider's Den"]),
        ("Galatea Fishing", "Murkwater Depths", ["Murkwater Depths"]),
        ("Coralot", "Murkwater Loch", ["Murkwater Depths"]),
        ("Shellwise", "Murkwater Depths", ["Murkwater Depths"]),
        ("Joydive", "Murkwater Depths", ["Murkwater Depths"]),
        ("Mochibear", "Tranquility Sanctum", ["Tranquility Sanctum"]),
        ("Umber Mining", "Glacite Tunnels", ["Glacite Tunnels", "Glacite Mineshafts"]),
        ("Tungsten Mining", "Glacite Tunnels", ["Glacite Tunnels", "Glacite Mineshafts"]),
        ("Glacite Mining", "Glacite Tunnels", ["Glacite Tunnels", "Glacite Mineshafts"]),
        ("Tarantula Slayer (Crimson Isle)", "Burning Desert", ["Burning Desert", "Dragontail"]),
        ("Blaze Slayer", "Smoldering Tomb", ["Smoldering Tomb", "The Wasteland"]),
        ("Zealots (FD)", "The End", ["Dragon's Nest", "Zealot Bruiser Hideout"]),
    ];

    [TestCaseSource(nameof(Cases))]
    public void WhereAndTextNameTheProductionZones((string Task, string Where, string[] Named) c)
    {
        var task = Get(c.Task);
        task.Should().NotBeNull();
        task.WhereForTest.Should().Be(c.Where);

        var text = Text(task);
        foreach (var named in c.Named)
            text.Should().Contain(named);
    }

    [TestCase("Gold Mining", "Mines of Divan")]
    [TestCase("Coal Mining", "Magma Fields")]
    [TestCase("Obsidian Mining", "The End")]
    [TestCase("Crimson Fishing", "Stronghold")]
    [TestCase("T4 Voidglooms", "Void Sepulture")]
    [TestCase("Helix Foraging", "Torrhus Canyon")]
    [TestCase("Winter Fishing", "Hot Springs")]
    [TestCase("Voracious Spider", "Arachne's Burrow")]
    [TestCase("Diamond Mining", "Royal Mines")]
    [TestCase("Red Mushroom", "Glowing Mushroom Cave")]
    [TestCase("Water Fishing", "Village")]
    [TestCase("Shellwise", "Murkwater Depths")]
    [TestCase("Joydive", "Murkwater Depths")]
    [TestCase("Galatea Fishing", "Murkwater Depths")]
    [TestCase("Mochibear", "Tranquility Sanctum")]
    public void NewWhere_IsDetectedAsTheTasksLocation_AndResolvesToAnIsland(string taskName, string where)
    {
        var task = Get(taskName);
        SkyblockZones.Matches(task.GetDetectionSignature().Locations.ToList(), where).Should().BeTrue();
        task.IslandForTest.Should().NotBeNull();
    }

    [Test]
    public void ObsidianMining_DoesNotSendPlayersToTheObsidianSanctuary()
    {
        Text(Get("Obsidian Mining")).Should().NotContain("Obsidian Sanctuary");
    }
}
