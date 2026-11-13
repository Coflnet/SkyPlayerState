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
