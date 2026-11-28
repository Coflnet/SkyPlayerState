using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class AreaRequirementsTests
{
    private static List<string> Labels(params string[] zones) =>
        AreaRequirements.ForZones(zones).Select(r => r.Label).ToList();

    private static List<string> AreaZonesOf(ProfitTask task) =>
        ((IEnumerable<string>)typeof(ProfitTask).GetProperty("AreaZones", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(task)).ToList();

    [Test]
    public void IslandNamesDeclaredAsLocationsResolveToTheirIsland()
    {
        Labels("Moonglade").Should().Equal("Foraging 12");
        Labels("Torrhus").Should().Equal("Foraging 12", "Heart of the Forest 4");
        Labels("Garden").Should().Equal("SkyBlock level 5");
        SkyblockZones.IslandOfLocation("Torrhus").Should().Be("Torrhus");
    }

    [Test]
    public void DetectionLookupStillTreatsIslandNamesAsUnknownZones()
    {
        // the classifier's tab-island fallback relies on IslandOf returning null for non-zones
        SkyblockZones.IslandOf("Moonglade").Should().BeNull();
        SkyblockZones.IslandOfLocation("Moonglade").Should().Be("Moonglade");
    }

    [Test]
    public void TheBaseCampIsGatedLikeTheGlaciteTunnels()
    {
        Labels("Glacite Tunnels", "Great Glacite Lake", "Dwarven Base Camp")
            .Should().Equal("Mining 12", "Heart of the Mountain 7");
        Labels("Dwarven Base Camp").Should().Equal("Mining 12", "Heart of the Mountain 7");
        Labels("Fossil Research Center", "Dwarven Base Camp").Should().Equal("Mining 12", "Heart of the Mountain 7");
    }

    [Test]
    public void GlaciteAndFossilTasksNeedHeartOfTheMountain7()
    {
        var tasks = new TaskRegistry().PublicMethodTasks.ToDictionary(t => t.GetDetectionSignature().MethodName);
        foreach (var name in new[] { "Fossil Excavation", "Tungsten Mining", "Umber Mining", "Aquamarine Mining", "Peridot Mining" })
            tasks[name].AllRequirements.Select(r => r.Label).Should().Contain("Heart of the Mountain 7", name);
    }

    /// <summary>
    /// Regression (2026-11-28): Coal Mining is shown at Magma Fields (Crystal Hollows) but listed no requirements, because its
    /// Hub zones (Coal Mine, Gold Mine) need nothing and only what every zone needs applied. The shown spot's requirements are
    /// recommended now; nothing blocks the task, it is still doable in the Hub.
    /// </summary>
    [TestCase("Coal Mining")]
    [TestCase("Gold Mining")]
    public void ATaskShownInTheCrystalHollows_RecommendsItsRequirements(string name)
    {
        var task = Public(name);

        task.AllRequirements.Where(r => !r.Hard).Select(r => r.Label).Should().Equal("Mining 12", "Heart of the Mountain 3");
        task.AllRequirements.Should().NotContain(r => r.Hard, "the Hub mines are open to everyone");
        task.Description.Should().EndWith("Recommended: Mining 12, Heart of the Mountain 3.");
    }

    [Test]
    public void ASpotRequirementTheZonesAlreadyNeedIsNotRepeated()
    {
        var fossil = Public("Fossil Excavation");

        fossil.AllRequirements.Select(r => (r.Label, r.Hard)).Should().Equal(("Mining 12", true), ("Heart of the Mountain 7", true));
    }

    [Test]
    public void EveryPublicTaskListsWhatItsShownSpotNeeds()
    {
        var missing = new TaskRegistry().PublicMethodTasks
            .Where(t => !string.IsNullOrEmpty(WhereOf(t)))
            .SelectMany(t => AreaRequirements.ForZone(WhereOf(t))
                .Where(spot => !t.AllRequirements.Any(r => r.Kind == spot.Kind && r.Key == spot.Key && r.Target >= spot.Target))
                .Select(spot => $"{t.GetDetectionSignature().MethodName}: {spot.Label}"))
            .ToList();
        missing.Should().BeEmpty();
    }

    private static MethodTask Public(string name)
        => new TaskRegistry().PublicMethodTasks.Single(t => t.GetDetectionSignature().MethodName == name);

    private static string WhereOf(ProfitTask task) =>
        (string)typeof(ProfitTask).GetProperty("Where", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(task);

    [Test]
    public void IslandsWithAnOddShortNameGetADisplayName()
    {
        SkyblockZones.IslandDisplayName("Jerry").Should().Be("Jerry's Workshop");
        SkyblockZones.IslandDisplayName("Hub").Should().Be("Hub");
        SkyblockZones.IslandDisplayName(null).Should().BeNull();
    }

    [Test]
    public void EveryZoneOfAPublicTaskResolvesToAnIsland()
    {
        var unresolved = new TaskRegistry().PublicMethodTasks
            .SelectMany(t => AreaZonesOf(t).Where(z => SkyblockZones.IslandOfLocation(z) == null && !SkyblockZones.AmbiguousZones.Contains(SkyblockZones.Canonical(z) ?? z) && z != "Private Island")
                .Select(z => $"{t.GetDetectionSignature().MethodName}: {z}"))
            .ToList();
        unresolved.Should().BeEmpty();
    }

    [Test]
    public void TasksInTheSameSingleIslandAllGetThatIslandsRequirements()
    {
        var registry = new TaskRegistry();
        var byIsland = registry.PublicMethodTasks
            .Select(t => (task: t, islands: AreaZonesOf(t).Select(SkyblockZones.IslandOfLocation).Distinct().ToList()))
            .Where(x => x.islands.Count == 1 && x.islands[0] != null)
            .GroupBy(x => x.islands[0]);
        foreach (var group in byIsland)
        {
            var islandRequirements = AreaRequirements.ForZone(group.Key).Select(r => r.Label).ToList();
            if (islandRequirements.Count == 0)
                continue;
            foreach (var (task, _) in group)
                task.AllRequirements.Select(r => r.Label).Should()
                    .Contain(islandRequirements, $"{task.GetDetectionSignature().MethodName} on {group.Key}");
        }
    }
}
