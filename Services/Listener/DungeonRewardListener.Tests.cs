using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class DungeonRewardChestParserTests
{
    private static ChestView BuildRewardChestView(string chestName, string costLine, List<string> contentsLines = null, string keyLine = null, string itemNamePrefix = "")
    {
        contentsLines ??= ["Some Reward Item"];
        var costSection = new List<string>();
        if (keyLine != null) costSection.Add(keyLine);
        if (costLine != null) costSection.Add(costLine);
        var description = "Contents\n" + string.Join("\n", contentsLines)
            + "\n\nCost\n" + string.Join("\n", costSection) + "\n\nClick to open!";
        return new ChestView
        {
            Name = chestName,
            Items = new List<Item> { new() { ItemName = itemNamePrefix + "Open Reward Chest", Description = description } }
        };
    }

    [Test]
    public void BedrockChest_WithColorCodedCoinsLine_ParsesCost()
    {
        var chest = BuildRewardChestView("Bedrock Chest", "§62,000,000 Coins");

        DungeonRewardChestParser.TryParse(chest, "The Catacombs (M7)", out var info).Should().BeTrue();

        info.ChestType.Should().Be("Bedrock Chest");
        info.CostCoins.Should().Be(2_000_000);
        info.IsDungeonChest.Should().BeTrue();
    }

    [Test]
    public void WoodChest_Free_ParsesToZeroCost()
    {
        var chest = BuildRewardChestView("Wood Chest", "FREE");

        DungeonRewardChestParser.TryParse(chest, "The Catacombs (F1)", out var info).Should().BeTrue();

        info.CostCoins.Should().Be(0);
        info.IsDungeonChest.Should().BeTrue();
    }

    [Test]
    public void CostSectionWithAKeyLinePlusCoins_ParsesTheCoinsAndIgnoresTheKey()
    {
        var chest = BuildRewardChestView("Diamond Chest", "500,000 Coins", keyLine: "Dungeon Chest Key");

        DungeonRewardChestParser.TryParse(chest, "The Catacombs (F3)", out var info).Should().BeTrue();

        info.CostCoins.Should().Be(500_000);
    }

    [Test]
    public void KuudraPaidChest_ParsesWithZeroCost_AndIsNotADungeonChest()
    {
        // Real payload sample from a production Kuudra chest (the dungeon one reuses the same button).
        var chest = BuildRewardChestView("Paid Chest", costLine: null, keyLine: "Burning Kuudra Key",
            contentsLines: ["Wither Spectre Shard", "Kuudra Tentacle", "§dCrimson Essence §8x500", "§5Kuudra Teeth §8x4", "Kraken Shard"],
            itemNamePrefix: "§a");

        DungeonRewardChestParser.TryParse(chest, null, out var info).Should().BeTrue();

        info.ChestType.Should().Be("Paid Chest");
        info.CostCoins.Should().Be(0, "Kuudra chests must never be charged coins");
        info.IsDungeonChest.Should().BeFalse();
        info.Contents.Should().HaveCount(5);
        info.Contents.Should().Contain("Crimson Essence x500");
    }

    [TestCase("Wardrobe")]
    [TestCase("Ender Chest")]
    public void NonRewardChest_ReturnsFalse(string chestName)
    {
        var chest = new ChestView
        {
            Name = chestName,
            Items = new List<Item> { new() { ItemName = "Some Other Item", Description = "nothing to see here" } }
        };

        DungeonRewardChestParser.TryParse(chest, null, out _).Should().BeFalse();
    }

    [Test]
    public void ObsidianChest_NoParseableCoinLine_FallsBackToF1Cost()
    {
        var chest = BuildRewardChestView("Obsidian Chest", costLine: null);

        DungeonRewardChestParser.TryParse(chest, "The Catacombs (F1)", out var info).Should().BeTrue();

        info.CostCoins.Should().Be(250_000);
    }

    [Test]
    public void ObsidianChest_NoParseableCoinLine_OnM5_FallsBackTo1Million()
    {
        var chest = BuildRewardChestView("Obsidian Chest", costLine: null);

        DungeonRewardChestParser.TryParse(chest, "The Catacombs (M5)", out var info).Should().BeTrue();

        info.CostCoins.Should().Be(1_000_000);
    }

    [Test]
    public void BedrockChest_NoParseableCoinLine_FallsBackTo2Million_RegardlessOfFloor()
    {
        var chest = BuildRewardChestView("Bedrock Chest", costLine: null);

        DungeonRewardChestParser.TryParse(chest, null, out var info).Should().BeTrue();

        info.CostCoins.Should().Be(2_000_000);
    }
}

