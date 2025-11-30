using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class PlayerElectionListenerTests
{
    [Test]
    public void ParseVotes_ValidItems_ReturnsDictionary()
    {
        // Arrange
        var items = new List<Item>
        {
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Future77", Description = "§7Votes: §b9,394" },
            new Item { ItemName = "§6[MVP§1++§6] 2nfg", Description = "§7Votes: §b4,604" },
            new Item { ItemName = "§b[MVP§5+§b] hannibal2", Description = "§7Votes: §b1,022" }
        };

        // Act
        var result = PlayerElectionListener.ParseVotes(items);

        // Assert
        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result["thirtyvirus"], Is.EqualTo(23166));
        Assert.That(result["Future77"], Is.EqualTo(9394));
        Assert.That(result["2nfg"], Is.EqualTo(4604));
        Assert.That(result["hannibal2"], Is.EqualTo(1022));
    }

    [Test]
    public void ParseVotes_FiltersNavigationItems_ReturnsOnlyPlayers()
    {
        // Arrange
        var items = new List<Item>
        {
            new Item { ItemName = "§e§l-->", Description = "§7The §a7 §7most-supported citizens will..." },
            new Item { ItemName = "§e§l<--", Description = "§7The §a7 §7most-supported citizens will..." },
            new Item { ItemName = " ", Description = "" },
            new Item { ItemName = "§cClose", Description = "" },
            new Item { ItemName = "§aLoading...", Description = "" },
            new Item { ItemName = "§aMinister Election", Description = "§7Traditional §bMayor §7elections..." },
            new Item { ItemName = "§aYour current vote", Description = "§7You have allocated §b1 §7votes towards..." },
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" }
        };

        // Act
        var result = PlayerElectionListener.ParseVotes(items);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result["thirtyvirus"], Is.EqualTo(23166));
    }

    [Test]
    public void ExtractPlayerName_YouTubeRank_ReturnsPlayerName()
    {
        // Arrange
        var itemName = "§c[§fYOUTUBE§c] thirtyvirus";

        // Act
        var result = PlayerElectionListener.ExtractPlayerName(itemName);

        // Assert
        Assert.That(result, Is.EqualTo("thirtyvirus"));
    }

    [Test]
    public void ExtractPlayerName_MVPPlusPlusRank_ReturnsPlayerName()
    {
        // Arrange
        var itemName = "§6[MVP§1++§6] 2nfg";

        // Act
        var result = PlayerElectionListener.ExtractPlayerName(itemName);

        // Assert
        Assert.That(result, Is.EqualTo("2nfg"));
    }

    [Test]
    public void ExtractPlayerName_MVPPlusRank_ReturnsPlayerName()
    {
        // Arrange
        var itemName = "§b[MVP§5+§b] hannibal2";

        // Act
        var result = PlayerElectionListener.ExtractPlayerName(itemName);

        // Assert
        Assert.That(result, Is.EqualTo("hannibal2"));
    }

    [Test]
    public void ExtractPlayerName_MVPCPlusRank_ReturnsPlayerName()
    {
        // Arrange
        var itemName = "§b[MVP§c+§b] fear5s";

        // Act
        var result = PlayerElectionListener.ExtractPlayerName(itemName);

        // Assert
        Assert.That(result, Is.EqualTo("fear5s"));
    }

    [Test]
    public void ParseUserVote_ValidItem_ReturnsVote()
    {
        // Arrange
        var items = new List<Item>
        {
            new Item 
            { 
                ItemName = "§aYour current vote", 
                Description = "§7You have allocated §b1 §7votes towards\n§7§b[MVP§5+§b] hannibal2§7."
            }
        };

        // Act
        var result = PlayerElectionListener.ParseUserVote(items);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.VotedFor, Is.EqualTo("hannibal2"));
        Assert.That(result.VoteCount, Is.EqualTo(1));
    }

    [Test]
    public void ParseUserVote_NoVoteItem_ReturnsNull()
    {
        // Arrange
        var items = new List<Item>
        {
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" }
        };

        // Act
        var result = PlayerElectionListener.ParseUserVote(items);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Process_PlayerElectionChest_StoresVotesAndUserVote()
    {
        // Arrange
        PlayerElectionEntry? capturedEntry = null;
        var service = new Mock<IPlayerElectionService>();
        service.Setup(x => x.StoreVotes(It.IsAny<PlayerElectionEntry>()))
            .Returns(Task.CompletedTask)
            .Callback<PlayerElectionEntry>(e => capturedEntry = e);

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = new DateTime(2025, 11, 30, 22, 58, 56, DateTimeKind.Utc),
                Chest = new ChestView()
                {
                    Name = "Player Election",
                    Items = new List<Item>()
                    {
                        new Item { ItemName = " ", Description = "" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Future77", Description = "§7Votes: §b9,394" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Derailious", Description = "§7Votes: §b5,525" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] im_a_squid_kid", Description = "§7Votes: §b5,042" },
                        new Item { ItemName = "§6[MVP§1++§6] 2nfg", Description = "§7Votes: §b4,604" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Candypat", Description = "§7Votes: §b4,576" },
                        new Item { ItemName = "§6[MVP§c++§6] Celestial_Milk", Description = "§7Votes: §b4,561" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Refraction", Description = "§7Votes: §b4,552" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] MrFaceplate", Description = "§7Votes: §b3,473" },
                        new Item { ItemName = "§b[MVP§c+§b] fear5s", Description = "§7Votes: §b3,156" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Intrests", Description = "§7Votes: §b1,988" },
                        new Item { ItemName = "§6[MVP§8++§6] lejimbo", Description = "§7Votes: §b1,439" },
                        new Item { ItemName = "§c[§fYOUTUBE§c] Toadstar0", Description = "§7Votes: §b1,433" },
                        new Item { ItemName = "§b[MVP§5+§b] hannibal2", Description = "§7Votes: §b1,022" },
                        new Item { ItemName = "§aYour current vote", Description = "§7You have allocated §b1 §7votes towards\n§7§b[MVP§5+§b] hannibal2§7." },
                        new Item { ItemName = "§cClose", Description = "" }
                    }
                }
            }
        };
        args.AddService<IPlayerElectionService>(service.Object);

        var listener = new PlayerElectionListener();

        // Act
        await listener.Process(args);

        // Assert - Check stored votes
        service.Verify(x => x.StoreVotes(It.IsAny<PlayerElectionEntry>()), Times.Once);
        Assert.That(capturedEntry, Is.Not.Null);
        Assert.That(capturedEntry!.Timestamp, Is.EqualTo(new DateTime(2025, 11, 30, 22, 58, 0, DateTimeKind.Utc)));
        Assert.That(capturedEntry.Votes, Has.Count.EqualTo(14));
        Assert.That(capturedEntry.Votes["thirtyvirus"], Is.EqualTo(23166));
        Assert.That(capturedEntry.Votes["hannibal2"], Is.EqualTo(1022));

        // Assert - Check user vote stored in state
        Assert.That(args.currentState.ExtractedInfo, Is.Not.Null);
        Assert.That(args.currentState.ExtractedInfo!.PlayerElectionVote, Is.Not.Null);
        Assert.That(args.currentState.ExtractedInfo.PlayerElectionVote!.VotedFor, Is.EqualTo("hannibal2"));
        Assert.That(args.currentState.ExtractedInfo.PlayerElectionVote.VoteCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Process_NotPlayerElectionChest_DoesNotStore()
    {
        // Arrange
        var service = new Mock<IPlayerElectionService>();
        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = DateTime.UtcNow,
                Chest = new ChestView()
                {
                    Name = "Some Other Chest",
                    Items = new List<Item>()
                }
            }
        };
        args.AddService<IPlayerElectionService>(service.Object);

        var listener = new PlayerElectionListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreVotes(It.IsAny<PlayerElectionEntry>()), Times.Never);
    }

    [Test]
    public async Task Process_MultipleUpdates_StoresEachEntry()
    {
        // Arrange
        var storedEntries = new List<PlayerElectionEntry>();
        var service = new Mock<IPlayerElectionService>();
        service.Setup(x => x.StoreVotes(It.IsAny<PlayerElectionEntry>()))
            .Returns(Task.CompletedTask)
            .Callback<PlayerElectionEntry>(e => storedEntries.Add(e));

        var listener = new PlayerElectionListener();

        var items1 = new List<Item>
        {
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" }
        };

        // First update
        var args1 = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = new DateTime(2025, 11, 30, 22, 58, 10, DateTimeKind.Utc),
                Chest = new ChestView() { Name = "Player Election", Items = items1 }
            }
        };
        args1.AddService<IPlayerElectionService>(service.Object);
        await listener.Process(args1);

        var items2 = new List<Item>
        {
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,200" }
        };

        // Second update (same minute)
        var args2 = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = new DateTime(2025, 11, 30, 22, 58, 45, DateTimeKind.Utc),
                Chest = new ChestView() { Name = "Player Election", Items = items2 }
            }
        };
        args2.AddService<IPlayerElectionService>(service.Object);
        await listener.Process(args2);

        // Assert - Both entries stored (database handles deduplication)
        Assert.That(storedEntries, Has.Count.EqualTo(2));
        Assert.That(storedEntries[0].Votes["thirtyvirus"], Is.EqualTo(23166));
        Assert.That(storedEntries[1].Votes["thirtyvirus"], Is.EqualTo(23200));
    }

    [Test]
    public void ParseVotes_AllTop14Players_ReturnsAllPlayers()
    {
        // Arrange - Full Player Election inventory from the example
        var items = new List<Item>
        {
            new Item { ItemName = " ", Description = "" },
            new Item { ItemName = "§e§l-->", Description = "§7The §a7 §7most-supported citizens will\n§7rise as §cMinisters §7and claim §dunique\n§dperks§7." },
            new Item { ItemName = "§c[§fYOUTUBE§c] thirtyvirus", Description = "§7Votes: §b23,166" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Future77", Description = "§7Votes: §b9,394" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Derailious", Description = "§7Votes: §b5,525" },
            new Item { ItemName = "§c[§fYOUTUBE§c] im_a_squid_kid", Description = "§7Votes: §b5,042" },
            new Item { ItemName = "§6[MVP§1++§6] 2nfg", Description = "§7Votes: §b4,604" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Candypat", Description = "§7Votes: §b4,576" },
            new Item { ItemName = "§6[MVP§c++§6] Celestial_Milk", Description = "§7Votes: §b4,561" },
            new Item { ItemName = "§e§l<--", Description = "§7The §a7 §7most-supported citizens will\n§7rise as §cMinisters §7and claim §dunique\n§dperks§7." },
            new Item { ItemName = "§c[§fYOUTUBE§c] Refraction", Description = "§7Votes: §b4,552" },
            new Item { ItemName = "§c[§fYOUTUBE§c] MrFaceplate", Description = "§7Votes: §b3,473" },
            new Item { ItemName = "§b[MVP§c+§b] fear5s", Description = "§7Votes: §b3,156" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Intrests", Description = "§7Votes: §b1,988" },
            new Item { ItemName = "§6[MVP§8++§6] lejimbo", Description = "§7Votes: §b1,439" },
            new Item { ItemName = "§c[§fYOUTUBE§c] Toadstar0", Description = "§7Votes: §b1,433" },
            new Item { ItemName = "§b[MVP§5+§b] hannibal2", Description = "§7Votes: §b1,022" },
            new Item { ItemName = "§aMinister Election", Description = "§7Traditional §bMayor §7elections are a\n§7thing of the past." },
            new Item { ItemName = "§aYour current vote", Description = "§7You have allocated §b1 §7votes towards\n§7§b[MVP§5+§b] hannibal2§7." },
            new Item { ItemName = "§cClose", Description = "" }
        };

        // Act
        var result = PlayerElectionListener.ParseVotes(items);

        // Assert
        Assert.That(result, Has.Count.EqualTo(14));
        Assert.That(result["thirtyvirus"], Is.EqualTo(23166));
        Assert.That(result["Future77"], Is.EqualTo(9394));
        Assert.That(result["Derailious"], Is.EqualTo(5525));
        Assert.That(result["im_a_squid_kid"], Is.EqualTo(5042));
        Assert.That(result["2nfg"], Is.EqualTo(4604));
        Assert.That(result["Candypat"], Is.EqualTo(4576));
        Assert.That(result["Celestial_Milk"], Is.EqualTo(4561));
        Assert.That(result["Refraction"], Is.EqualTo(4552));
        Assert.That(result["MrFaceplate"], Is.EqualTo(3473));
        Assert.That(result["fear5s"], Is.EqualTo(3156));
        Assert.That(result["Intrests"], Is.EqualTo(1988));
        Assert.That(result["lejimbo"], Is.EqualTo(1439));
        Assert.That(result["Toadstar0"], Is.EqualTo(1433));
        Assert.That(result["hannibal2"], Is.EqualTo(1022));
    }
}
