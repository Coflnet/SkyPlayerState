using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class PlayerStateBackgroundServiceTests
{
    private static UpdateMessage Msg(string? player, string? user = null, int seq = 0)
        => new() { PlayerId = player!, UserId = user!, ChatBatch = new() { seq.ToString() } };

    [Test]
    public void StateKey_SeparatesAnonymousUsers_ButKeepsRealPlayers()
    {
        PlayerStateBackgroundService.StateKey(Msg("abc", "u1")).Should().Be("abc");
        PlayerStateBackgroundService.StateKey(Msg(null)).Should().Be(StateObject.AnonymousId);
        PlayerStateBackgroundService.StateKey(Msg(" ", "")).Should().Be(StateObject.AnonymousId);
        PlayerStateBackgroundService.StateKey(Msg(StateObject.AnonymousId, "u1"))
            .Should().NotBe(PlayerStateBackgroundService.StateKey(Msg(null, "u2")));
    }

    [Test]
    public async Task ProcessGrouped_RunsAnonymousUsersConcurrently()
    {
        var running = 0;
        var maxRunning = 0;
        var updates = Enumerable.Range(0, 6).Select(i => Msg(null, "user" + i)).ToList();
        await PlayerStateBackgroundService.ProcessGrouped(updates, async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
        });
        maxRunning.Should().BeGreaterThan(1);
    }

    [Test]
    public async Task ProcessGrouped_KeepsPerPlayerOrder_AndBoundsConcurrency()
    {
        var order = new List<int>();
        var running = 0;
        var maxRunning = 0;
        var updates = Enumerable.Range(0, 5).Select(i => Msg("player", null, i)).ToList();
        updates.AddRange(Enumerable.Range(0, 10).Select(i => Msg("other" + i)));
        await PlayerStateBackgroundService.ProcessGrouped(updates, async update =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(20);
            if (update.PlayerId == "player")
                lock (order)
                    order.Add(int.Parse(update.ChatBatch![0]));
            Interlocked.Decrement(ref running);
        }, maxParallelGroups: 3);
        order.Should().Equal(0, 1, 2, 3, 4);
        maxRunning.Should().BeLessThanOrEqualTo(3);
    }

    [TestCase(2, false)]
    [TestCase(int.MaxValue, true)]
    public async Task Update_WaitsBetweenRetries_AndOnlyDropsAfterTheLastAttempt(int failures, bool dropped)
    {
        var handler = new FailingHandler(failures);
        var service = ServiceWithOnlyChatHandler(handler, "60,60,60");
        var msg = Msg("retry-player");
        msg.Kind = UpdateMessage.UpdateKind.CHAT;

        var error = await (Task<bool>)typeof(PlayerStateBackgroundService)
            .GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { msg, 0 })!;

        error.Should().Be(dropped);
        handler.Calls.Should().HaveCount(dropped ? 4 : 3);
        for (var i = 1; i < handler.Calls.Count; i++)
            (handler.Calls[i] - handler.Calls[i - 1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50));
    }

    [TestCase("-1")]
    [TestCase("180000")]
    [TestCase("0,0,0,0,0,0")]
    [TestCase("abc")]
    public void RetryDelays_OutOfBoundsConfig_FallsBackToTheBoundedDefault(string configured)
    {
        var service = ServiceWithOnlyChatHandler(new FailingHandler(0), configured);

        var delays = (TimeSpan[])typeof(PlayerStateBackgroundService)
            .GetField("retryDelays", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;

        delays.Should().Equal(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    private static PlayerStateBackgroundService ServiceWithOnlyChatHandler(UpdateListener handler, string retryDelaysMs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UPDATE_RETRY_DELAYS_MS"] = retryDelaysMs })
            .Build();
        var service = new PlayerStateBackgroundService(new MockingServices(), config, NullLogger<PlayerStateBackgroundService>.Instance,
            new InMemoryPersistence(), new ActivitySource("test"), NullLoggerFactory.Instance);
        var handlers = (ConcurrentDictionary<UpdateMessage.UpdateKind, List<UpdateListener>>)typeof(PlayerStateBackgroundService)
            .GetField("Handlers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        handlers[UpdateMessage.UpdateKind.CHAT] = new List<UpdateListener> { handler };
        return service;
    }

    private class FailingHandler(int failures) : UpdateListener
    {
        public List<TimeSpan> Calls { get; } = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();

        public override Task Process(UpdateArgs args)
        {
            Calls.Add(clock.Elapsed);
            if (Calls.Count <= failures)
                throw new InvalidOperationException("Unable to connect to any of the specified MySQL hosts.");
            return Task.CompletedTask;
        }
    }

    /// <summary>Resolves every interface dependency of the built-in listeners to an inert mock.</summary>
    private class MockingServices : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        public IServiceProvider ServiceProvider => this;
        public IServiceScope CreateScope() => this;
        public void Dispose() { }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
                return this;
            if (!serviceType.IsInterface)
                return null;
            return ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(serviceType))!).Object;
        }
    }

    private class InMemoryPersistence : IPersistenceService
    {
        public Task<StateObject> GetStateObject(string playerId) => Task.FromResult(new StateObject());
        public Task SaveStateObject(StateObject stateObject) => Task.CompletedTask;
        public Task ForceSave(StateObject stateObject) => Task.CompletedTask;
        public Task DeleteStateObject(string playerId) => Task.CompletedTask;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }
}
