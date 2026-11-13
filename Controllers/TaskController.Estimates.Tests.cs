using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Controllers;

public class TaskControllerEstimatesTests
{
    private static TaskEstimator MakeEstimator(TaskRegistry registry, TaskAggregateService aggregates = null)
    {
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        return new TaskEstimator(registry,
            aggregates ?? new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance),
            new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance),
            NullLogger<TaskEstimator>.Instance);
    }

    // distinct price per formula drop so the estimates differ and the ordering is meaningful
    private static Dictionary<string, double> Prices(TaskRegistry registry) => registry.MethodTasks
        .SelectMany(t => t.FormulaDropsForTest).Select(d => d.ItemTag).Distinct()
        .Select((tag, i) => (tag, price: 100.0 + i * 37)).ToDictionary(x => x.tag, x => x.price);

    private static async Task<List<TaskEstimate>> Estimate()
    {
        var registry = new TaskRegistry();
        return await MakeEstimator(registry).GetCommunityEstimates(Prices(registry));
    }

    [Test]
    public async Task CommunityEstimates_HaveNoPlayerData()
    {
        var estimates = await Estimate();

        estimates.Should().NotBeEmpty();
        estimates.Should().OnlyContain(e => e.StatBucket == StatScoreService.UnknownBucket
            && e.Source != "personal" && e.PersonalTrackedMinutes == 0 && e.TraceId == null);
    }

    [Test]
    public async Task CommunityEstimates_AreSortedByCoinsPerHourDescending()
    {
        var estimates = await Estimate();

        estimates.Select(e => e.CoinsPerHour).Should().BeInDescendingOrder();
        estimates.First().CoinsPerHour.Should().BeGreaterThan(estimates.Last().CoinsPerHour);
    }

    [Test]
    public async Task CommunityEstimates_ExcludeHiddenTasks_AndCarryDescriptionAndType()
    {
        var registry = new TaskRegistry();
        var estimates = await MakeEstimator(registry).GetCommunityEstimates(Prices(registry));

        var names = estimates.Select(e => e.TaskName).ToList();
        names.Should().NotContain(["Bazaar Purchases", "Auction Purchases", "Minion Collection", "Crimson Isle Mobs"]);
        names.Should().BeEquivalentTo(registry.PublicMethodTasks.Select(t => t.GetDetectionSignature().MethodName));
        estimates.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Description));
        estimates.Single(e => e.TaskName == "Diana").Description.Should().Be(new DianaTask().Description);
        estimates.Select(e => e.Type).Distinct().Should().Contain(TaskType.Active);
    }

    [Test]
    public async Task CommunityEstimates_CarryTheTasksRequirements()
    {
        var registry = new TaskRegistry();
        var estimates = await MakeEstimator(registry).GetCommunityEstimates(Prices(registry));

        var withRequirements = estimates.Where(e => e.Requirements.Count > 0).ToList();
        withRequirements.Should().NotBeEmpty();
        var task = registry.PublicMethodTasks.First(t => t.AllRequirements.Count > 0);
        var estimate = estimates.Single(e => e.TaskName == task.GetDetectionSignature().MethodName);
        estimate.Requirements.Select(r => r.Label).Should().Equal(task.AllRequirements.Select(r => r.Label));
        estimate.Requirements.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Kind));
    }

    [Test]
    public async Task CommunityEstimates_AreCachedBetweenCalls()
    {
        var registry = new TaskRegistry();
        var estimator = MakeEstimator(registry);

        var first = await estimator.GetCommunityEstimates(Prices(registry));
        var second = await estimator.GetCommunityEstimates(new Dictionary<string, double>());

        second.Should().BeSameAs(first);
    }

    /// <summary>Regression: a cold pod's formula-only result must not be cached for the full minute.</summary>
    [Test]
    public async Task CommunityEstimates_AreOnlyBrieflyCached_WhileSnapshotIsCold()
    {
        var registry = new TaskRegistry();
        var cold = new TaskAggregateService(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance);
        var estimator = MakeEstimator(registry, cold);

        await estimator.GetCommunityEstimates(Prices(registry));

        cold.IsSnapshotWarm.Should().BeFalse();
        estimator.CommunityCacheTtl.Should().Be(TaskEstimator.CommunityColdCacheDuration);
    }

    [Test]
    public async Task CommunityEstimates_AreCachedForAMinute_OnceSnapshotIsWarm()
    {
        var registry = new TaskRegistry();
        var warm = new TaskAggregateService(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance);
        warm.SetSnapshotForTest(new());
        var estimator = MakeEstimator(registry, warm);

        await estimator.GetCommunityEstimates(Prices(registry));

        estimator.CommunityCacheTtl.Should().BeGreaterThan(TaskEstimator.CommunityColdCacheDuration);
    }

    [Test]
    public async Task CommunityEstimates_SerializeAsCamelCase()
    {
        var estimates = await Estimate();

        var json = JsonSerializer.Serialize(estimates.Take(1), new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = true });

        json.Should().Contain("\"taskName\"").And.Contain("\"coinsPerHour\"").And.Contain("\"description\"").And.Contain("\"type\"");
        TestContext.Out.WriteLine(json);
    }

    /// <summary>/Task/estimates must be matched by its own action, not captured by /Task/{playerId}.</summary>
    [Test]
    public void EstimatesRoute_OutranksThePlayerIdRoute()
    {
        string Template(string action) => typeof(TaskController).GetMethod(action)!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;
        var literal = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(nameof(TaskController.GetCommunityEstimates))));
        var player = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(nameof(TaskController.GetEstimates))));

        literal.Should().BeLessThan(player, "a lower precedence value is matched first");
    }
}
