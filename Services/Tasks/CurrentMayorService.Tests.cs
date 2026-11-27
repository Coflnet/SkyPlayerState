using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Mayor.Client.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class CurrentMayorServiceTests
{
    private class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Mock<Mayor.Client.Api.IMayorApi> api;
    private TestClock clock;
    private Queue<Task<CoflnetSkyMayorModelsModelCandidate>> answers;
    private CurrentMayorService service;

    [SetUp]
    public void Setup()
    {
        api = new();
        clock = new();
        answers = new();
        api.Setup(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(() => answers.Dequeue());
        service = new CurrentMayorService(api.Object, NullLogger<CurrentMayorService>.Instance, clock);
    }

    private static Task<CoflnetSkyMayorModelsModelCandidate> Mayor(string name) =>
        Task.FromResult(new CoflnetSkyMayorModelsModelCandidate { Key = name.ToLowerInvariant(), Name = name, Perks = [new() { Name = "Perk", Description = "" }] });

    [Test]
    public async Task ServesTheKnownMayor_WhileASlowRefreshRuns()
    {
        answers.Enqueue(Mayor("Diaz"));
        (await service.GetCurrentMayorSnapshot()).Name.Should().Be("diaz");
        var slow = new TaskCompletionSource<CoflnetSkyMayorModelsModelCandidate>();
        answers.Enqueue(slow.Task);
        clock.Now += TimeSpan.FromHours(2);

        var during = service.GetCurrentMayorSnapshot();
        var second = service.GetCurrentMayorSnapshot();

        during.IsCompleted.Should().BeTrue("a hanging mayor api must not delay callers once a mayor is known");
        (await during).Name.Should().Be("diaz");
        (await second).Name.Should().Be("diaz");
        api.Verify(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        slow.SetResult(await Mayor("Diana"));
        (await service.GetCurrentMayorSnapshot()).Name.Should().Be("diana");
    }

    [Test]
    public async Task KeepsTheKnownMayor_WhenARefreshFails_AndRetriesLater()
    {
        answers.Enqueue(Mayor("Diaz"));
        await service.GetCurrentMayorSnapshot();
        answers.Enqueue(Task.FromException<CoflnetSkyMayorModelsModelCandidate>(new Exception("down")));
        clock.Now += TimeSpan.FromHours(2);

        (await service.GetCurrentMayorSnapshot()).Name.Should().Be("diaz");
        (await service.GetCurrentMayorSnapshot()).Name.Should().Be("diaz");
        api.Verify(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        answers.Enqueue(Mayor("Diana"));
        clock.Now += TimeSpan.FromMinutes(6);
        await service.GetCurrentMayorSnapshot();
        (await service.GetCurrentMayorSnapshot()).Name.Should().Be("diana");
    }

    [Test]
    public async Task WaitsForTheFirstLoad_AndCarriesThePerks()
    {
        answers.Enqueue(Mayor("Marina"));

        var mayor = await service.GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("marina");
        mayor.HasPerk("Perk").Should().BeTrue();
    }
}
