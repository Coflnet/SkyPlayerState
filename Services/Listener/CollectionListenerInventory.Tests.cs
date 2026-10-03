using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.Extensions.Logging;
using Moq;
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

    private static async Task ProcessView(StateObject state, ChestView chest, DateTime receivedAt = default, CraftRecipeCache? craftCache = null)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest, ReceivedAt = receivedAt }
        };
        // the swap guard logs through the DI logger, like production
        args.AddService<ILogger<CollectionListener>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionListener>.Instance);
        if (craftCache != null)
            args.AddService(craftCache);
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
                Chest = new ChestView { Name = "Master Catacombs - Floor V", Items = new() { new Item { ItemName = "Chest Modifiers" } } }
            }
        };
        args.AddService<ILogger<CollectionListener>>(logger);

        await new CollectionListener().Process(args);

        state.ExtractedInfo.LastDungeonFloor.Should().Be("The Catacombs (M5)");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains("Croesus menu") && e.Message.Contains("Master Catacombs - Floor V")
            && e.Message.Contains("The Catacombs (M5)"));
    }

    private static ChestView CroesusMenu(string title) => new()
    {
        Name = title,
        Items = new() { new Item { ItemName = "Wood" }, new Item { ItemName = "Gold" }, new Item { ItemName = "Go Back" }, new Item { ItemName = "Close" }, new Item { ItemName = "Chest Modifiers" } }
    };

    [Test]
    public void MasterCatacombsFloorV_MapsToM5()
        => CollectionListener.TryParseCroesusFloor(CroesusMenu("Master Catacombs - Floor V")).Should().Be("The Catacombs (M5)");

    [Test]
    public void CatacombsFloorVII_WithMenuItems_MapsToF7()
        => CollectionListener.TryParseCroesusFloor(CroesusMenu("Catacombs - Floor VII")).Should().Be("The Catacombs (F7)");

    [Test]
    public void BestScoresView_IsNotACroesusMenu()
    {
        var view = new ChestView
        {
            Name = "The Catacombs - Floor I",
            Items = new() { new Item { ItemName = "256 (A)" }, new Item { ItemName = "269 (A)" }, new Item { ItemName = "Go Back" }, new Item { ItemName = "Close" } }
        };
        CollectionListener.TryParseCroesusFloor(view).Should().BeNull();
    }

    // ---- first-time drops (tag absent from the previous accessible inventory) ----

    /// <summary>Container part followed by the 36 accessible slots (padded with empty slots).</summary>
    private static ChestView View(string name, List<Item> container, params Item[] inventory)
    {
        var items = new List<Item>(container);
        items.AddRange(inventory);
        while (items.Count < container.Count + 36)
            items.Add(new Item());
        return new ChestView { Name = name, Items = items };
    }

    /// <summary>
    /// A bare inventory upload from Minecraft 1.21: 5 crafting and 4 armor slots, the 36 inventory
    /// slots (<paramref name="inventory"/> fills them from the top left), then the offhand slot.
    /// </summary>
    private static ChestView BareInventory(params Item[] inventory)
    {
        var items = Enumerable.Range(0, 9).Select(_ => new Item()).ToList();
        items.AddRange(inventory);
        while (items.Count < 46)
            items.Add(new Item());
        return new ChestView { Name = "Crafting", Items = items };
    }

    /// <summary>
    /// Production 2026-10-02 (Zecs1): 12x "+64 SHARD_FLAMING_SPIDER" between "Crafting" and a bazaar or
    /// chest view. The last 36 slots of the 46 slot bare inventory start one slot late, so the stack in
    /// the top left inventory slot was missing there and came back as a gain in the next chest view.
    /// </summary>
    [Test]
    public async Task StackInTheTopLeftSlotIsNotAGainAfterTheBareInventory()
    {
        var state = new StateObject();
        var bazaar = Enumerable.Range(0, 36).Select(_ => new Item()).ToList();
        await ProcessView(state, View("", new(), StackableItem("SHARD_FLAMING_SPIDER", 64), StackableItem("SHARD_FLAMING_SPIDER", 64)), Now);

        await ProcessView(state, BareInventory(StackableItem("SHARD_FLAMING_SPIDER", 64), StackableItem("SHARD_FLAMING_SPIDER", 64)), Now);
        await ProcessView(state, View("Bazaar ➜ Oddities", bazaar, StackableItem("SHARD_FLAMING_SPIDER", 64), StackableItem("SHARD_FLAMING_SPIDER", 64)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task StackInTheTopLeftSlotIsNotALossInTheBareInventory()
    {
        var state = new StateObject();
        await ProcessView(state, View("SkyBlock Menu", Enumerable.Range(0, 54).Select(_ => new Item()).ToList(),
            StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);

        await ProcessView(state, BareInventory(StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    /// <summary>
    /// A player at "Your Island" whose stored periods are captured, with the given clean prices.
    /// </summary>
    private static (StateObject State, List<TrackedProfitService.Period> Stored, Func<ChestView, Task> Process) PeriodHarness(Dictionary<string, long> prices)
    {
        var stored = new List<TrackedProfitService.Period>();
        var profitService = new Mock<TrackedProfitService>((global::Cassandra.ISession)null);
        profitService.Setup(p => p.AddPeriod(It.IsAny<TrackedProfitService.Period>()))
            .Callback<TrackedProfitService.Period>(stored.Add).Returns(Task.CompletedTask);
        var sniperApi = new Mock<ISniperApi>();
        sniperApi.Setup(a => a.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(prices);
        var bazaarApi = new Mock<IBazaarApi>();
        bazaarApi.Setup(b => b.GetAllPricesAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ItemPrice>());
        var listener = new CollectionListener();
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "Your Island";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddMinutes(-2);
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow.AddSeconds(-10);
        async Task Process(ChestView chest)
        {
            var args = new MockedUpdateArgs
            {
                currentState = state,
                msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest, ReceivedAt = DateTime.UtcNow }
            };
            args.AddService<ILogger<CollectionListener>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionListener>.Instance);
            args.AddService(profitService.Object);
            args.AddService(sniperApi.Object);
            args.AddService(bazaarApi.Object);
            await new RecentViewsUpdate().Process(args);
            await listener.Process(args);
        }
        return (state, stored, Process);
    }

    /// <summary>
    /// Production 2026-10-02 (IamCarry): one -21B period with 213 item types, because periods only ended
    /// on a scoreboard update. An inventory change worth more than 10M coins now ends the period right
    /// after it, so it can be checked in a period of its own.
    /// </summary>
    [Test]
    public async Task InventoryChangeAboveTenMillionCoinsEndsThePeriod()
    {
        var (state, stored, Process) = PeriodHarness(new() { { "ENCHANTED_DIAMOND_BLOCK", 200_000 }, { "ENCHANTED_COBBLESTONE", 1_000 } });
        await Process(View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 10), StackableItem("ENCHANTED_DIAMOND_BLOCK", 1)));

        // 10 * 1,000 coins: stays in the running period
        await Process(View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 20), StackableItem("ENCHANTED_DIAMOND_BLOCK", 1)));
        stored.Should().BeEmpty();
        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_COBBLESTONE").Should().Be(10);

        // 60 * 200,000 coins = 12M: the period ends with this change
        await Process(View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 20), StackableItem("ENCHANTED_DIAMOND_BLOCK", 61)));

        stored.Should().ContainSingle();
        stored[0].Location.Should().Be("Your Island");
        stored[0].ItemsCollected.Should().BeEquivalentTo(new Dictionary<string, int> { { "ENCHANTED_COBBLESTONE", 10 }, { "ENCHANTED_DIAMOND_BLOCK", 60 } });
        state.ItemsCollectedRecently.Should().BeEmpty();
        state.ExtractedInfo.LastLocationChange.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// Production 2026-10-02 (Lajzy, "Craft Item -> Craft Item"): the input left the inventory in one
    /// upload and the output arrived in the next, which the 10M rule stored as a -25M and a +30M period.
    /// </summary>
    [TestCase("Craft Item")]
    [TestCase("Quick Crafting")]
    public async Task LargeChangeInACraftingViewEndsThePeriodWithTheNextView(string craftingView)
    {
        var (state, stored, Process) = PeriodHarness(new() { { "GRIFFIN_FEATHER", 100_000 }, { "BRAIDED_GRIFFIN_FEATHER", 27_000_000 }, { "ENCHANTED_COBBLESTONE", 1_000 } });
        var table = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        await Process(View(craftingView, table, StackableItem("GRIFFIN_FEATHER", 480), StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)));

        // 320 feathers lie in the crafting grid: -32M, but the output is not out yet
        await Process(View(craftingView, table, StackableItem("GRIFFIN_FEATHER", 160), StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)));
        stored.Should().BeEmpty();
        state.ExtractedInfo.PeriodSplitPending.Should().BeTrue();

        // the first view after the crafting table shows the output: one period with both sides
        await Process(View("", new(), StackableItem("GRIFFIN_FEATHER", 160), StackableItem("BRAIDED_GRIFFIN_FEATHER", 3)));
        stored.Should().ContainSingle();
        stored[0].ItemsCollected.Should().BeEquivalentTo(new Dictionary<string, int> { { "GRIFFIN_FEATHER", -320 }, { "BRAIDED_GRIFFIN_FEATHER", 2 } });
        state.ExtractedInfo.PeriodSplitPending.Should().BeFalse();

        // nothing is pending any more: a small change afterwards stays in the running period
        await Process(View("", new(), StackableItem("GRIFFIN_FEATHER", 161), StackableItem("BRAIDED_GRIFFIN_FEATHER", 3)));
        stored.Should().ContainSingle();
    }

    [Test]
    public async Task SmallChangeInACraftingViewDoesNotEndThePeriodAfterIt()
    {
        var (state, stored, Process) = PeriodHarness(new() { { "ENCHANTED_COBBLESTONE", 1_000 } });
        var table = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        await Process(View("Craft Item", table, StackableItem("ENCHANTED_COBBLESTONE", 20)));
        await Process(View("Craft Item", table, StackableItem("ENCHANTED_COBBLESTONE", 10)));
        await Process(View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 12)));

        stored.Should().BeEmpty();
        state.ExtractedInfo.PeriodSplitPending.Should().BeFalse();
    }

    /// <summary>
    /// Production 2026-10-03 (Hyperrinon): no scoreboard upload for 35 days, zone still "The Garden". The
    /// first large change stored everything that had changed since as 12.6B of "Melon Farming" there.
    /// </summary>
    [Test]
    public async Task PeriodRunningForDaysIsStoredAtUnknown()
    {
        var (state, stored, Process) = PeriodHarness(new() { { "ENCHANTED_DIAMOND_BLOCK", 200_000 } });
        state.ExtractedInfo.CurrentLocation = "The Garden";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddDays(-35);
        await Process(View("", new(), StackableItem("ENCHANTED_DIAMOND_BLOCK", 1)));
        await Process(View("", new(), StackableItem("ENCHANTED_DIAMOND_BLOCK", 61)));

        stored.Should().ContainSingle();
        stored[0].Location.Should().Be("Unknown");
        state.ExtractedInfo.CurrentLocation.Should().Be("The Garden", "the next scoreboard decides whether the zone changed");
    }

    /// <summary>
    /// Production 2026-10-03 (Hyperrinon): the periods after the first one lasted seconds, but still no
    /// scoreboard had confirmed the zone (2x 1.6B DIVAN_DRILL at "The Garden").
    /// </summary>
    [Test]
    public async Task ShortPeriodWithoutAScoreboardForHoursIsStoredAtUnknown()
    {
        var (state, stored, Process) = PeriodHarness(new() { { "ENCHANTED_DIAMOND_BLOCK", 200_000 } });
        state.ExtractedInfo.CurrentLocation = "The Garden";
        state.ExtractedInfo.CurrentLocationSeenAt = DateTime.UtcNow.AddHours(-2);
        await Process(View("", new(), StackableItem("ENCHANTED_DIAMOND_BLOCK", 1)));
        await Process(View("", new(), StackableItem("ENCHANTED_DIAMOND_BLOCK", 61)));

        stored.Should().ContainSingle();
        stored[0].Location.Should().Be("Unknown");
    }

    [TestCase(4, 1, false, TestName = "Usual period")]
    [TestCase(40, 35, false, TestName = "Dungeon run without a zone confirmation")]
    [TestCase(61, 0, true, TestName = "Period of more than an hour")]
    [TestCase(1, 61, true, TestName = "No scoreboard for more than an hour")]
    public void LocationIsStaleAfterAnHour(int periodMinutes, int scoreboardMinutesAgo, bool stale)
    {
        var now = DateTime.UtcNow;
        var info = new ExtractedInfo { LastLocationChange = now.AddMinutes(-periodMinutes), CurrentLocationSeenAt = now.AddMinutes(-scoreboardMinutesAgo) };

        CollectionListener.IsLocationStale(info, now).Should().Be(stale);
    }

    [Test]
    public void InventoryChangeValueDoesNotLetAGainAndALossCancelOut()
    {
        var before = new Dictionary<string, int> { { "HYPERION", 1 }, { "ENCHANTED_COBBLESTONE", 5 } };
        var after = new Dictionary<string, int> { { "TERMINATOR", 1 }, { "ENCHANTED_COBBLESTONE", 5 } };
        var prices = new Dictionary<string, double> { { "HYPERION", 900_000_000 }, { "TERMINATOR", 700_000_000 }, { "ENCHANTED_COBBLESTONE", 1_000 } };

        CollectionListener.InventoryChangeValue(before, after, prices).Should().Be(1_600_000_000);
    }

    [TestCase(46, 9, TestName = "1.21 bare inventory: offhand slot after the inventory")]
    [TestCase(45, 9, TestName = "1.8 bare inventory or one row chest")]
    [TestCase(90, 54, TestName = "Large chest")]
    [TestCase(2, 0, TestName = "Fewer than 36 slots")]
    public void AccessibleInventoryStartsAfterTheContainerPart(int slots, int expectedStart)
    {
        var items = Enumerable.Range(0, slots).Select(_ => new Item()).ToList();

        CollectionListener.AccessibleInventoryStart(items).Should().Be(expectedStart);
    }

    [Test]
    public async Task NewStackableTagAfterNormalViewIsCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("NECRON_HANDLE", 1)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(1, "first-time drop");
    }

    [Test]
    public async Task NewUuidItemNeverSeenIsCountedOnce()
    {
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), GearItem("HYPERION", uuid)));
        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(1);

        // moving within the inventory afterwards does not count it again
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), GearItem("HYPERION", uuid)));
        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(1);
    }

    [Test]
    public async Task NewTagFromPreviousContainerPartIsNotCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        // previous view is a chest showing the item in its container part only
        await ProcessView(state, View("Craft Item", new() { StackableItem("NECRON_HANDLE", 1) }, StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("NECRON_HANDLE", 1)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(0, "taken out of a chest");
    }

    [Test]
    public async Task NewTagSeenInOlderRecentViewIsNotCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("NECRON_HANDLE", 1)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64))); // dropped
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("NECRON_HANDLE", 1))); // re-picked

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(0);
    }

    [Test]
    public async Task NewUuidItemInKnownUuidsIsNotCounted()
    {
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        CollectionListener.RegisterKnownItemUuids(state, new ChestView { Items = new() { GearItem("HYPERION", uuid) } });
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), GearItem("HYPERION", uuid)));

        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(0);
    }

    [Test]
    public async Task NewTagAfterTradeWindowIsNotCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("You                  Someone", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("NECRON_HANDLE", 1)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(0);
    }

    private static Item OpenRewardChestButton() => new()
    {
        ItemName = "Open Reward Chest",
        Description = "Contents\nNecron's Handle\nWither Essence x54\n\nCost\n100,000,000 Coins\n\nClick to open!"
    };

    [Test]
    public async Task RewardChestPreview_DoesNotBlockTheRealUuidDrop()
    {
        // production 2026-10-01: three players paid 100M for a Bedrock chest holding Necron's Handle but
        // it was never counted - the chest GUI's container previews the loot (same tag AND uuid).
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("Bedrock", new() { GearItem("NECRON_HANDLE", uuid), OpenRewardChestButton() }, StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), GearItem("NECRON_HANDLE", uuid)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(1);
    }

    [Test]
    public async Task RewardChestPreview_DoesNotBlockTheRealStackableDrop()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("Bedrock", new() { StackableItem("NECRON_HANDLE", 1), OpenRewardChestButton() }, StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("NECRON_HANDLE", 1)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(1);
    }

    [Test]
    public async Task NonRewardContainerShowingTheTagStillBlocksIt_WithUuid()
    {
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("Craft Item", new() { GearItem("NECRON_HANDLE", uuid) }, StackableItem("ENCHANTED_COBBLESTONE", 64)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), GearItem("NECRON_HANDLE", uuid)));

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(0);
    }

    // ── Wholesale inventory swap (Rift entry/exit, profile switch) ──

    private static Item[] NormalInventory(string uuidSuffix = "") => new[]
    {
        StackableItem("SKYBLOCK_MENU", 1),
        GearItem("HYPERION", "hyperion-" + uuidSuffix),
        GearItem("ASPECT_OF_THE_VOID", "aotv-" + uuidSuffix),
        GearItem("INFERNO_ROD", "rod-" + uuidSuffix),
        GearItem("FLAMING_CHESTPLATE", "chest-" + uuidSuffix),
        StackableItem("ENCHANTED_COBBLESTONE", 64),
        StackableItem("ENCHANTED_DIAMOND", 32),
        StackableItem("ENCHANTED_COAL", 16),
        StackableItem("ENCHANTED_IRON", 8),
        StackableItem("ENCHANTED_GOLD", 4),
        StackableItem("ENCHANTED_LAPIS", 2),
    };

    private static Item[] RiftInventory() => new[]
    {
        StackableItem("SKYBLOCK_MENU", 1),
        StackableItem("HEMOVIBE", 64),
        StackableItem("VAMPIRIC_MELON", 20),
        StackableItem("BACTE_FRAGMENT", 8),
        StackableItem("FAKE_SHURIKEN", 256),
        StackableItem("COVEN_SEAL", 1),
        StackableItem("TIMITE", 3),
        StackableItem("WYLD_BOW", 1),
        StackableItem("RIFT_PRISM", 1),
    };

    [Test]
    public async Task RiftEntry_InventoryReplacement_IsNotCountedAsLoot()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), NormalInventory()));
        await ProcessView(state, View("", new(), RiftInventory()));

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task RiftExit_NormalInventoryWithNeverSeenUuidsReturning_IsNotCountedAsLoot()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), RiftInventory()));
        await ProcessView(state, View("", new(), NormalInventory()));

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task NormalPickupAmongTenItems_IsStillCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), NormalInventory()));
        await ProcessView(state, View("", new(), NormalInventory().Append(StackableItem("NECRON_HANDLE", 1)).ToArray()));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "NECRON_HANDLE", 1 } });
    }

    [Test]
    public async Task StackableCountChangeAmongTenItems_IsStillCounted()
    {
        var state = new StateObject();
        var previous = NormalInventory();
        var current = NormalInventory();
        current[5] = StackableItem("ENCHANTED_COBBLESTONE", 69);
        await ProcessView(state, View("", new(), previous));
        await ProcessView(state, View("", new(), current));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "ENCHANTED_COBBLESTONE", 5 } });
    }

    [Test]
    public async Task SmallInventoryFullyReplaced_IsNotASwap_AndKeepsFirstTimeDropBehaviour()
    {
        // below 4 distinct items the swap heuristic is off (a nearly empty inventory legitimately
        // changes completely, e.g. after selling everything and picking something up)
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("A_ITEM", 1), StackableItem("B_ITEM", 1), StackableItem("C_ITEM", 1)));
        await ProcessView(state, View("", new(), StackableItem("D_ITEM", 5), StackableItem("E_ITEM", 2), StackableItem("F_ITEM", 1)));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(
            new Dictionary<string, int> { { "D_ITEM", 5 }, { "E_ITEM", 2 }, { "F_ITEM", 1 } });
    }

    [Test]
    public void IsWholesaleInventorySwap_ThresholdIsTenPercentOfPrevious()
    {
        var previous = Enumerable.Range(0, 10).Select(i => "tag:P" + i).ToHashSet();
        var other = new[] { "tag:X1", "tag:X2", "tag:X3" };

        var oneKept = previous.Take(1).Concat(other).ToHashSet();
        CollectionListener.IsWholesaleInventorySwap(previous, oneKept, out var overlap1).Should().BeTrue();
        overlap1.Should().Be(1);

        var twoKept = previous.Take(2).Concat(other).ToHashSet();
        CollectionListener.IsWholesaleInventorySwap(previous, twoKept, out var overlap2).Should().BeFalse();
        overlap2.Should().Be(2);

        CollectionListener.IsWholesaleInventorySwap(previous, new HashSet<string>(), out _).Should().BeFalse("an empty current inventory is not a swap");
        CollectionListener.IsWholesaleInventorySwap(previous.Take(3).ToHashSet(), other.ToHashSet(), out _).Should().BeFalse("fewer than 4 previous identities");
        CollectionListener.IsWholesaleInventorySwap(previous.Take(4).ToHashSet(), other.ToHashSet(), out _).Should().BeTrue();
    }

    [Test]
    public async Task CroesusMenuInDungeonHubPeriod_ResolvesPeriodToTheFloor()
    {
        // production 2026-10-01: Croesus claim periods stayed "Dungeon Hub" because the reading is
        // stamped mid-period and ResolveLocation rejects a floor reading after the period start.
        var state = new StateObject();
        var periodStart = DateTime.UtcNow.AddMinutes(-3);
        state.ExtractedInfo.CurrentLocation = "Dungeon Hub";
        state.ExtractedInfo.LastLocationChange = periodStart;
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1",
                Chest = CroesusMenu("Catacombs - Floor VII")
            }
        };

        args.AddService<ILogger<CollectionListener>>(new CapturingLogger());

        await new CollectionListener().Process(args);

        Tasks.DungeonRewardAttribution.ResolveLocation("Dungeon Hub", state.ExtractedInfo.LastDungeonFloor,
            state.ExtractedInfo.LastDungeonFloorAt, state.ExtractedInfo.LastLocationChange).Should().Be("The Catacombs (F7)");
    }

    [Test]
    public async Task CroesusMenuOutsideDungeonHub_KeepsTheNowStamp()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Garden";
        state.ExtractedInfo.LastLocationChange = DateTime.UtcNow.AddMinutes(-3);
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = CroesusMenu("Catacombs - Floor VII") }
        };

        args.AddService<ILogger<CollectionListener>>(new CapturingLogger());

        await new CollectionListener().Process(args);

        state.ExtractedInfo.LastDungeonFloorAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void SaturatingAccumulationClampsInsteadOfWrapping()
    {
        var counts = new Dictionary<string, int> { { "BAZAAR_PURCHASE", int.MaxValue - 5 }, { "DUNGEON_CHEST_COST", int.MinValue + 5 } };
        Tasks.ItemCountMath.Add(counts, "BAZAAR_PURCHASE", 100);
        Tasks.ItemCountMath.Add(counts, "DUNGEON_CHEST_COST", -100);
        Tasks.ItemCountMath.Add(counts, "NEW", int.MaxValue);
        counts["BAZAAR_PURCHASE"].Should().Be(int.MaxValue);
        counts["DUNGEON_CHEST_COST"].Should().Be(int.MinValue);
        counts["NEW"].Should().Be(int.MaxValue);
    }

    // ---- item creation time guard (pre-existing gear is not a drop) ----

    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Item GearItemCreatedAt(string tag, string uuid, object timestamp)
    {
        var item = GearItem(tag, uuid);
        item.ExtraAttributes["timestamp"] = timestamp;
        return item;
    }

    private static long UnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Test]
    public async Task UnknownUuidGearCreatedMonthsAgoIsNotCountedButRegistered()
    {
        var state = new StateObject();
        var uuid = Guid.NewGuid().ToString();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64),
            GearItemCreatedAt("HYPERION", uuid, UnixMs(Now.AddMonths(-4)))), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(0, "an old item was moved here, not dropped");
        CollectionListener.TryGetItemUuidHash(GearItem("HYPERION", uuid), out var hash);
        state.KnownItemUuids.Should().Contain(hash);
    }

    [Test]
    public async Task RewardChestLootCreatedHoursAgoIsStillCounted()
    {
        // a Croesus chest can be claimed long after the run - its loot must not fall to the age guard
        var state = new StateObject();
        var handle = GearItemCreatedAt("NECRON_HANDLE", Guid.NewGuid().ToString(), UnixMs(Now.AddHours(-20)));
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("Bedrock", new() { handle, OpenRewardChestButton() }, StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), handle), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("NECRON_HANDLE").Should().Be(1);
    }

    [Test]
    public async Task UnknownUuidGearCreatedOneMinuteAgoIsCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64),
            GearItemCreatedAt("HYPERION", Guid.NewGuid().ToString(), UnixMs(Now.AddMinutes(-1)))), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(1);
    }

    [Test]
    public async Task UnknownUuidGearWithoutTimestampIsCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64),
            GearItem("HYPERION", Guid.NewGuid().ToString())), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("HYPERION").Should().Be(1);
    }

    [Test]
    public void TryGetItemCreationTimeParsesTheSupportedShapes()
    {
        var expected = new DateTime(2026, 10, 1, 12, 4, 31, 884, DateTimeKind.Utc);
        var ms = UnixMs(expected);
        foreach (var raw in new object[]
        {
            ms, (double)ms, ms.ToString(), (int)(ms / 1000), (double)(ms / 1000), (ms / 1000).ToString(),
            new Newtonsoft.Json.Linq.JValue(ms),
            System.Text.Json.JsonDocument.Parse(ms.ToString()).RootElement,
            System.Text.Json.JsonDocument.Parse("\"" + ms + "\"").RootElement,
        })
        {
            CollectionListener.TryGetItemCreationTime(GearItemCreatedAt("X", "u", raw), out var created).Should().BeTrue(raw.GetType().Name);
            created.Should().BeCloseTo(expected, TimeSpan.FromSeconds(1), raw.GetType().Name);
            created.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [Test]
    public void TryGetItemCreationTimeParsesLegacyStringFormat()
    {
        CollectionListener.TryGetItemCreationTime(GearItemCreatedAt("X", "u", "6/27/19 2:05 PM"), out var created).Should().BeTrue();
        created.Should().Be(new DateTime(2019, 6, 27, 14, 5, 0, DateTimeKind.Utc));
        CollectionListener.TryGetItemCreationTime(GearItemCreatedAt("X", "u", "9/24/20 3:19 AM"), out created).Should().BeTrue();
        created.Should().Be(new DateTime(2020, 9, 24, 3, 19, 0, DateTimeKind.Utc));
    }

    [Test]
    public void TryGetItemCreationTimeRejectsMissingOrUnparseable()
    {
        CollectionListener.TryGetItemCreationTime(GearItem("X", "u"), out _).Should().BeFalse();
        CollectionListener.TryGetItemCreationTime(new Item(), out _).Should().BeFalse();
        foreach (var raw in new object[] { "garbage", "", true, -5L, 0L })
            CollectionListener.TryGetItemCreationTime(GearItemCreatedAt("X", "u", raw), out _).Should().BeFalse(raw.ToString());
    }

    // ---- transfer views (sack/bazaar/shop/trade) ----

    private static async Task<(StateObject State, CapturingLogger Logger)> SackNotification(
        StateObject state, DateTime receivedAt, string line = "Added items:\n +70,000 Enchanted Wheat (Sack)\nfiller\nfiller")
    {
        var itemsApi = new Moq.Mock<Coflnet.Sky.Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemNamesGetAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<Coflnet.Sky.Items.Client.Model.ItemPreview> { new() { Name = "Enchanted Wheat", Tag = "ENCHANTED_WHEAT" }, new() { Name = "Compost", Tag = "COMPOST" } });
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ReceivedAt = receivedAt, ChatBatch = [line] }
        };
        args.AddService(itemsApi.Object);
        var logger = new CapturingLogger();
        var listener = new CollectionListener();
        listener.SetLogger(logger);
        await listener.Process(args);
        return (state, logger);
    }

    [TestCase("Wheat Sack")]
    [TestCase("Bazaar Orders")]
    [TestCase("Wheat ➜ Coins")]
    [TestCase("Confirm Sell Offer")]
    public async Task SackNotificationRightAfterTransferViewIsIgnored(string viewName)
    {
        var state = new StateObject();
        await ProcessView(state, View(viewName, new(), StackableItem("ENCHANTED_WHEAT", 10)), Now);

        var (_, logger) = await SackNotification(state, Now.AddSeconds(30));

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message == $"Ignored sack change after transfer view for {state.PlayerId}: +70000 Enchanted Wheat");
    }

    [Test]
    public async Task SmallIgnoredSackNotificationIsNotLogged()
    {
        var state = new StateObject();
        await ProcessView(state, View("Wheat Sack", new(), StackableItem("ENCHANTED_WHEAT", 10)), Now);

        var (_, logger) = await SackNotification(state, Now.AddSeconds(5), "Added items:\n +64 Enchanted Wheat (Sack)\nfiller\nfiller");

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information);
    }

    [Test]
    public async Task SackNotificationTwoMinutesAfterTransferViewIsCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("Wheat Sack", new(), StackableItem("ENCHANTED_WHEAT", 10)), Now);

        await SackNotification(state, Now.AddMinutes(2));

        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_WHEAT").Should().Be(70000);
    }

    [Test]
    public async Task SackNotificationWithoutAnyTransferViewIsCounted()
    {
        var state = new StateObject();
        // default(DateTime) LastTransferViewAt must not count as recent even for a default ReceivedAt
        await SackNotification(state, default);
        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_WHEAT").Should().Be(70000);

        var plain = new StateObject();
        await ProcessView(plain, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(plain, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await SackNotification(plain, Now.AddSeconds(1));
        plain.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_WHEAT").Should().Be(70000);
    }

    [Test]
    public async Task TransferViewTimeIsRecordedOnEarlyReturnPathsAndWhenPreviousViewWasTransfer()
    {
        var state = new StateObject();
        // first view: no previous view -> early return path
        await ProcessView(state, View("Wheat Sack", new(), StackableItem("ENCHANTED_WHEAT", 10)), Now);
        state.ExtractedInfo.LastTransferViewAt.Should().Be(Now);

        // current view is plain, previous was the sack
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_WHEAT", 10)), Now.AddSeconds(3));
        state.ExtractedInfo.LastTransferViewAt.Should().Be(Now.AddSeconds(3));

        // plain after plain leaves it untouched
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_WHEAT", 10)), Now.AddSeconds(60));
        state.ExtractedInfo.LastTransferViewAt.Should().Be(Now.AddSeconds(3));
    }

    [Test]
    public async Task InventoryDiffAfterSackViewCountsNothing()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_WHEAT", 64), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("Wheat Sack", new(), StackableItem("ENCHANTED_WHEAT", 64), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        // wheat vanished completely, cobblestone increased (taken out of the sack)
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 164), StackableItem("ENCHANTED_SUGAR", 5)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task InventoryDiffAfterNpcShopViewCountsNothing()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("Farm Merchant Shop", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 10)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task InventoryDiffBetweenPlainViewsIsStillCounted()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 100)), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_COBBLESTONE").Should().Be(36);
    }

    private static async Task<CapturingLogger> ProcessViewLogged(StateObject state, ChestView chest, CraftRecipeCache? craftCache = null)
    {
        var logger = new CapturingLogger();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest, ReceivedAt = Now }
        };
        args.AddService<ILogger<CollectionListener>>(logger);
        if (craftCache != null)
            args.AddService(craftCache);
        await new RecentViewsUpdate().Process(args);
        await new CollectionListener().Process(args);
        return logger;
    }

    [TestCase("Order options")]
    [TestCase("How many do you want?")]
    [TestCase("How much do you want to pay?")]
    [TestCase("At what price are you selling?")]
    [TestCase("Bazaar ➜ Farming")]
    [TestCase("Bazaar")]
    public async Task RefundAfterBazaarSubMenuIsNotCounted(string viewName)
    {
        var state = new StateObject();
        await ProcessView(state, View(viewName, new(), StackableItem("SHARD_FLARE", 1)), Now);

        await ProcessView(state, View("", new(), StackableItem("SHARD_FLARE", 132)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    /// <summary>
    /// Production 2026-10-03 (nameeagleismy, a shard flipper): 699 Hideonwall and 468 Hideonfloor shards
    /// of a cancelled sell order arrived between "Critter Safari Entry" and the bare inventory and were
    /// booked as 160M coins of Critter Safari loot; a Safari run gives about 7 Hideonwall shards.
    /// </summary>
    [Test]
    public async Task BulkShardGainIsNotLoot()
    {
        var state = new StateObject();
        await ProcessView(state, View("Critter Safari Entry", new(), StackableItem("SHARD_HIDEONWALL", 3), StackableItem("HELIX_LOG", 10)), Now);

        await ProcessView(state, View("Crafting", new(), StackableItem("SHARD_HIDEONWALL", 702), StackableItem("SHARD_HIDEONFLOOR", 468), StackableItem("SHARD_GAZER", 6), StackableItem("HELIX_LOG", 110)), Now);

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "SHARD_GAZER", 6 }, { "HELIX_LOG", 100 } });
    }

    [Test]
    public async Task SameRefundAfterPlainInventoryIsIgnoredAndLogged()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("SHARD_FLARE", 1)), Now);

        var logger = await ProcessViewLogged(state, View("", new(), StackableItem("SHARD_FLARE", 132)));

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message == "Bulk inventory change for " + state.PlayerId + ": 131x SHARD_FLARE between views  -> ");
    }

    [Test]
    public async Task SmallChangesAreNotLoggedAsBulk()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("SHARD_FLARE", 1)), Now);

        var logger = await ProcessViewLogged(state, View("", new(), StackableItem("SHARD_FLARE", 64)));

        state.ItemsCollectedRecently.GetValueOrDefault("SHARD_FLARE").Should().Be(63);
        logger.Entries.Should().NotContain(e => e.Message.StartsWith("Bulk inventory change"));
    }

    [TestCase("Auction View")]
    [TestCase("BIN Auction View")]
    [TestCase("Auction House")]
    [TestCase("Auctions Browser")]
    [TestCase("Manage Auctions")]
    [TestCase("Create Auction")]
    [TestCase("Create BIN Auction")]
    [TestCase("Your Bids")]
    public async Task ItemsArrivingAfterAuctionViewAreNotCounted(string viewName)
    {
        var state = new StateObject();
        await ProcessView(state, View(viewName, new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);

        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("GIANTS_SWORD", 1)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task NewItemWhileCurrentViewIsAuctionViewIsNotCountedAsFirstTimeDrop()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);

        await ProcessView(state, View("BIN Auction View", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("GIANTS_SWORD", 1)), Now);
        state.ItemsCollectedRecently.Should().BeEmpty();

        // control: the same arrival in a plain view is a first-time drop
        var control = new StateObject();
        await ProcessView(control, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64)), Now);
        await ProcessView(control, View("", new(), StackableItem("ENCHANTED_COBBLESTONE", 64), StackableItem("GIANTS_SWORD", 1)), Now);
        control.ItemsCollectedRecently.GetValueOrDefault("GIANTS_SWORD").Should().Be(1);
    }

    [Test]
    public void AuctionAndBazaarViewsAreTransferViews()
    {
        CollectionListener.IsTransferView(View("Auction View", new())).Should().BeTrue();
        CollectionListener.IsTransferView(View("Order options", new())).Should().BeTrue();
        CollectionListener.IsTransferView(View("Skyblock Menu", new())).Should().BeFalse();
    }

    [Test]
    public async Task NegativeDiffOfItemsMovedIntoCurrentStorageViewIsNotBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("GRIFFIN_FEATHER", 128)), Now);

        await ProcessView(state, View("Ender Chest (2/4)", new() { StackableItem("GRIFFIN_FEATHER", 64) }, StackableItem("GRIFFIN_FEATHER", 64)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task NegativeDiffWithTagAbsentFromCurrentStorageContainerIsStillBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("GRIFFIN_FEATHER", 128)), Now);

        await ProcessView(state, View("Ender Chest (2/4)", new() { StackableItem("ENCHANTED_COBBLESTONE", 64) }, StackableItem("GRIFFIN_FEATHER", 64)), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("GRIFFIN_FEATHER").Should().Be(-64);
    }

    [TestCase("Sack of Sacks")]
    [TestCase("Bazaar ➜ Oddities")]
    [TestCase("BIN Auction View")]
    [TestCase("Trades")]
    [TestCase("(1/2) Hunting Box")]
    public async Task NegativeDiffWhileCurrentViewIsATransferViewIsNotBooked(string viewName)
    {
        var state = new StateObject();
        await ProcessView(state, View("Emissary Sisko", new(), StackableItem("MELON_BLOCK", 179)), Now);

        // deposited/sold before the transfer view's first upload - the container does not show the item
        await ProcessView(state, View(viewName, new(), StackableItem("MELON_BLOCK", 64)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task LootGainedBeforeOpeningATransferViewIsStillBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("(1/3) Loadouts", new(), StackableItem("ENCHANTED_POTATO", 10)), Now);

        await ProcessView(state, View("Bazaar ➜ Oddities", new(), StackableItem("ENCHANTED_POTATO", 488)), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_POTATO").Should().Be(478);
    }

    [TestCase("Hunting Box")]
    [TestCase("(1/2) Hunting Box")]
    public async Task ShardsWithdrawnFromTheHuntingBoxAreNotCounted(string viewName)
    {
        var state = new StateObject();
        await ProcessView(state, View(viewName, new(), StackableItem("SHARD_HOWLING_SPIRIT", 3)), Now);

        await ProcessView(state, View(viewName, new(), StackableItem("SHARD_HOWLING_SPIRIT", 67)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task ShardsLeavingThePlainInventoryAreNotBookedAsALoss()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("SHARD_APEX_DRAGON", 70), StackableItem("ENDER_PEARL", 16)), Now);

        // 64 shards right-clicked into the Hunting Box, 4 pearls thrown
        await ProcessView(state, View("Crafting", new(), StackableItem("SHARD_APEX_DRAGON", 6), StackableItem("ENDER_PEARL", 12)), Now);

        state.ItemsCollectedRecently.Should().NotContainKey("SHARD_APEX_DRAGON");
        state.ItemsCollectedRecently.GetValueOrDefault("ENDER_PEARL").Should().Be(-4);
    }

    [Test]
    public async Task ShardsDepositedIntoTheFusionBoxAreNotBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("Fusion Box", new(), StackableItem("SHARD_FLARE", 10)), Now);

        await ProcessView(state, View("Fusion Box", new(), StackableItem("SHARD_FLARE", 240)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    /// <summary>
    /// Production 2026-10-03: gems removed from the own gear in the Gemstone Grinder (Gudzikk: 3x
    /// PERFECT_ONYX_GEM, 17.8M coins each) and items taken out of a backpack whose view was not uploaded
    /// (tsguy: +160x GRIFFIN_FEATHER between "Storage" and "Greater Backpack") were booked as loot.
    /// </summary>
    [TestCase("Gemstone Grinder", "Gemstone Grinder")]
    [TestCase("Gemstone Grinder", "")]
    [TestCase("Storage", "Greater Backpack (Slot #16)")]
    [TestCase("Storage", "")]
    public async Task ItemsArrivingAfterASwapViewAreNotLoot(string swapView, string nextView)
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENDER_PEARL", 16)), Now);
        await ProcessView(state, View(swapView, new(), StackableItem("ENDER_PEARL", 16)), Now);

        await ProcessView(state, View(nextView, new(), StackableItem("ENDER_PEARL", 16), StackableItem("PERFECT_ONYX_GEM", 1), StackableItem("GRIFFIN_FEATHER", 160)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task GemPutIntoGearInTheGemstoneGrinderIsNotALoss()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("PERFECT_ONYX_GEM", 3)), Now);

        await ProcessView(state, View("Gemstone Grinder", new(), StackableItem("PERFECT_ONYX_GEM", 2)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task BulkChangesOfOtherItemsAreNotLogged()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_POTATO", 1)), Now);

        var logger = await ProcessViewLogged(state, View("", new(), StackableItem("ENCHANTED_POTATO", 400)));

        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_POTATO").Should().Be(399);
        logger.Entries.Should().NotContain(e => e.Message.StartsWith("Bulk inventory change"));
    }

    [Test]
    public async Task PositiveDiffWithCurrentStorageViewIsStillBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("GRIFFIN_FEATHER", 10)), Now);

        await ProcessView(state, View("Ender Chest (2/4)", new() { StackableItem("GRIFFIN_FEATHER", 64) }, StackableItem("GRIFFIN_FEATHER", 20)), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("GRIFFIN_FEATHER").Should().Be(10);
    }

    // ---- recipe (Supercraft) views: crafting converts items the player already had ----

    /// <summary>
    /// A SkyBlock recipe view: 54 container slots with the "Supercraft" button in slot 32, the ingredients
    /// from slot 10 (3x3 grid) and the result in slot 25 - the layout <see cref="RecipeUpdate"/> reads.
    /// </summary>
    private static ChestView RecipeView(string name, string resultTag, string[] ingredientTags, params Item[] inventory)
    {
        var container = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        // as uploaded in production 2026-10-02: no color code, no tag
        container[32] = new Item { ItemName = "Supercraft", Count = 1 };
        container[25] = StackableItem(resultTag, 1);
        var slots = new[] { 10, 11, 12, 19, 20, 21, 28, 29, 30 };
        for (var i = 0; i < ingredientTags.Length; i++)
            container[slots[i]] = StackableItem(ingredientTags[i], 1);
        return View(name, container, inventory);
    }

    private static ChestView PlainMenu(params Item[] inventory)
        => View("Some Menu", Enumerable.Range(0, 54).Select(_ => new Item()).ToList(), inventory);

    [Test]
    public async Task CraftedStackableOutputAfterRecipeViewIsNotAGain()
    {
        // production: 30M coins of ENCHANTED_COMPOST booked between "Compost Bundle" and the bazaar
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COMPOST", 6)), Now);
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"], StackableItem("ENCHANTED_COMPOST", 6)), Now);
        await ProcessView(state, View("Bazaar ➜ Oddities", new(), StackableItem("ENCHANTED_COMPOST", 21)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task CraftedOutputIsLoggedAsIgnored()
    {
        var state = new StateObject();
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"], StackableItem("ENCHANTED_COMPOST", 6)), Now);
        var logger = await ProcessViewLogged(state, View("", new(), StackableItem("ENCHANTED_COMPOST", 21)));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message == $"Ignored crafted 15x ENCHANTED_COMPOST for {state.PlayerId} after recipe view Compost Bundle");
    }

    [Test]
    public async Task CraftIngredientsLeavingTheInventoryAfterRecipeViewAreNotALoss()
    {
        var state = new StateObject();
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"],
            StackableItem("COMPOST", 320), StackableItem("ENCHANTED_COMPOST", 1)), Now);
        await ProcessView(state, View("", new(), StackableItem("COMPOST", 160), StackableItem("ENCHANTED_COMPOST", 2)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task CraftedNonStackableResultAfterRecipeViewIsNotAGain()
    {
        var state = new StateObject();
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();
        await ProcessView(state, RecipeView("Hyperion", "HYPERION", ["WITHER_BLOOD"], GearItem("HYPERION", uuidA)), Now);
        // a never seen uuid without creation time counts as a drop in a plain diff
        await ProcessView(state, View("", new(), GearItem("HYPERION", uuidA), GearItem("HYPERION", uuidB)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task OtherLootInTheSameDiffAsACraftStillCounts()
    {
        var state = new StateObject();
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"],
            StackableItem("COMPOST", 320), StackableItem("ENCHANTED_COMPOST", 1), StackableItem("WHEAT", 1)), Now);
        await ProcessView(state, View("", new(), StackableItem("COMPOST", 160), StackableItem("ENCHANTED_COMPOST", 2), StackableItem("WHEAT", 65)), Now);

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { ["WHEAT"] = 64 });
    }

    [Test]
    public async Task SameGainAfterANonRecipeViewIsStillBooked()
    {
        var state = new StateObject();
        await ProcessView(state, PlainMenu(StackableItem("ENCHANTED_COMPOST", 6)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COMPOST", 21)), Now);

        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_COMPOST").Should().Be(15);
    }

    private const string CraftSackLine = "Removed items:\n -3,360 Compost (Sack)\n +70,000 Enchanted Wheat (Sack)\nfiller\nfiller";

    [Test]
    public async Task SackIngredientLossWhileRecipeViewIsStillOpenIsNotBooked()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COMPOST", 6)), Now);
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"], StackableItem("ENCHANTED_COMPOST", 6)), Now);

        var (_, logger) = await SackNotification(state, Now.AddMinutes(2), CraftSackLine);

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { ["ENCHANTED_WHEAT"] = 70000 });
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message == $"Ignored sack change after craft for {state.PlayerId}: -3360 Compost");
    }

    [TestCase(30, false, TestName = "Sack ingredient loss within the window after the view following the recipe view is not booked")]
    [TestCase(120, true, TestName = "Sack ingredient loss two minutes after the view following the recipe view is booked")]
    public async Task SackIngredientLossAfterRecipeViewWindow(int seconds, bool booked)
    {
        var state = new StateObject();
        await ProcessView(state, RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST"], StackableItem("ENCHANTED_COMPOST", 6)), Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_COMPOST", 6)), Now);

        await SackNotification(state, Now.AddSeconds(seconds), CraftSackLine);

        state.ItemsCollectedRecently.GetValueOrDefault("COMPOST").Should().Be(booked ? -3360 : 0);
        state.ItemsCollectedRecently.GetValueOrDefault("ENCHANTED_WHEAT").Should().Be(70000);
    }

    [Test]
    public void TryGetRecipeReadsResultAndIngredients()
    {
        var view = RecipeView("Compost Bundle", "ENCHANTED_COMPOST", ["COMPOST", "WHEAT"]);

        CollectionListener.TryGetRecipe(view, out var result, out var ingredients).Should().BeTrue();
        result.Should().Be("ENCHANTED_COMPOST");
        ingredients.Should().BeEquivalentTo(new[] { "COMPOST", "WHEAT" });
    }

    /// <summary>
    /// Production 2026-10-02: the mod for current Minecraft versions uploads the button as "Supercraft",
    /// older ones as "§aSupercraft". Matching only the colored name recognised no recipe view at all.
    /// </summary>
    [TestCase("Supercraft")]
    [TestCase("§aSupercraft")]
    public void TryGetRecipeAcceptsTheButtonWithAndWithoutColorCode(string buttonName)
    {
        var view = RecipeView("Highlite", "HIGHLITE", ["YOUNGITE", "TIMITE", "OBSOLITE"]);
        view.Items[32].ItemName = buttonName;

        CollectionListener.TryGetRecipe(view, out var result, out var ingredients).Should().BeTrue();
        result.Should().Be("HIGHLITE");
        ingredients.Should().BeEquivalentTo(new[] { "YOUNGITE", "TIMITE", "OBSOLITE" });
    }

    /// <summary>
    /// Production 2026-10-03 (Skiller0709, "Enchanted Melon Slice"): a recipe view uploaded without the
    /// Supercraft button. Crafts after such an upload were booked as loot (gtulol: 64x HEAVY_GABAGOOL).
    /// </summary>
    [Test]
    public async Task RecipeViewWithoutTheSupercraftButtonIsStillARecipeView()
    {
        var recipe = RecipeView("Enchanted Melon Slice", "ENCHANTED_MELON", ["MELON"], StackableItem("ENCHANTED_MELON", 6));
        recipe.Items[32] = new Item { Count = 0 };
        recipe.Items[23] = new Item { ItemName = "Crafting Table", Count = 1, Description = "Craft this recipe by using a crafting\ntable or Supercraft." };
        CollectionListener.TryGetRecipe(recipe, out var result, out var ingredients).Should().BeTrue();
        result.Should().Be("ENCHANTED_MELON");
        ingredients.Should().BeEquivalentTo(new[] { "MELON" });
        RecipeUpdate.ReadRecipe(recipe).Should().BeNull("the requirements are on the button");

        var state = new StateObject();
        await ProcessView(state, recipe, Now);
        await ProcessView(state, View("", new(), StackableItem("ENCHANTED_MELON", 70)), Now);

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public void TryGetRecipeRejectsOtherViews()
    {
        var realCraftingTable = RecipeView("x", "A", ["B"]);
        realCraftingTable.Items[32] = new Item();
        realCraftingTable.Items[23] = new Item { ItemName = "Crafting Table", Tag = "WORKBENCH", Count = 1 };
        CollectionListener.TryGetRecipe(realCraftingTable, out _, out _).Should().BeFalse();
        CollectionListener.TryGetRecipe(PlainMenu(), out _, out _).Should().BeFalse();
        CollectionListener.TryGetRecipe(new ChestView { Items = Enumerable.Range(0, 89).Select(_ => new Item()).ToList() }, out _, out _).Should().BeFalse();
        CollectionListener.TryGetRecipe(new ChestView { Items = null }, out _, out _).Should().BeFalse();
        CollectionListener.TryGetRecipe(null, out _, out _).Should().BeFalse();
        var noResult = RecipeView("x", "A", ["B"]);
        noResult.Items[25] = new Item();
        CollectionListener.TryGetRecipe(noResult, out _, out _).Should().BeFalse();
    }

    // ---- crafting table: a craft is a conversion, the output is no loot ----

    private static CraftRecipeCache CacheWith(string result, int resultCount, params (string Tag, int Count)[] ingredients)
    {
        var cache = new CraftRecipeCache((Func<System.Threading.Tasks.Task<IEnumerable<Recipe>>>?)null);
        cache.Add(MakeRecipe(result, resultCount, DateTime.UtcNow, ingredients));
        return cache;
    }

    private static Recipe MakeRecipe(string result, int resultCount, DateTime updated, params (string Tag, int Count)[] ingredients) => new()
    {
        Tag = result,
        ResultCount = resultCount,
        LastUpdated = updated,
        Requirements = new(),
        Ingredients = ingredients.Select(i => new KeyValuePair<string?, int>(i.Tag, i.Count)).ToList()
    };

    private static CraftRecipeCache BraidedCache() => CacheWith("BRAIDED_GRIFFIN_FEATHER", 1, ("GRIFFIN_FEATHER", 160), ("SOUL_STRING", 256));

    private static async Task<(StateObject State, CapturingLogger Logger)> CraftAt(string previousViewName, Item[] before, Item[] after, string nextViewName, CraftRecipeCache? cache)
    {
        var state = new StateObject();
        var table = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        await ProcessView(state, View("", new(), before), Now, cache);
        await ProcessView(state, View(previousViewName, table, before), Now, cache);
        var logger = await ProcessViewLogged(state, View(nextViewName, new(), after), cache);
        return (state, logger);
    }

    [TestCase("")]
    [TestCase("Bazaar ➜ Mining")]
    public async Task CraftAtCraftingTableIsNeitherLootNorLoss(string nextView)
    {
        // production 2026-10-03 (_I_am_better): +1 BRAIDED_GRIFFIN_FEATHER booked as 25.7M coins of Diana
        var (state, logger) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], nextView, BraidedCache());

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Message.StartsWith("Ignored crafted 1x BRAIDED_GRIFFIN_FEATHER"));
    }

    [Test]
    public async Task QuickCraftingIsDetectedToo()
    {
        var (state, _) = await CraftAt("Quick Crafting",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", BraidedCache());

        state.ItemsCollectedRecently.Should().BeEmpty();
    }

    [Test]
    public async Task PartlyRemainingIngredientsAreNotALoss()
    {
        var (state, logger) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 200), StackableItem("SOUL_STRING", 256)],
            [StackableItem("GRIFFIN_FEATHER", 40), StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", BraidedCache());

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Message.StartsWith("Ignored crafted 1x BRAIDED_GRIFFIN_FEATHER"));
    }

    [Test]
    public async Task OutputWithoutShrinkingIngredientsIsLoot()
    {
        var (state, _) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256), StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", BraidedCache());

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "BRAIDED_GRIFFIN_FEATHER", 1 } });
    }

    [Test]
    public async Task GainBeyondWhatTheIngredientsExplainIsLoot()
    {
        var (state, _) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 3)], "", BraidedCache());

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "BRAIDED_GRIFFIN_FEATHER", 2 } });
    }

    [Test]
    public async Task UnknownRecipeIsBookedAsBefore()
    {
        var (state, _) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", new CraftRecipeCache((Func<System.Threading.Tasks.Task<IEnumerable<Recipe>>>?)null));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "BRAIDED_GRIFFIN_FEATHER", 1 } });
    }

    [Test]
    public async Task SameChangeAfterANonCraftingViewIsBookedAsBefore()
    {
        var (state, _) = await CraftAt("",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", BraidedCache());

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "BRAIDED_GRIFFIN_FEATHER", 1 } });
    }

    [Test]
    public async Task CraftedNonStackableItemIsNotBooked()
    {
        var cache = CacheWith("HYPERION", 1, ("WITHER_BLOOD", 8), ("NECRON_HANDLE", 1));
        var (state, logger) = await CraftAt("Craft Item",
            [StackableItem("WITHER_BLOOD", 8), StackableItem("NECRON_HANDLE", 1)],
            [GearItem("HYPERION", Guid.NewGuid().ToString())], "", cache);

        state.ItemsCollectedRecently.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Message.StartsWith("Ignored crafted 1x HYPERION"));
    }

    [Test]
    public async Task CraftWithoutCacheServiceBehavesAsBefore()
    {
        var (state, _) = await CraftAt("Craft Item",
            [StackableItem("GRIFFIN_FEATHER", 160), StackableItem("SOUL_STRING", 256)],
            [StackableItem("BRAIDED_GRIFFIN_FEATHER", 1)], "", null);

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { { "BRAIDED_GRIFFIN_FEATHER", 1 } });
    }

    [Test]
    public void DetectionClaimsIngredientsOnlyOnce()
    {
        var recipes = new Dictionary<string, IReadOnlyList<CraftRecipe>>
        {
            ["A"] = new[] { new CraftRecipe(new() { { "X", 10 } }, 1, DateTime.UtcNow) },
            ["B"] = new[] { new CraftRecipe(new() { { "X", 10 } }, 1, DateTime.UtcNow) },
        };
        var (output, consumed) = CraftDetection.Detect(new Dictionary<string, int> { { "X", 10 } },
            new Dictionary<string, int> { { "A", 1 }, { "B", 1 } }, t => recipes.GetValueOrDefault(t) ?? Array.Empty<CraftRecipe>());

        output.Should().BeEquivalentTo(new Dictionary<string, int> { { "A", 1 } });
        consumed.Should().BeEquivalentTo(new Dictionary<string, int> { { "X", 10 } });
    }

    [Test]
    public void DetectionUsesTheNewestFittingVariantAndResultCount()
    {
        var recipes = new[]
        {
            new CraftRecipe(new() { { "X", 20 } }, 2, DateTime.UtcNow),
            new CraftRecipe(new() { { "Y", 5 } }, 1, DateTime.UtcNow.AddDays(-1)),
        };
        var (output, consumed) = CraftDetection.Detect(new Dictionary<string, int> { { "X", 40 }, { "Y", 5 } },
            new Dictionary<string, int> { { "X", 0 }, { "Y", 5 }, { "A", 5 } }, _ => recipes);

        // 5 gained, 2 per craft: 3 crafts would need 60 X, only 2 are possible -> 4 crafted
        output.Should().BeEquivalentTo(new Dictionary<string, int> { { "A", 4 } });
        consumed.Should().BeEquivalentTo(new Dictionary<string, int> { { "X", 40 } });
    }

    [Test]
    public void CacheCollapsesVariantsAndOrdersNewestFirst()
    {
        var cache = new CraftRecipeCache((Func<System.Threading.Tasks.Task<IEnumerable<Recipe>>>?)null);
        var old = DateTime.UtcNow.AddDays(-3);
        cache.Add(MakeRecipe("R", 1, old, ("A", 2), ("B", 1)));
        cache.Add(MakeRecipe("R", 1, DateTime.UtcNow, ("B", 1), ("A", 1), ("A", 1))); // same totals, newer
        cache.Add(MakeRecipe("R", 1, DateTime.UtcNow.AddDays(-1), ("C", 4)));
        cache.Add(MakeRecipe("R", 1, DateTime.UtcNow, ("R", 1), ("A", 1))); // contains the result
        cache.Add(MakeRecipe("R", 1, DateTime.UtcNow)); // empty

        var list = cache.Get("R");
        list.Should().HaveCount(2);
        list[0].Ingredients.Should().BeEquivalentTo(new Dictionary<string, int> { { "A", 2 }, { "B", 1 } });
        list[1].Ingredients.Should().BeEquivalentTo(new Dictionary<string, int> { { "C", 4 } });
        list[0].LastUpdated.Should().BeAfter(list[1].LastUpdated);
    }

    [Test]
    public async Task CacheGetBeforeLoadIsEmptyAndLoadsInTheBackground()
    {
        var release = new System.Threading.Tasks.TaskCompletionSource<IEnumerable<Recipe>>();
        var cache = new CraftRecipeCache(() => release.Task);

        cache.Get("R").Should().BeEmpty();
        release.SetResult(new[] { MakeRecipe("R", 1, DateTime.UtcNow, ("A", 1)) });
        for (var i = 0; i < 100 && cache.Get("R").Count == 0; i++)
            await Task.Delay(20);

        cache.Get("R").Should().ContainSingle();
    }

    [Test]
    public async Task CacheWithFailingLoaderDoesNotThrow()
    {
        var calls = 0;
        var cache = new CraftRecipeCache(() => { calls++; throw new InvalidOperationException("cassandra down"); });

        cache.Get("R").Should().BeEmpty();
        await Task.Delay(100);
        cache.Get("R").Should().BeEmpty();
        await cache.LoadInternal();
        calls.Should().BeGreaterThan(0);
    }

    [Test]
    public void CacheWithoutRecipeServiceDoesNotThrow()
    {
        new CraftRecipeCache((RecipeService?)null, null).Get("R").Should().BeEmpty();
    }
}
