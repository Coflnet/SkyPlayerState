using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Regression tests for the gear-uuid dedup in <see cref="CollectionListener.HandleInventory"/> -
/// production found unique gear (which flips its inventory COUNT between e.g. 1 and 2 whenever a
/// player swaps equipment sets, enters the Dojo/dungeons/Kuudra etc, without ever being a real drop)
/// was the largest source of false "collected" revenue (2.3B coins in a 6h sample for one player's
/// TERMINATOR/RETIA_SUPREMA alone). Every view is processed through the same
/// <see cref="RecentViewsUpdate"/> -&gt; <see cref="CollectionListener"/> ordering production uses, so
/// <c>HandleInventory</c>'s "previous view" lookup behaves exactly as it does live.
/// </summary>
public class CollectionListenerInventoryTests
{
    private static Item GearItem(string tag, string uuid) => new()
    {
        Tag = tag,
        ItemName = tag,
        Count = 1,
        ExtraAttributes = new Dictionary<string, object> { { "uuid", uuid } }
    };

    private static Item StackableItem(string tag, int count) => new()
    {
        Tag = tag,
        ItemName = tag,
        Count = count
    };

    private static async Task ProcessView(StateObject state, ChestView chest)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest }
        };
        // Matches production handler order (PlayerStateBackgroundService): RecentViewsUpdate pushes
        // the current view before CollectionListener reads "the previous view" from the same queue.
        await new RecentViewsUpdate().Process(args);
        await new CollectionListener().Process(args);
    }

    [Test]
    public async Task KnownUuidReappearingAtHigherCountIsNotCounted()
    {
        // (a) An item uuid already in the known set (seen earlier, e.g. via storage) reappearing
        // alongside an already-present copy - the tag's count flips 1 -> 2 - must not be counted.
        var state = new StateObject();
        var knownUuid = Guid.NewGuid().ToString();
        // Simulate this uuid having been seen before (e.g. in the Ender Chest) without needing the
        // full StorageListener pipeline - same entry point StorageListener itself calls.
        CollectionListener.RegisterKnownItemUuids(state, new ChestView { Items = new() { GearItem("TERMINATOR", knownUuid) } });

        var uuidA = Guid.NewGuid().ToString();
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA) } });
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA), GearItem("TERMINATOR", knownUuid) } });

        state.ItemsCollectedRecently.GetValueOrDefault("TERMINATOR").Should().Be(0,
            "the second copy's uuid was already known - re-equipping it is not a drop");
    }

    [Test]
    public async Task NeverSeenUuidIsCountedOnceAndNotAgainAfterLeavingAndReturning()
    {
        // (b)+(c) combined: a genuinely new uuid is counted exactly once when first seen, produces
        // no negative entry when it later leaves the accessible view, and is not counted again when
        // it reappears (it is in the known set by then).
        var state = new StateObject();
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();

        // View 1: only uuidA present (first ever view - nothing to diff against yet).
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA) } });
        state.ItemsCollectedRecently.Should().BeEmpty();

        // View 2: uuidB shows up for the first time alongside uuidA (count 1 -> 2).
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA), GearItem("TERMINATOR", uuidB) } });
        state.ItemsCollectedRecently.GetValueOrDefault("TERMINATOR").Should().Be(1, "uuidB is genuinely new");

        // View 3: uuidB leaves again (count 2 -> 1) - must not produce a negative entry.
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA) } });
        state.ItemsCollectedRecently.GetValueOrDefault("TERMINATOR").Should().Be(1,
            "a uuid item leaving is never counted as consumed/negative");

        // View 4: uuidB comes back (count 1 -> 2 again) - already known, must not be counted twice.
        await ProcessView(state, new ChestView { Name = "", Items = new() { GearItem("TERMINATOR", uuidA), GearItem("TERMINATOR", uuidB) } });
        state.ItemsCollectedRecently.GetValueOrDefault("TERMINATOR").Should().Be(1,
            "uuidB was already recorded as known the first time it appeared");
    }

    [Test]
    public async Task StackableItemsWithoutUuidStillCountPositiveAndNegativeDiffs()
    {
        // (d) Tags with no uuid-bearing items at all keep the pre-existing plain count-diff logic,
        // unchanged - both increases and decreases.
        var state = new StateObject();
        await ProcessView(state, new ChestView { Name = "", Items = new() { StackableItem("ENCHANTED_COBBLESTONE", 64) } });
        state.ItemsCollectedRecently.Should().BeEmpty();

        await ProcessView(state, new ChestView { Name = "", Items = new() { StackableItem("ENCHANTED_COBBLESTONE", 100) } });
        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_COBBLESTONE").Should().Be(36);

        await ProcessView(state, new ChestView { Name = "", Items = new() { StackableItem("ENCHANTED_COBBLESTONE", 20) } });
        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_COBBLESTONE").Should().Be(36 - 80);
    }

    [Test]
    public void RegisterKnownItemUuidsIsCappedAtOneThousandTwentyFourOldestFirst()
    {
        var state = new StateObject();
        var uuids = new List<string>();
        for (var i = 0; i < 1100; i++)
            uuids.Add(Guid.NewGuid().ToString());

        foreach (var uuid in uuids)
            CollectionListener.RegisterKnownItemUuids(state, new ChestView { Items = new() { GearItem("SOMETHING", uuid) } });

        state.KnownItemUuids.Should().HaveCount(1024);
        // the oldest entries (registered first) must have been evicted, the newest kept.
        CollectionListener.TryGetItemUuidHash(GearItem("SOMETHING", uuids[0]), out var oldestHash);
        CollectionListener.TryGetItemUuidHash(GearItem("SOMETHING", uuids[^1]), out var newestHash);
        state.KnownItemUuids.Should().NotContain(oldestHash);
        state.KnownItemUuids.Should().Contain(newestHash);
    }

    [Test]
    public async Task StorageListenerRegistersItemUuidsBeforeItsOwnStorageTypeFiltering()
    {
        // StorageListener.Process registers this view's item uuids unconditionally, BEFORE it even
        // decides whether the view is a storage chest it should save - so the known set is populated
        // "in a way that does not depend on the collection tracking being skipped or not" even when
        // the rest of that method can't run (here: no StorageService/Cassandra wired up in this unit
        // test - the registration side effect must already have happened by the time that throws).
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY,
                PlayerId = "p1",
                Chest = new ChestView { Name = "Ender Chest", Items = new() { GearItem("TERMINATOR", uuid) } }
            }
        };

        try { await new StorageListener().Process(args); } catch { /* no StorageService wired up - irrelevant to this test */ }

        CollectionListener.TryGetItemUuidHash(GearItem("TERMINATOR", uuid), out var hash);
        state.KnownItemUuids.Should().Contain(hash);
    }

    /// <summary>Captures every log call so tests can assert on level/message without a mocking library.</summary>
    private sealed class CapturingLogger : ILogger<CollectionListener>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }
    }

    [Test]
    public async Task CroesusFloorMenuLogsAtInformation()
    {
        // There was no evidence in production logs that HandleCroesusChest's floor recognition ever
        // fires - add an Information line so the next log analysis can confirm it.
        var state = new StateObject();
        var logger = new CapturingLogger();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1",
                Chest = new ChestView { Name = "Master Mode Catacombs - Floor III", Items = new() }
            }
        };
        args.AddService<ILogger<CollectionListener>>(logger);

        await new CollectionListener().Process(args);

        state.ExtractedInfo.LastDungeonFloor.Should().Be("The Catacombs (M3)");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains("Croesus menu") && e.Message.Contains("Master Mode Catacombs - Floor III")
            && e.Message.Contains("The Catacombs (M3)"));
    }
}
