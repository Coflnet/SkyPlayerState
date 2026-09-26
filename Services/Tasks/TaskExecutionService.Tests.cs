using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Covers TaskExecutionService's own execution loop (run every registered task, sort
/// accessible-first then by profit, isolate a single task by name) against a hand-built
/// <see cref="TaskParams"/> - the upstream parameter building (persistence/prices/estimator/...)
/// is exercised indirectly through the higher level TaskController.GetResults/GetResult endpoints
/// and is not re-mocked here, since none of it is touched by the (playerId-less) TaskParams
/// overloads under test. <see cref="TaskRegistry"/> has no external dependencies of its own, so a
/// real instance (with the full production task catalog) is used rather than a mock.
/// </summary>
public class TaskExecutionServiceTests
{
    private static TaskExecutionService MakeService(TaskRegistry registry = null)
    {
        // Only `registry` (and `logger`, unused by the overloads under test) are ever read by
        // ExecuteAll(TaskParams)/ExecuteOne(TaskParams, string) - every other dependency here only
        // matters for BuildParameters(playerId, ...), which these tests never call.
        return new TaskExecutionService(null, null, null, null, null, registry ?? new TaskRegistry(), null, null, null);
    }

    private static TaskParams MakeParams(params Period[] periods)
    {
        return new TaskParams
        {
            TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            MaxAvailableCoins = 1_000_000_000,
            LocationProfit = periods.GroupBy(l => l.Location).ToDictionary(l => l.Key, l => l.ToArray()),
            CleanPrices = new Dictionary<string, long>(),
            BazaarPrices = [],
            Names = new Dictionary<string, string>()
        };
    }

    [Test]
    public async Task ExecuteOne_ReturnsGuidance_StepsWarpAndWiki()
    {
        // Tracked Inferno Demonlord activity (SHARD_BURNINGSOUL at the Smoldering Tomb) drives
        // ComputeFromPlayerData, which populates the guidance fields (Where/Island/WikiUrl/Warp/
        // Steps) on every result. BurningsoulTask (registered under MethodName "Inferno Demonlord")
        // is one of the 124 pinned task names - see TaskCatalog.Registration.Tests.cs.
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        var period = new Period
        {
            PlayerUuid = "test-player",
            Server = "m1",
            Location = "Smoldering Tomb",
            Profit = 400_000,
            StartTime = start,
            EndTime = start.AddMinutes(5),
            ItemsCollected = new() { { "SHARD_BURNINGSOUL", 30 } }
        };

        var service = MakeService();
        var result = await service.ExecuteOne(MakeParams(period), "Inferno Demonlord");

        result.Should().NotBeNull();
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Breakdown.Should().NotBeNull();
        result.Breakdown.Steps.Should().NotBeEmpty("every task result must carry a followable how-to guide");
        result.Breakdown.Where.Should().NotBeNullOrEmpty();
        result.Breakdown.Steps.Should().Contain(s => s.OnClick != null && s.OnClick.StartsWith("/warp"),
            "the guidance steps should include a clickable warp to get close");
    }

    [Test]
    public async Task ExecuteOne_UnknownTaskName_ReturnsNull()
    {
        var service = MakeService();
        var result = await service.ExecuteOne(MakeParams(), "NotARealTaskName");
        result.Should().BeNull();
    }

    [Test]
    public async Task ExecuteAll_ReturnsEveryRegisteredTask_SortedAccessibleFirstThenByProfit()
    {
        var registry = new TaskRegistry();
        var service = MakeService(registry);

        var results = await service.ExecuteAll(MakeParams());

        results.Should().HaveCount(registry.Tasks.Count,
            "every registered task should produce exactly one result, even with no tracked data");
        var accessibleEnds = results.Select(r => r.IsAccessible ? 0 : 1).ToList();
        accessibleEnds.Should().BeInAscendingOrder("accessible tasks must be listed before inaccessible ones");
        foreach (var group in results.GroupBy(r => r.IsAccessible))
        {
            group.Select(r => r.ProfitPerHour).Should().BeInDescendingOrder(
                "within the accessible/inaccessible group, results must be sorted by profit/hour descending");
        }
    }
}

