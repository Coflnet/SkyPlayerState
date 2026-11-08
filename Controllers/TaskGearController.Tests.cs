using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services.Gear;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tasks.Gear;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Controllers;

public class TaskGearControllerTests
{
    private static readonly TaskRegistry Registry = new();

    private static GearSetupService MakeService(GearProfileCatalog? catalog = null)
    {
        var session = new Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        var estimator = new TaskEstimator(Registry, aggregates,
            new StatScoreService(null!, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, Registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance),
            NullLogger<TaskEstimator>.Instance);
        var prices = new TaskPriceService(new Mock<ISniperApi>().Object, new Mock<IBazaarApi>().Object,
            NullLogger<TaskPriceService>.Instance);
        return catalog == null
            ? new GearSetupService(estimator, aggregates, prices, Registry, NullLogger<GearSetupService>.Instance)
            : new GearSetupService(estimator, aggregates, prices, Registry, catalog, NullLogger<GearSetupService>.Instance);
    }

    private static TaskGearController Controller(GearSetupService setups, InMemoryGearReader? reader = null)
        => new(setups, Clusters(setups, reader));

    private static GearClusterService Clusters(GearSetupService setups, InMemoryGearReader? reader = null)
        => new(reader ?? new InMemoryGearReader(), Registry, setups, NullLogger<GearClusterService>.Instance);

    [Test]
    public async Task Service_EstimatesEveryBoundTask()
    {
        var all = await MakeService(GearFixtures.All()).GetAll();

        all.Select(s => s.TaskName).Should().BeEquivalentTo(GearFixtures.All().Bindings.Select(b => b.Binding.Task));
        all.Should().OnlyContain(s => s.Setups.Count == 3 && s.Baseline.Id == GearTiers.Baseline);
    }

    [Test]
    public async Task Service_RecomputesOnlyWhenTheCommunityEstimatesRefresh()
    {
        var service = MakeService(GearFixtures.All());

        var first = await service.GetAll();
        var second = await service.GetAll();

        second.Should().BeSameAs(first, "the community estimates are cached for a minute and so is the result");
    }

    [Test]
    public void Service_UsesTheMergedAggregateForRevenueShares()
    {
        var service = MakeService(GearFixtures.All());
        var snapshot = new Dictionary<(string, byte), BucketAggregate>
        {
            [("Wheat Farming", 1)] = new() { WSeconds = 3600, ItemCounts = new() { ["WHEAT"] = 100 }, Contributors = ["a"] },
            [("Wheat Farming", 3)] = new() { WSeconds = 3600, ItemCounts = new() { ["SYNTHETIC_RARE_DROP"] = 1 }, Contributors = ["b"] }
        };
        var community = new List<TaskEstimate> { new() { TaskName = "Wheat Farming", CoinsPerHour = 1000, Contributors = 3, Source = "global" } };
        var prices = new Dictionary<string, double> { ["WHEAT"] = 1, ["SYNTHETIC_RARE_DROP"] = 100 };

        var wheat = service.Compute(community, snapshot, prices).Single(s => s.TaskName == "Wheat Farming");

        // half the revenue is the rare drop: endgame = 0.5 * 3/2 (fortune) + 0.5 * 1.2 (magic find)
        wheat.Setups.Single(s => s.Id == "endgame").Multiplier!.Value.Should().BeApproximately(0.5 * 1.5 + 0.5 * 1.2, 1e-9);
        wheat.Setups.Single(s => s.Id == "endgame").Confidence.Should().Be("high");
    }

