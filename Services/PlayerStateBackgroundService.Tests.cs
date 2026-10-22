using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
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

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }
}