/// <summary>
/// Regression coverage for BuildParameters' id handling: player state
/// (<see cref="Models.IPersistenceService.GetStateObject"/>) is keyed by the player's Minecraft
/// NAME, while the tracked profit history (<see cref="TrackedProfitService.GetHistoryForPlayer"/>)
/// is keyed by uuid. BuildParameters used to query both with the same incoming id, which left
/// history silently empty whenever a caller (e.g. SkyModCommands' old TaskCommand) passed a uuid
/// instead of a name. <see cref="TaskExecutionService.LoadStateAndHistory"/> is the extracted seam
/// that resolves the two independently; it is exercised here directly (rather than through the full
/// <c>playerId</c> overload) since it is the only part of BuildParameters that reads
/// persistence/profitService - prices/estimator/mayor are irrelevant to this bug and are left null.
/// </summary>
public class TaskExecutionServiceIdResolutionTests
{
    private static TaskExecutionService MakeService(Mock<IPersistenceService> persistence, Mock<TrackedProfitService> profitService)
    {
        // A real (Null) logger rather than null: LoadStateAndHistory's guard (see the read-guard
        // tests below) logs a warning on a degraded read, which would NRE against a null logger.
        return new TaskExecutionService(persistence.Object, profitService.Object, null, null, null,
            new TaskRegistry(), null, null, NullLogger<TaskExecutionService>.Instance);
    }

    private static Mock<TrackedProfitService> MakeProfitServiceMock() => new((global::Cassandra.ISession)null);