/// <summary>
/// Uses the existing MockedUpdateArgs test setup (see KuudraListener.Tests.cs/ShensListener.Tests.cs)
/// for building UpdateArgs sequences against a shared StateObject.
/// </summary>
public class DungeonRewardListenerTests
{
    private static ChestView BuildRewardChestView(string chestName, string costLine)
    {
        var description = "Contents\nSome Reward Item\n\nCost\n" + costLine + "\n\nClick to open!";
        return new ChestView
        {
            Name = chestName,
            Items = new List<Item> { new() { ItemName = "Open Reward Chest", Description = description } }
        };
    }

    private static MockedUpdateArgs InventoryUpdate(StateObject state, string chestName, string costLine, string playerId = "p1")
    {
        return new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY,
                PlayerId = playerId,
                Chest = BuildRewardChestView(chestName, costLine)
            }
        };
    }

    private static MockedUpdateArgs ScoreboardUpdate(StateObject state, long purse, string playerId = "p1")
    {
        return new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Scoreboard,
                PlayerId = playerId,
                Scoreboard = [$"Purse: {purse.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}"]
            }
        };
    }

    [Test]
    public async Task PurseDropConfirmation_ChargesExactlyTheCostOnce()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = 10_000_000;
        var listener = new DungeonRewardListener();

        await listener.Process(InventoryUpdate(state, "Gold Chest", "250,000 Coins"));
        state.ExtractedInfo.PendingDungeonChestCharge.Should().NotBeNull();

        await listener.Process(ScoreboardUpdate(state, 9_750_000));

        state.ItemsCollectedRecently.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST).Should().Be(-250_000);
        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull();

        // a further scoreboard tick at the same purse must not charge again
        await listener.Process(ScoreboardUpdate(state, 9_750_000));
        state.ItemsCollectedRecently.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST).Should().Be(-250_000);
    }

    [Test]
    public async Task NoPurseDrop_NothingCharged()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = 10_000_000;
        var listener = new DungeonRewardListener();

        await listener.Process(InventoryUpdate(state, "Gold Chest", "250,000 Coins"));
        await listener.Process(ScoreboardUpdate(state, 10_000_000)); // purse unchanged

        state.ItemsCollectedRecently.Should().NotContainKey(PseudoItems.DUNGEON_CHEST_COST);
        state.ExtractedInfo.PendingDungeonChestCharge.Should().NotBeNull("the charge is still awaiting a confirming purse drop");
    }

    [Test]
    public async Task TwoChestsInARow_BothCharged()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = 10_000_000;
        var listener = new DungeonRewardListener();

        await listener.Process(InventoryUpdate(state, "Gold Chest", "100,000 Coins"));

        // purse already reflects chest 1 being bought by the time chest 2's GUI is seen (no separate
        // scoreboard tick in between) - the pending charge for chest 1 must be settled against this
        // before it is replaced by chest 2's pending charge (see DungeonRewardListener.HandleInventory).
        state.ExtractedInfo.Purse = 9_900_000;
        await listener.Process(InventoryUpdate(state, "Diamond Chest", "200,000 Coins"));

        state.ItemsCollectedRecently.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST).Should().Be(-100_000,
            "chest 1 should have been settled against the known purse before being replaced");
        state.ExtractedInfo.PendingDungeonChestCharge.Should().NotBeNull("chest 2 command is still pending");

        await listener.Process(ScoreboardUpdate(state, 9_700_000));

        state.ItemsCollectedRecently.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST).Should().Be(-300_000,
            "both chests should have ended up charged");
        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull();
    }

    [Test]
    public async Task PendingChargeExpiry_DropsWithoutCharging()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = 10_000_000;
        var listener = new DungeonRewardListener();

        await listener.Process(InventoryUpdate(state, "Gold Chest", "100,000 Coins"));
        state.ExtractedInfo.PendingDungeonChestCharge.Should().NotBeNull();
        // simulate more than 2 minutes passing without a confirming scoreboard tick
        state.ExtractedInfo.PendingDungeonChestCharge.SeenAt = DateTime.UtcNow.AddMinutes(-5);

        // even a purse drop that would otherwise confirm the charge must not fire once expired
        await listener.Process(ScoreboardUpdate(state, 9_000_000));

        state.ItemsCollectedRecently.Should().NotContainKey(PseudoItems.DUNGEON_CHEST_COST);
        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull("an expired pending charge is dropped, not charged");
    }

    /// <summary>Captures every log call so tests can assert on level/message without a mocking library.</summary>
    private sealed class CapturingLogger : ILogger
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
    public async Task KuudraChestIsNeverTreatedAsADungeonChestEvenWithAStaleFloor()
    {
        // Production: 92/894 Kuudra "Dungeon reward chest ... seen" lines were mislabelled with a
        // stale Catacombs floor because the player had been in a dungeon earlier - Kuudra chests must
        // never be charged and must never carry a floor.
        var state = new StateObject();
        state.ExtractedInfo.Purse = 10_000_000;
        state.ExtractedInfo.LastDungeonFloor = "The Catacombs (F3)";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);

        var args = InventoryUpdate(state, "Paid Chest", "0 Coins");
        await listener.Process(args);

        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull("Kuudra chests are never charged/pending, regardless of LastDungeonFloor");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information && e.Message.Contains("Dungeon reward chest"),
            "Kuudra chests must not be logged as a dungeon reward chest at Information");
        var debugEntry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("Kuudra")).Which;
        debugEntry.Message.Should().NotContain("F3", "a Kuudra chest must never carry a (possibly stale) dungeon floor");
    }

    private static MockedUpdateArgs ContainerUpdate(StateObject state, string title, List<Item> items, string playerId = "p1") =>
        new()
        {
            currentState = state,
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY,
                PlayerId = playerId,
                Chest = new ChestView { Name = title, Items = items }
            }
        };

    [Test]
    public async Task DiscoveryLogFiresForAnUnrecognisedContainerOnADungeonFloor()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Catacombs (F7)";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);

        await listener.Process(ContainerUpdate(state, "Mystery Menu", new List<Item> { new() { ItemName = "§bSome Item" } }));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains("Dungeon area container") && e.Message.Contains("Mystery Menu") && e.Message.Contains("Some Item"));
    }

    [Test]
    public async Task DiscoveryLogIncludesFullLoreForRewardOrOpenNamedItems()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "Dungeon Hub";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);

        await listener.Process(ContainerUpdate(state, "Mystery Menu", new List<Item> {
            new() { ItemName = "§aOpen Reward Chest", Description = "§7Line one\n§7Line two" }
        }));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains("Open Reward Chest") && e.Message.Contains("Line one | Line two"));
    }

    [TestCase("Ender Chest")]
    [TestCase("Backpack (Slot 1)")]
    public async Task DiscoveryLogSkipsStorageContainers(string title)
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Catacombs (F7)";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);

        await listener.Process(ContainerUpdate(state, title, new List<Item> { new() { ItemName = "§bSome Item" } }));

        logger.Entries.Should().NotContain(e => e.Message.Contains("Dungeon area container"));
    }

    [Test]
    public async Task DiscoveryLogDoesNotFireOutsideDungeonFloorsOrHub()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "Hub";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);

        await listener.Process(ContainerUpdate(state, "Mystery Menu", new List<Item> { new() { ItemName = "§bSome Item" } }));

        logger.Entries.Should().NotContain(e => e.Message.Contains("Dungeon area container"));
    }

    [Test]
    public async Task DiscoveryLogThrottlesRepeatsOfTheSameTitleForTheSamePlayer()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = "The Catacombs (F7)";
        var logger = new CapturingLogger();
        var listener = new DungeonRewardListener();
        listener.SetLogger(logger);
        var items = new List<Item> { new() { ItemName = "§bSome Item" } };

        await listener.Process(ContainerUpdate(state, "Mystery Menu", items));
        await listener.Process(ContainerUpdate(state, "Mystery Menu", items));

        logger.Entries.Count(e => e.Message.Contains("Dungeon area container")).Should().Be(1,
            "a second view of the same title for the same player within the throttle window must not log again");

        // A different title is a different throttle key - must still log.
        await listener.Process(ContainerUpdate(state, "Another Menu", items));
        logger.Entries.Count(e => e.Message.Contains("Dungeon area container")).Should().Be(2);

        // A different player is also a different throttle key - must still log.
        await listener.Process(ContainerUpdate(state, "Mystery Menu", items, playerId: "p2"));
        logger.Entries.Count(e => e.Message.Contains("Dungeon area container")).Should().Be(3);
    }
}