    /// <summary>Regression: the response echoed the binding's spelling instead of the task's canonical name.</summary>
    [Test]
    public async Task Service_ReportsTheCanonicalTaskName()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fixed);
        profile["bindings"]![0]!["task"] = "dIaNa";
        var service = MakeService(GearFixtures.Catalog(profile.ToJsonString()));

        (await service.GetAll()).Single().TaskName.Should().Be("Diana");
        (await service.Get("diana"))!.TaskName.Should().Be("Diana");
    }

    [Test]
    public async Task Service_WithTheShippedCatalog_ListsOneEntryPerShippedBinding()
    {
        (await MakeService().GetAll()).Should().HaveCount(GearProfileCatalog.Embedded.Bindings.Count());
    }

    [Test]
    public async Task Get_UnknownTask_Is404TaskNotFound()
    {
        var result = await Controller(MakeService(GearFixtures.All())).Get("Not A Task", CancellationToken.None);

        Slug(result).Should().Be("task_not_found");
    }

    [Test]
    public async Task Get_HiddenTask_Is404TaskNotFound()
    {
        var result = await Controller(MakeService(GearFixtures.All())).Get("Bazaar Purchases", CancellationToken.None);

        Slug(result).Should().Be("task_not_found");
    }

    [Test]
    public async Task Get_TaskWithoutProfile_Is404GearSetupsNotFound()
    {
        var result = await Controller(MakeService(GearFixtures.All())).Get("Oak Foraging", CancellationToken.None);

        Slug(result).Should().Be("gear_setups_not_found");
    }

    [Test]
    public async Task Get_BoundTask_ReturnsItsSetups()
    {
        var result = await Controller(MakeService(GearFixtures.All())).Get("f7", CancellationToken.None);

        result.Value!.TaskName.Should().Be("F7");
        result.Value.ProfileId.Should().Be("test-runtime");
    }

    [Test]
    public void GearSetupsRoute_OutranksThePlayerIdRoutes()
    {
        string Template(System.Type controller, string action) => controller.GetMethod(action)!
            .GetCustomAttribute<HttpGetAttribute>()!.Template ?? "";
        var gearAll = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/gear-setups"));
        var gearOne = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/gear-setups/" + Template(typeof(TaskGearController), nameof(TaskGearController.Get))));
        var player = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(typeof(TaskController), nameof(TaskController.GetEstimates))));
        var playerResult = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(typeof(TaskController), nameof(TaskController.GetResults))));

        gearAll.Should().BeLessThan(player, "a lower precedence value is matched first");
        gearOne.Should().BeLessThan(playerResult);
        typeof(TaskGearController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("Task/gear-setups");
    }

    [Test]
    public async Task GetClusters_UnknownOrHiddenTask_Is404TaskNotFound()
    {
        var controller = Controller(MakeService(GearFixtures.All()));

        Slug(await controller.GetClusters("Not A Task", CancellationToken.None)).Should().Be("task_not_found");
        Slug(await controller.GetClusters("Bazaar Purchases", CancellationToken.None)).Should().Be("task_not_found");
    }

    [Test]
    public async Task GetClusters_NothingMeasured_ReturnsTheLabelledEstimatesAsFallback()
    {
        var result = (await Controller(MakeService(GearFixtures.All())).GetClusters("wheat farming", CancellationToken.None)).Value!;

        result.TaskName.Should().Be("Wheat Farming");
        result.Category.Should().Be(GearModifierTable.Farming);
        result.Clusters.Should().BeEmpty();
        result.ComputedAt.Should().BeNull("no derivation ran yet");
        result.Estimates.Should().NotBeNull();
        result.Estimates!.Baseline.Basis.Should().Be(GearSetupBasis.Estimate);
        result.Estimates.Setups.Should().NotBeEmpty().And.OnlyContain(s => s.Basis == GearSetupBasis.Estimate);
    }

    [Test]
    public async Task GetClusters_Measured_ResponseShape()
    {
        var setups = MakeService(GearFixtures.All());
        var reader = new InMemoryGearReader();
        var gear = GearClusterTestData.TestGear();
        var other = GearClusterTestData.TestGear();
        other.Slots["tool"].Stats["Farming Fortune"] = 256;
        other.Slots["tool"].HotPotatoCount = 10; // lore stats are not part of the fingerprint, the potato books are
        reader.Add(GearClusterTestData.Obs(1, gear, 1, 10_000), GearClusterTestData.Obs(2, other, 1, 14_000));
        var clusters = Clusters(setups, reader);
        await clusters.Refresh("Wheat Farming", GearClusterTestData.Now);

        var result = (await new TaskGearController(setups, clusters).GetClusters("Wheat Farming", CancellationToken.None)).Value!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        json["taskName"]!.GetValue<string>().Should().Be("Wheat Farming");
        json["computedAt"]!.GetValue<System.DateTime>().Should().Be(GearClusterTestData.Now);
        json["windowWeeks"]!.GetValue<int>().Should().Be(13);
        json["minPlayers"]!.GetValue<int>().Should().Be(2);
        json["minHours"]!.GetValue<double>().Should().Be(1);
        var all = json["clusters"]!.AsArray();
        all.Select(c => c!["level"]!.GetValue<int>()).Should().BeEquivalentTo([1, 2, 3], "nested levels of the same two players");
        var cluster = all.Single(c => c!["level"]!.GetValue<int>() == 3)!;
        cluster["basis"]!.GetValue<string>().Should().Be("measured");
        cluster["players"]!.GetValue<int>().Should().Be(2);
        cluster["coinsPerHour"]!.GetValue<double>().Should().Be(12_000);
        cluster["rounded"]!.GetValue<bool>().Should().BeTrue();
        cluster["commonP25"].Should().BeNull();
        cluster["observations"]!.GetValue<int>().Should().Be(2);
        cluster["observationsLabel"]!.GetValue<string>().Should().Be("2+");
        cluster["hours"]!.GetValue<double>().Should().Be(2);
        cluster["parentKey"]!.GetValue<string>().Should().StartWith("L2|");
        cluster["includesChildren"]!.GetValue<bool>().Should().BeFalse();
        all.Single(c => c!["level"]!.GetValue<int>() == 1)!["includesChildren"]!.GetValue<bool>().Should().BeTrue();
        cluster["slots"]!["tool"]!["tag"]!.GetValue<string>().Should().Be("THEORETICAL_HOE_WHEAT_3");
        // regression: one player's exact lore stat was published; now the median of both players, rounded (175, 256 -> 215.5)
        cluster["slots"]!["tool"]!["stats"]!["Farming Fortune"]!.GetValue<double>().Should().Be(220);
        cluster["pet"]!["type"]!.GetValue<string>().Should().Be("ELEPHANT");
        json["modifierEffects"]!.AsArray().Should().BeEmpty();
        json["estimates"]!["setups"]![0]!["basis"]!.GetValue<string>().Should().Be("estimate");
    }

    /// <summary>Regression: the empty response before the first derivation was cached for 5 minutes.</summary>
    [Test]
    public async Task GetClusters_BeforeTheFirstDerivation_IsNotCacheable()
    {
        var setups = MakeService(GearFixtures.All());
        var reader = new InMemoryGearReader();
        var clusters = Clusters(setups, reader);
        TaskGearController Controller() => new(setups, clusters)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        var before = Controller();
        await before.GetClusters("Wheat Farming", CancellationToken.None);
        before.Response.Headers.CacheControl.ToString().Should().Be("no-cache");

        await clusters.Refresh("Wheat Farming", GearClusterTestData.Now);
        var after = Controller();
        await after.GetClusters("Wheat Farming", CancellationToken.None);
        after.Response.Headers.CacheControl.ToString().Should().NotContain("no-cache");
    }

    [Test]
    public void GearClustersRoute_IsAbsolute_AndOutranksThePlayerIdRoutes()
    {
        var template = typeof(TaskGearController).GetMethod(nameof(TaskGearController.GetClusters))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;
        string Template(System.Type controller, string action) => controller.GetMethod(action)!
            .GetCustomAttribute<HttpGetAttribute>()!.Template ?? "";
        var clusters = RoutePrecedence.ComputeInbound(TemplateParser.Parse(template.TrimStart('/')));
        var playerResult = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(typeof(TaskController), nameof(TaskController.GetResults))));

        template.Should().Be("/Task/gear-clusters/{taskName}");
        clusters.Should().BeLessThan(playerResult);
    }

    private static string? Slug<T>(ActionResult<T> result) =>
        ((result.Result as NotFoundObjectResult)?.Value as ErrorResponse)?.Slug;
}
