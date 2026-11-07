using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>"Slayer" means Maddox slayer quests only, and one activity must not show up twice in the results.</summary>
public class SlayerClassificationTests
{
    private static TaskParams EmptyParams() => new()
    {
        TestTime = new System.DateTime(2025, 7, 24, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<System.Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = new Dictionary<string, Period[]>(),
        CleanPrices = new Dictionary<string, long>(),
        BazaarPrices = [],
        Names = new Dictionary<string, string>()
    };

    [Test]
    public void AshfangTask_IsNotRegistered_SinceItOverlapsBlazeSlayer()
    {
        new TaskRegistry().Tasks.Select(t => t.GetType()).Should().NotContain(typeof(AshfangTask));
        TaskCatalog.IntentionallyUnregistered.Should().Contain(typeof(AshfangTask));
    }

    [Test]
    public void BarbarianDukeX_IsCombat_NotSlayer()
    {
        new BarbarianDukeXTask().GetDetectionSignature().Category.Should().Be("Combat");
    }

    [Test]
    public async Task Ashfang_BreakdownIsCombat_NotSlayer()
    {
        var result = await new AshfangTask().Execute(EmptyParams());

        result.Breakdown.Category.Should().Be("Combat");
    }

    [Test]
    public void RegisteredSlayerCategory_IsOnlyMaddoxSlayerQuests()
    {
        var slayerNames = new TaskRegistry().MethodTasks
            .Where(t => t.GetDetectionSignature().Category == "Slayer")
            .Select(t => t.GetType().Name);

        slayerNames.Should().NotContain(["BarbarianDukeXTask", "AshfangTask"]);
        slayerNames.Should().Contain(["BlazeSlayerTask", "TarantulaSlayerTask", "RevenantSlayerTask"]);
    }

    /// <summary>
    /// Regression: the profit-level tier trackers counted every period in their zones, so a Blaze or Tarantula
    /// slayer session was listed twice in /Task/{player}/results (once here, once as the MethodTask).
    /// </summary>
    [Test]
    public void SupersededProfitLevelSlayerTasks_AreNotRegistered()
    {
        var registered = new TaskRegistry().Tasks.Select(t => t.GetType()).ToHashSet();

        registered.Should().NotContain(
            [typeof(T3InfernoDemonlordTask), typeof(T4InfernoDemonlordTask), typeof(T4TarantulaTask), typeof(T5TarantulaTask)]);
        TaskCatalog.IntentionallyUnregistered.Should().Contain(
            [typeof(T3InfernoDemonlordTask), typeof(T4InfernoDemonlordTask), typeof(T4TarantulaTask), typeof(T5TarantulaTask)]);
    }

    [Test]
    public void NoRegisteredIndividualSlayerTask_OverlapsAMethodTaskLocation()
    {
        var registry = new TaskRegistry();
        var methodZones = registry.MethodTasks.Where(t => !t.IsHidden).ToList();

        foreach (var slayer in registry.Tasks.OfType<IndividualSlayerTask>())
            foreach (var zone in slayer.LocationNamesForTest)
                methodZones.Where(m => m.GetDetectionSignature().Category == "Slayer" && SkyblockZones.Matches(m.GetDetectionSignature().Locations, zone))
                    .Should().BeEmpty($"{slayer.GetType().Name} would duplicate a slayer MethodTask in {zone}");
    }
}
