using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ItemIdCacheTests
{
    private Mock<IItemsService> itemsService = null!;
    private List<List<Item>> calls = null!;
    private long nextId;

    [SetUp]
    public void Setup()
    {
        calls = new();
        nextId = 100;
        itemsService = new Mock<IItemsService>();
        itemsService.Setup(s => s.FindOrCreate(It.IsAny<IEnumerable<Item>>()))
            .ReturnsAsync((IEnumerable<Item> items) =>
            {
                var list = items.ToList();
                lock (calls)
                    calls.Add(list);
                return list.Select(i => new Item(i) { Id = nextId++ }).ToList();
            });
    }

    private static Item MakeItem(string uuid) => new()
    {
        Tag = "HYPERION",
        ItemName = "Hyperion",
        ExtraAttributes = new Dictionary<string, object>() { { "uuid", uuid }, { "tier", 5 } }
    };

    private static UpdateMessage Msg(string player, params Item[] items) => new()
    {
        Kind = UpdateMessage.UpdateKind.INVENTORY,
        PlayerId = player,
        Chest = new() { Name = "Chest", Items = items.ToList() }
    };

    private static Func<string, StateObject?> NoStates => _ => null;

    private IEnumerable<Item> Requested => calls.SelectMany(c => c);

    [Test]
    public async Task PrefetchRequestsEachDistinctItemOnce()
    {
        var cache = new ItemIdCache();
        var batch = new[]
        {
            Msg("a", MakeItem("1"), MakeItem("2")),
            Msg("a", MakeItem("2"), MakeItem("3")),
            Msg("b", MakeItem("1"), MakeItem("3")),
        };

        await cache.Prefetch(batch, itemsService.Object, NoStates, NullLogger.Instance);

        calls.Should().HaveCount(1);
        Requested.Select(i => i.ExtraAttributes!["uuid"]).Should().BeEquivalentTo(new[] { "1", "2", "3" });
    }

    [Test]
    public async Task PrefetchSkipsCachedItems()
    {
        var cache = new ItemIdCache();
        cache.Cache(new[] { new Item(MakeItem("1")) { Id = 5 } });

        await cache.Prefetch(new[] { Msg("a", MakeItem("1"), MakeItem("2")) }, itemsService.Object, NoStates, NullLogger.Instance);

        Requested.Select(i => i.ExtraAttributes!["uuid"]).Should().Equal("2");
    }

    [Test]
    public async Task PrefetchSkipsItemsPresentInRecentViews()
    {
        var cache = new ItemIdCache();
        var state = new StateObject();
        state.RecentViews.Enqueue(new() { Items = new() { new Item(MakeItem("1")) { Id = 7 } } });

        await cache.Prefetch(new[] { Msg("a", MakeItem("1"), MakeItem("2")), Msg("b", MakeItem("1")) },
            itemsService.Object, key => key == "a" ? state : null, NullLogger.Instance);

        // "1" is only skipped for the player that has it locally
        Requested.Select(i => i.ExtraAttributes!["uuid"]).Should().BeEquivalentTo(new[] { "1", "2" });
    }

    [Test]
    public async Task PrefetchChunksLargeBatches()
    {
        var cache = new ItemIdCache();
        var items = Enumerable.Range(0, ItemIdCache.PrefetchChunkSize + 1).Select(i => MakeItem(i.ToString())).ToArray();

        await cache.Prefetch(new[] { Msg("a", items) }, itemsService.Object, NoStates, NullLogger.Instance);

        calls.Should().HaveCount(2);
        Requested.Should().HaveCount(items.Length);
    }

    [Test]
    public async Task PrefetchedIdsAreAssignedWithoutSecondLookup()
    {
        var cache = new ItemIdCache();
        var listener = new ItemIdAssignUpdate(cache);
        var batch = new[] { Msg("a", MakeItem("1")), Msg("b", MakeItem("1"), MakeItem("2")) };
        await cache.Prefetch(batch, itemsService.Object, NoStates, NullLogger.Instance);
        calls.Clear();

        foreach (var msg in batch)
        {
            var args = new MockedUpdateArgs() { currentState = new StateObject(), msg = msg };
            args.AddService<IItemsService>(itemsService.Object);
            await listener.Process(args);
        }

        calls.Should().BeEmpty();
        batch.SelectMany(m => m.Chest!.Items).Should().OnlyContain(i => i.Id != null);
        batch[0].Chest!.Items[0].Id.Should().Be(batch[1].Chest!.Items[0].Id);
    }

    [Test]
    public async Task PrefetchFailureFallsBackToPerMessageLookup()
    {
        var cache = new ItemIdCache();
        var listener = new ItemIdAssignUpdate(cache);
        var failing = new Mock<IItemsService>();
        failing.Setup(s => s.FindOrCreate(It.IsAny<IEnumerable<Item>>())).ThrowsAsync(new InvalidOperationException("scylla down"));
        var msg = Msg("a", MakeItem("1"));

        var prefetch = () => cache.Prefetch(new[] { msg }, failing.Object, NoStates, NullLogger.Instance);
        await prefetch.Should().NotThrowAsync();

        var args = new MockedUpdateArgs() { currentState = new StateObject(), msg = msg };
        args.AddService<IItemsService>(itemsService.Object);
        await listener.Process(args);
        msg.Chest!.Items[0].Id.Should().NotBeNull();
        calls.Should().HaveCount(1);
    }

    [Test]
    public async Task PrefetchIgnoresNonInventoryAndMissingChest()
    {
        var cache = new ItemIdCache();
        var chat = new UpdateMessage() { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "a" };
        var noChest = new UpdateMessage() { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "a" };

        await cache.Prefetch(new[] { chat, noChest }, itemsService.Object, NoStates, NullLogger.Instance);

        calls.Should().BeEmpty();
    }

    private static Mock<ILogger> LoggerMock() => new();

    private static void VerifyWarnings(Mock<ILogger> logger, Times times) => logger.Verify(l => l.Log(
        LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
        (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), times);

    [Test]
    public void SlowChainWarnsOnlyAboveThreshold()
    {
        var logger = LoggerMock();
        var chains = new[]
        {
            new PlayerStateBackgroundService.ChainResult("fast", 2, TimeSpan.FromSeconds(1)),
            new PlayerStateBackgroundService.ChainResult("slow", 90, TimeSpan.FromSeconds(8)),
        };

        PlayerStateBackgroundService.ReportChains(chains, 3, 120, 10, logger.Object, _ => "Name");
        VerifyWarnings(logger, Times.Never());

        PlayerStateBackgroundService.ReportChains(chains, 3, 120, 5, logger.Object, k => k == "slow" ? "Ekwav" : null);
        VerifyWarnings(logger, Times.Once());
        logger.Invocations.Last().Arguments[2].ToString().Should().Contain("slow").And.Contain("Ekwav").And.Contain("8000");
    }

    [Test]
    public void SlowestChainGaugeIsSetPerPartition()
    {
        var chains = new[]
        {
            new PlayerStateBackgroundService.ChainResult("a", 1, TimeSpan.FromSeconds(1)),
            new PlayerStateBackgroundService.ChainResult("b", 3, TimeSpan.FromSeconds(2.5)),
        };

        PlayerStateBackgroundService.ReportChains(chains, 41, 4, 5, NullLogger.Instance, _ => null);

        var text = new System.IO.MemoryStream();
        Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(text).Wait();
        System.Text.Encoding.UTF8.GetString(text.ToArray())
            .Should().Contain("sky_playerstate_batch_slowest_chain_seconds{partition=\"41\"} 2.5");
    }

    [Test]
    public async Task ProcessGroupedReportsChainLengths()
    {
        var updates = new[] { Msg("a"), Msg("a"), Msg("b") };

        var chains = await PlayerStateBackgroundService.ProcessGrouped(updates, _ => Task.CompletedTask);

        chains.ToDictionary(c => c.StateKey, c => c.Updates).Should().BeEquivalentTo(new Dictionary<string, int> { { "a", 2 }, { "b", 1 } });
    }
}
