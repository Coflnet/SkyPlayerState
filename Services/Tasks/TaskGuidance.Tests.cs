using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Guards the "helpful task list" data (WikiUrl/Where/WhereWikiUrl/Steps/Warp) that makes a task
/// actually followable: every task the default <c>/cofl task</c> list can show must have a real
/// (non-null, well-formed) wiki link, enough steps to actually be followed, and either a warp to
/// get close or a documented reason it doesn't. Covers every registered ProfitTask - not just
/// MethodTask - since IslandTask/IndividualSlayerTask/Kat/Forge/Composter/crafting tasks all
/// inherit the same guidance members from ProfitTask (see ProfitTask.cs).
/// </summary>
public class TaskGuidanceTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly Regex WikiUrlPattern = new(@"^https://hypixelskyblock\.minecraft\.wiki/w/\S+$");

    [Test]
    public void EveryRegisteredTask_HasAWikiUrl()
    {
        // Strict: every task the default task list can show must have SOME wiki link, not just a
        // well-formed one if present - MethodTask/IslandTask/IndividualSlayerTask all fall back to
        // their island's wiki page when no method-specific page is curated (see SkyblockZones),
        // so this only fails for a task with neither a curated WikiUrl nor a resolvable Where/Island.
        var violations = Registry.Tasks
            .Where(t => string.IsNullOrEmpty(t.WikiUrlForTest))
            .Select(t => t.Name)
            .ToList();
        violations.Should().BeEmpty("every task shown in /cofl task should have a wiki link - " +
            "give it a Where that resolves to a known island (SkyblockZones) or curate a WikiUrl directly");
    }

    [Test]
    public void WikiUrl_IsAlwaysAWellFormedCommunityWikiLink()
    {
        var violations = Registry.Tasks
            .Where(t => t.WikiUrlForTest != null && !WikiUrlPattern.IsMatch(t.WikiUrlForTest))
            .Select(t => $"{t.Name}: '{t.WikiUrlForTest}'")
            .ToList();
        violations.Should().BeEmpty("every emitted WikiUrl must be a well-formed hypixelskyblock.minecraft.wiki link");
    }

    [Test]
    public void WhereWikiUrl_WhenSet_IsWellFormedCommunityWikiLink()
    {
        var violations = Registry.Tasks
            .Where(t => t.WhereWikiUrlForTest != null && !WikiUrlPattern.IsMatch(t.WhereWikiUrlForTest))
            .Select(t => $"{t.Name}: '{t.WhereWikiUrlForTest}'")
            .ToList();
        violations.Should().BeEmpty("every emitted WhereWikiUrl must be a well-formed hypixelskyblock.minecraft.wiki link");
    }

    [Test]
    public void EveryTask_HasAtLeastThreeSteps()
    {
        var violations = Registry.Tasks
            .Where(t => t.StepsForTest.Count < 3)
            .Select(t => $"{t.Name}: {t.StepsForTest.Count} steps")
            .ToList();
        violations.Should().BeEmpty("every task should have at least 3 followable steps");
    }

    [Test]
    public void EveryStep_HasNonEmptyText()
    {
        var violations = Registry.Tasks
            .SelectMany(t => t.StepsForTest.Select(s => (task: t.Name, step: s)))
            .Where(x => string.IsNullOrWhiteSpace(x.step.Text))
            .Select(x => x.task)
            .ToList();
        violations.Should().BeEmpty("no step should render as blank text");
    }

    /// <summary>
    /// Tasks that intentionally have no warp: either they cover many far apart Locations (a single
    /// warp button would be misleading) or the activity is not tied to a place at all. Keep this
    /// list explicit and short - a task landing here unexpectedly should get its zone added to
    /// SkyblockZones instead of being added here.
    /// </summary>
    private static readonly string[] NoWarpIsExpected =
    [
        "HuntingTrap", // passive task: picks whichever of ~15 zones has the priciest shard right
                       // now (see HuntingTrapTask.Execute), so there is no single "Where"/warp
    ];

    [Test]
    public void EveryTask_HasAWarpOrIsExplicitlyExempt()
    {
        var missing = Registry.Tasks
            .Where(t => t.EffectiveWarpCommandForTest == null)
            .Select(t => t.Name)
            .Where(name => !NoWarpIsExpected.Contains(name))
            .ToList();
        missing.Should().BeEmpty("every task should resolve a warp via its own WarpCommand or its island's - " +
            "if a task legitimately has none, add its zone to SkyblockZones or add it to NoWarpIsExpected with a reason");
    }

    /// <summary>
    /// Regression: Oasis is a Farming Islands zone, not Crimson Isle (see SkyblockZones), but
    /// OasisFishingTask's hand-written Steps used to send players to "/warp isle" (Crimson Isle).
    /// </summary>
    [Test]
    public void OasisFishingTask_WarpIsNotCrimsonIsle()
    {
        var task = new OasisFishingTask();
        task.EffectiveWarpCommandForTest.Should().NotBe("/warp isle",
            "Oasis is on The Farming Islands, not the Crimson Isle");
        task.StepsForTest.Should().NotContain(s => s.OnClick == "/warp isle" || (s.Text != null && s.Text.Contains("/warp isle")),
            "no OasisFishingTask step should send the player to the Crimson Isle warp");
    }
}