    [Test]
    public async Task LoadStateAndHistory_CalledWithName_LoadsStateByNameAndHistoryByTheStatesUuid()
    {
        var uuid = Guid.NewGuid();
        var expectedHistoryId = uuid.ToString("N");
        var state = new StateObject { PlayerId = "Ekwav", McInfo = new McInfo { Uuid = uuid } };
        var persistence = new Mock<IPersistenceService>();
        persistence.Setup(p => p.GetStateObject("Ekwav")).ReturnsAsync(state);
        var profitService = MakeProfitServiceMock();
        var history = new List<Period> { new() { PlayerUuid = expectedHistoryId } };
        profitService
            .Setup(p => p.GetHistoryForPlayer(expectedHistoryId, It.IsAny<DateTime?>(), It.IsAny<int>()))
            .ReturnsAsync(history);

        var service = MakeService(persistence, profitService);
        var (resultState, resultHistory, resolvedId) = await service.LoadStateAndHistory("Ekwav", default);

        resultState.Should().BeSameAs(state);
        resolvedId.Should().Be(expectedHistoryId,
            "history/PlayerUuid must use the state's own uuid, not the name it was looked up under");
        resultHistory.Should().BeSameAs(history);
        persistence.Verify(p => p.GetStateObject("Ekwav"), Times.Once);
        profitService.Verify(p => p.GetHistoryForPlayer(expectedHistoryId, It.IsAny<DateTime?>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task LoadStateAndHistory_CalledWithUuid_StillTriesTheStateLookup_AndHistoryUsesThatUuid()
    {
        // No name-keyed state exists under a uuid, so the (still attempted) lookup comes back empty -
        // exactly like the real PersistenceService.GetStateObject does for an unknown key - carrying
        // no uuid of its own (McInfo defaults to Guid.Empty).
        var uuidStr = Guid.NewGuid().ToString("N");
        var persistence = new Mock<IPersistenceService>();
        persistence.Setup(p => p.GetStateObject(uuidStr)).ReturnsAsync(new StateObject { PlayerId = uuidStr });
        var profitService = MakeProfitServiceMock();
        var history = new List<Period> { new() { PlayerUuid = uuidStr } };
        profitService
            .Setup(p => p.GetHistoryForPlayer(uuidStr, It.IsAny<DateTime?>(), It.IsAny<int>()))
            .ReturnsAsync(history);

        var service = MakeService(persistence, profitService);
        var (_, resultHistory, resolvedId) = await service.LoadStateAndHistory(uuidStr, default);

        resolvedId.Should().Be(uuidStr, "with no usable uuid on the (empty) state, the given uuid itself must be used for history/PlayerUuid");
        resultHistory.Should().BeSameAs(history);
        persistence.Verify(p => p.GetStateObject(uuidStr), Times.Once,
            "the state lookup should still be attempted even when called with a uuid");
        profitService.Verify(p => p.GetHistoryForPlayer(uuidStr, It.IsAny<DateTime?>(), It.IsAny<int>()), Times.Once);
    }

    // ── Read guards: a thrown or hanging state/history read must not fail or hang the whole
    // /results request - GetResults/GetResult (via ExecuteAll/ExecuteOne -> BuildParameters ->
    // LoadStateAndHistory) must degrade to an empty ExtractedInfo/LocationProfit instead, the same
    // way TaskController.GetEstimates/LoadState already degrades to community-only estimates. ──

    private static readonly TimeSpan BoundedTime = TimeSpan.FromSeconds(3);

    [Test]
    public async Task LoadStateAndHistory_PersistenceThrows_DegradesToNullState_WithinBoundedTime()
    {
        var persistence = new Mock<IPersistenceService>();
        persistence.Setup(p => p.GetStateObject(It.IsAny<string>())).ThrowsAsync(new Exception("cassandra unavailable"));
        var profitService = MakeProfitServiceMock();
        profitService.Setup(p => p.GetHistoryForPlayer(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Period>());

        var service = MakeService(persistence, profitService);
        var stopwatch = Stopwatch.StartNew();
        var (state, history, resolvedId) = await service.LoadStateAndHistory("Ekwav", default);
        stopwatch.Elapsed.Should().BeLessThan(BoundedTime, "a thrown state read must not propagate or hang");

        state.Should().BeNull("a failed state read must degrade to an empty ExtractedInfo, not throw");
        resolvedId.Should().Be("Ekwav", "with no usable state the given id must still be used for history");
        history.Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public async Task LoadStateAndHistory_PersistenceNeverCompletes_TimesOut_WithinBoundedTime()
    {
        var neverCompletes = new TaskCompletionSource<StateObject>();
        var persistence = new Mock<IPersistenceService>();
        persistence.Setup(p => p.GetStateObject(It.IsAny<string>())).Returns(neverCompletes.Task);
        var profitService = MakeProfitServiceMock();
        profitService.Setup(p => p.GetHistoryForPlayer(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Period>());

        var service = MakeService(persistence, profitService);
        var stopwatch = Stopwatch.StartNew();
        var (state, history, resolvedId) = await service.LoadStateAndHistory("Ekwav", default);
        stopwatch.Elapsed.Should().BeLessThan(BoundedTime, "a hanging state read must be timed out, not awaited forever");

        state.Should().BeNull();
        resolvedId.Should().Be("Ekwav");
        history.Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public async Task LoadStateAndHistory_HistoryThrows_DegradesToEmptyHistory_WithinBoundedTime()
    {
        var uuid = Guid.NewGuid();
        var state = new StateObject { PlayerId = "Ekwav", McInfo = new McInfo { Uuid = uuid } };
        var persistence = new Mock<IPersistenceService>();
        persistence.Setup(p => p.GetStateObject("Ekwav")).ReturnsAsync(state);
        var profitService = MakeProfitServiceMock();
        profitService.Setup(p => p.GetHistoryForPlayer(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<int>()))
            .ThrowsAsync(new Exception("profit history unavailable"));

        var service = MakeService(persistence, profitService);
        var stopwatch = Stopwatch.StartNew();
        var (resultState, history, resolvedId) = await service.LoadStateAndHistory("Ekwav", default);
        stopwatch.Elapsed.Should().BeLessThan(BoundedTime, "a thrown history read must not propagate or hang");

        resultState.Should().BeSameAs(state, "the state read itself succeeded and must still be used");
        resolvedId.Should().Be(uuid.ToString("N"));
        history.Should().NotBeNull().And.BeEmpty("a failed history read must degrade to empty LocationProfit, not throw");
    }
}
