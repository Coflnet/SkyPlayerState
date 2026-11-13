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
        => new(setups, Models(setups, reader));

    private static GearModelService Models(GearSetupService setups, InMemoryGearReader? reader = null, GearModelOptions? options = null)
        => new(reader ?? new InMemoryGearReader(), Registry, setups, NullLogger<GearModelService>.Instance, options ?? GearModelOptions.Default);

    /// <summary>
    /// Five players give every part of the model under the rules before 2026-11-09 (late game cohort of 2 players and
    /// 1 h without shrinkage, curve from 5 players): the response shape test keeps them so it can stay this small.
    /// </summary>
    private static readonly GearModelOptions FivePlayerModel = GearModelOptions.Default with
    {
        LateGameMinPlayers = 2, LateGameMinHours = 1, LateGamePriorHours = 0, CurveMinPlayers = 5
    };

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
    public async Task GetModel_UnknownOrHiddenTask_Is404TaskNotFound()
    {
        var controller = Controller(MakeService(GearFixtures.All()));

        Slug(await controller.GetModel("Not A Task", CancellationToken.None)).Should().Be("task_not_found");
        Slug(await controller.GetModel("Bazaar Purchases", CancellationToken.None)).Should().Be("task_not_found");
    }

    [Test]
    public async Task GetModel_NothingMeasured_ReturnsTheFormulaAndTheLabelledEstimatesAsFallback()
    {
        var result = (await Controller(MakeService(GearFixtures.All())).GetModel("wheat farming", CancellationToken.None)).Value!;

        result.TaskName.Should().Be("Wheat Farming");
        result.Category.Should().Be(GearModifierTable.Farming);
        result.ComputedAt.Should().BeNull("no derivation ran yet");
        result.Distribution.Should().BeNull();
        result.LateGame.Should().BeNull();
        result.Curve.Should().BeNull();
        result.Pieces.Should().BeEmpty();
        result.RecommendedEndgame.Should().BeNull();
        result.ScoreFormula.Weights.Should().ContainKeys("Farming Fortune", "Wheat Fortune");
        result.Estimates.Should().NotBeNull();
        result.Estimates!.Baseline.Basis.Should().Be(GearSetupBasis.Estimate);
        result.Estimates.Setups.Should().NotBeEmpty().And.OnlyContain(s => s.Basis == GearSetupBasis.Estimate);
    }

    [Test]
    public async Task GetModel_Measured_ResponseShape()
    {
        var setups = MakeService(GearFixtures.All());
        var reader = new InMemoryGearReader();
        // five players, tool Farming Fortune 150 .. 250 (other harvesting levels, so other fingerprints: lore stats are
        // not part of a fingerprint); Fermento set (+75) and a level 100 Elephant (+150) on top
        var rates = new[] { 10_000, 11_000, 12_000, 13_000, 15_000 };
        reader.Add(rates.Select((rate, i) => GearModelTestData.Obs(i + 1, GearModelTestData.TestGear(harvesting: i + 1, toolFortune: 150 + 25 * i), 1, rate)).ToArray());
        var models = Models(setups, reader, FivePlayerModel);
        await models.Refresh("Wheat Farming", GearModelTestData.Now);

        var result = (await new TaskGearController(setups, models).GetModel("Wheat Farming", CancellationToken.None)).Value!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        json["taskName"]!.GetValue<string>().Should().Be("Wheat Farming");
        json["computedAt"]!.GetValue<System.DateTime>().Should().Be(GearModelTestData.Now);
        json["windowWeeks"]!.GetValue<int>().Should().Be(13);
        json["minPlayers"]!.GetValue<int>().Should().Be(2);
        json["scoreFormula"]!["kind"]!.GetValue<string>().Should().Be("weighted-sum");
        var distribution = json["distribution"]!;
        distribution["players"]!.GetValue<int>().Should().Be(5);
        distribution["scoreP50"]!.GetValue<double>().Should().Be(420, "tool 200 + Fermento 75 + Elephant 150 = 425, 2 significant digits");
        distribution["rounded"]!.GetValue<bool>().Should().BeFalse();
        var lateGame = json["lateGame"]!;
        lateGame["widened"]!.GetValue<bool>().Should().BeTrue("the P90 holds one player, widened to the next build");
        lateGame["playersLabel"]!.GetValue<string>().Should().Be("2+");
        lateGame["coinsPerHour"]!.GetValue<double>().Should().Be(14_000, "the median of the top two players' rates");
        lateGame["commonP25"].Should().BeNull();
        lateGame["confidence"]!.GetValue<string>().Should().Be("low");
        var curve = json["curve"]!;
        curve["players"]!.GetValue<int>().Should().Be(5);
        curve["points"]!.AsArray().Select(p => p!["scorePercentile"]!.GetValue<int>()).Should().Equal(10, 25, 50, 75, 90);
        curve["slopePer10Pct"]!.GetValue<double>().Should().BePositive();
        var tool = json["pieces"]!.AsArray().Single(p => p!["slot"]!.GetValue<string>() == "tool")!;
        tool["item"]!["tag"]!.GetValue<string>().Should().Be("THEORETICAL_HOE_WHEAT_3");
        tool["item"]!["stats"]!["Farming Fortune"]!.GetValue<double>().Should().Be(225, "the highest value two players reach");
        tool["item"]!["enchants"]!["harvesting"]!.GetValue<int>().Should().Be(4, "the highest level two players reach");
        tool["players"]!.GetValue<int>().Should().Be(5);
        var helmet = json["pieces"]!.AsArray().Single(p => p!["slot"]!.GetValue<string>() == "helmet")!;
        helmet["effects"]!.AsArray().Single()!["id"]!.GetValue<string>().Should().Be("set-fermento");
        var endgame = json["recommendedEndgame"]!;
        endgame["basis"]!.GetValue<string>().Should().Be("cohort", "a late game cohort exists");
        endgame["coinsPerHour"]!.GetValue<double>().Should().Be(lateGame["coinsPerHour"]!.GetValue<double>());
        endgame["score"]!.GetValue<double>().Should().Be(450, "tool 225 + Fermento 75 + Elephant 150");
        endgame["extrapolated"]!.GetValue<bool>().Should().BeFalse();
        endgame["pieces"]!["necklace"].Should().BeNull("zero-score sightings are not income upgrades");
        endgame["pieces"]!["tool"]!.GetValue<string>().Should().Be(tool["id"]!.GetValue<string>());
        endgame["pieces"]!["helmet"]!.GetValue<string>().Should().Be(helmet["id"]!.GetValue<string>(), "the full Fermento set adds 75");
        endgame["effects"]!.AsArray().Select(e => e!.GetValue<string>()).Should().BeEquivalentTo(["set-fermento", "pet-elephant-trunk-efficiency"]);
        json["modifierEffects"]!.AsArray().Should().BeEmpty();
        json["estimates"]!["setups"]![0]!["basis"]!.GetValue<string>().Should().Be("estimate");
    }

    [Test]
    public async Task GetClusters_TheOldRoute_ReturnsTheSameModel()
    {
        var controller = Controller(MakeService(GearFixtures.All()));

#pragma warning disable CS0618 // the obsolete route is what this test covers
        var old = (await controller.GetClusters("Wheat Farming", CancellationToken.None)).Value!;
        Slug(await controller.GetClusters("Not A Task", CancellationToken.None)).Should().Be("task_not_found");
#pragma warning restore CS0618
        var current = (await controller.GetModel("Wheat Farming", CancellationToken.None)).Value!;

        old.Should().BeEquivalentTo(current);
    }

    /// <summary>Regression: the empty response before the first derivation was cached for 5 minutes.</summary>
    [Test]
    public async Task GetModel_BeforeTheFirstDerivation_IsNotCacheable()
    {
        var setups = MakeService(GearFixtures.All());
        var reader = new InMemoryGearReader();
        var models = Models(setups, reader);
        TaskGearController Controller() => new(setups, models)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        var before = Controller();
        await before.GetModel("Wheat Farming", CancellationToken.None);
        before.Response.Headers.CacheControl.ToString().Should().Be("no-cache");

        await models.Refresh("Wheat Farming", GearModelTestData.Now);
        var after = Controller();
        await after.GetModel("Wheat Farming", CancellationToken.None);
        after.Response.Headers.CacheControl.ToString().Should().NotContain("no-cache");
    }

    [TestCase(nameof(TaskGearController.GetModel), "/Task/gear-model/{taskName}")]
    [TestCase(nameof(TaskGearController.GetClusters), "/Task/gear-clusters/{taskName}")]
    public void GearModelRoutes_AreAbsolute_AndOutrankThePlayerIdRoutes(string action, string expected)
    {
        var template = typeof(TaskGearController).GetMethod(action)!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;
        string Template(System.Type controller, string name) => controller.GetMethod(name)!
            .GetCustomAttribute<HttpGetAttribute>()!.Template ?? "";
        var model = RoutePrecedence.ComputeInbound(TemplateParser.Parse(template.TrimStart('/')));
        var playerResult = RoutePrecedence.ComputeInbound(TemplateParser.Parse("Task/" + Template(typeof(TaskController), nameof(TaskController.GetResults))));

        template.Should().Be(expected);
        model.Should().BeLessThan(playerResult);
    }

    private static string? Slug<T>(ActionResult<T> result) =>
        ((result.Result as NotFoundObjectResult)?.Value as ErrorResponse)?.Slug;
}
