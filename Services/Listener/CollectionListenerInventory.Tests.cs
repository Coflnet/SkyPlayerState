using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
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

    private static async Task ProcessView(StateObject state, ChestView chest, DateTime receivedAt = default)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest, ReceivedAt = receivedAt }
        };
        // the swap guard logs through the DI logger, like production
        args.AddService<ILogger<CollectionListener>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionListener>.Instance);
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
            .ReturnsAsync(new List<Coflnet.Sky.Items.Client.Model.ItemPreview> { new() { Name = "Enchanted Wheat", Tag = "ENCHANTED_WHEAT" } });
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

    private static async Task<CapturingLogger> ProcessViewLogged(StateObject state, ChestView chest)
    {
        var logger = new CapturingLogger();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest, ReceivedAt = Now }
        };
        args.AddService<ILogger<CollectionListener>>(logger);
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

    [Test]
    public async Task SameRefundAfterPlainInventoryIsStillCountedAndLogged()
    {
        var state = new StateObject();
        await ProcessView(state, View("", new(), StackableItem("SHARD_FLARE", 1)), Now);

        var logger = await ProcessViewLogged(state, View("", new(), StackableItem("SHARD_FLARE", 132)));

        state.ItemsCollectedRecently.GetValueOrDefault("SHARD_FLARE").Should().Be(131);
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
}
