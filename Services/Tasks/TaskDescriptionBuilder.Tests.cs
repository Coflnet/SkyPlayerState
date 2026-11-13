using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Tasks;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tests;

public class TaskDescriptionBuilderTests
{
    [Test]
    public void NameOnlyWhenNothingElseKnown()
    {
        TaskDescriptionBuilder.Build("Mining Coal", null, null, [])
            .Should().Be("Mining Coal.");
    }

    [Test]
    public void ListsPlaceAndIsland()
    {
        TaskDescriptionBuilder.Build("Mining Coal", "Coal Mine", "Hub", [])
            .Should().Be("Mining Coal at Coal Mine, Hub.");
    }

    [Test]
    public void DoesNotRepeatAPlaceThatIsTheTaskNameIgnoringBrackets()
    {
        TaskDescriptionBuilder.Build("Torrhus Canyon (Hunting)", "Torrhus Canyon", "Torrhus", [])
            .Should().Be("Torrhus Canyon (Hunting).");
        TaskDescriptionBuilder.Build("Lotus Atoll", "Lotus Atoll", "Lotus Atoll", [])
            .Should().Be("Lotus Atoll.");
        TaskDescriptionBuilder.Build("Spirit Cave (Hunting)", "Spirit Cave", "The Park", [])
            .Should().Be("Spirit Cave (Hunting), The Park.");
    }

    [Test]
    public void FallsBackToTheCategoryWhenNothingElseIsKnown()
    {
        TaskDescriptionBuilder.Build("Hunting Trap", null, null, [], "Hunting")
            .Should().Be("Hunting Trap, a hunting task.");
    }

    [Test]
    public void StripsTierTagsFromThePlace()
    {
        TaskDescriptionBuilder.Build("Kuudra T1", "Kuudra's Hollow (T1)", "Kuudra's Hollow", [])
            .Should().Be("Kuudra T1 at Kuudra's Hollow.");
        TaskDescriptionBuilder.Build("F3", "The Catacombs (F3)", "Dungeon Hub", [])
            .Should().Be("F3 at The Catacombs, Dungeon Hub.");
    }

    [Test]
    public void SplitsHardAndRecommendedRequirements()
    {
        var requirements = new[]
        {
            TaskRequirement.Skill("mining", 12),
            TaskRequirement.HeartOfTheMountain(3),
            TaskRequirement.CatacombsLevel(20, hard: false)
        };
        TaskDescriptionBuilder.Build("Mining Mithril", null, null, requirements)
            .Should().Be("Mining Mithril. Requires: Mining 12, Heart of the Mountain 3. Recommended: Catacombs 20.");
    }

    [Test]
    public void GeneratedDescriptionsAreInformativeForRegisteredTasks()
    {
        var descriptions = new TaskRegistry().MethodTasks.Select(t => (name: t.GetDetectionSignature().MethodName, text: t.Description)).ToList();
        descriptions.Should().NotContain(d => d.text.StartsWith("Calculates profit for"));
        descriptions.Should().NotContain(d => d.text == d.name + ".", "a bare name tells nothing");
        descriptions.Should().NotContain(d => d.text.Contains("Requires Kill"));
        descriptions.Should().NotContain(d => d.text.Contains("Main drops"));
    }

    [Test]
    public void HandWrittenDescriptionOverrideIsKept()
    {
        new BazaarPurchaseTask().Description.Should().StartWith("Hidden accounting task");
    }

    [Test]
    public void EstimatesCarryOnlyPublicRequirementFields()
    {
        var info = TaskRequirementInfo.From(new TaskRequirement
        {
            Kind = RequirementKinds.SkillLevel, Key = "mining", Target = 12, Label = "Mining 12", Hard = true,
            Met = true, Current = 40, CurrentText = "40"
        });
        var json = JsonSerializer.Serialize(info);
        json.Should().Contain("\"Label\":\"Mining 12\"").And.Contain("\"Kind\":\"skill_level\"")
            .And.NotContain("Met").And.NotContain("Current");
    }
}
