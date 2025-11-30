using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MayorAuraListenerTests
{
    [Test]
    public void ParseTotalCoinsRaised_ValidDescription_ReturnsAmount()
    {
        // Arrange
        var description = "§7Total Coins Raised: §69,052,941,873\n§7Your Participation: §60\n§7Your Gains: §60";

        // Act
        var result = MayorAuraListener.ParseTotalCoinsRaised(description);

        // Assert
        Assert.That(result, Is.EqualTo(9052941873L));
    }

    [Test]
    public void ParseTotalCoinsRaised_SmallAmount_ReturnsAmount()
    {
        // Arrange
        var description = "§7Total Coins Raised: §6123,456\n§7Your Participation: §60";

        // Act
        var result = MayorAuraListener.ParseTotalCoinsRaised(description);

        // Assert
        Assert.That(result, Is.EqualTo(123456L));
    }

    [Test]
    public void ParseTotalCoinsRaised_NoCommas_ReturnsAmount()
    {
        // Arrange
        var description = "§7Total Coins Raised: §6500\n§7Your Participation: §60";

        // Act
        var result = MayorAuraListener.ParseTotalCoinsRaised(description);

        // Assert
        Assert.That(result, Is.EqualTo(500L));
    }

    [Test]
    public void ParseTotalCoinsRaised_InvalidDescription_ReturnsNull()
    {
        // Arrange
        var description = "§7Some other description without coins raised";

        // Act
        var result = MayorAuraListener.ParseTotalCoinsRaised(description);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseTotalCoinsRaised_EmptyString_ReturnsNull()
    {
        // Arrange
        var description = "";

        // Act
        var result = MayorAuraListener.ParseTotalCoinsRaised(description);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Process_MayorAuraChest_StoresFundraisingData()
    {
        // Arrange
        FundraisingEntry? capturedEntry = null;
        var service = new Mock<IMayorAuraService>();
        service.Setup(x => x.StoreFundraising(It.IsAny<FundraisingEntry>()))
            .Returns(Task.CompletedTask)
            .Callback<FundraisingEntry>(e => capturedEntry = e);

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = new DateTime(2025, 11, 30, 22, 56, 18, DateTimeKind.Utc),
                Chest = new ChestView()
                {
                    Name = "Mayor Aura",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§6Fundraising",
                            Description = "§7Total Coins Raised: §69,052,941,873\n§7Your Participation: §60\n§7Your Gains: §60"
                        }
                    }
                }
            }
        };
        args.AddService<IMayorAuraService>(service.Object);

        var listener = new MayorAuraListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreFundraising(It.IsAny<FundraisingEntry>()), Times.Once);
        Assert.That(capturedEntry, Is.Not.Null);
        Assert.That(capturedEntry!.TotalCoinsRaised, Is.EqualTo(9052941873L));
        Assert.That(capturedEntry.Timestamp, Is.EqualTo(new DateTime(2025, 11, 30, 22, 56, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task Process_NotMayorAuraChest_DoesNotStore()
    {
        // Arrange
        var service = new Mock<IMayorAuraService>();
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
        args.AddService<IMayorAuraService>(service.Object);

        var listener = new MayorAuraListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreFundraising(It.IsAny<FundraisingEntry>()), Times.Never);
    }

    [Test]
    public async Task Process_MultipleUpdates_StoresEachEntry()
    {
        // Arrange
        var storedEntries = new List<FundraisingEntry>();
        var service = new Mock<IMayorAuraService>();
        service.Setup(x => x.StoreFundraising(It.IsAny<FundraisingEntry>()))
            .Returns(Task.CompletedTask)
            .Callback<FundraisingEntry>(e => storedEntries.Add(e));

        var listener = new MayorAuraListener();

        // First update at 22:56:18
        var args1 = CreateMayorAuraArgs(service.Object, new DateTime(2025, 11, 30, 22, 56, 18, DateTimeKind.Utc), 9000000000L);
        await listener.Process(args1);

        // Second update at 22:56:45 (same minute, different amount)
        var args2 = CreateMayorAuraArgs(service.Object, new DateTime(2025, 11, 30, 22, 56, 45, DateTimeKind.Utc), 9050000000L);
        await listener.Process(args2);

        // Third update at 22:57:10 (different minute)
        var args3 = CreateMayorAuraArgs(service.Object, new DateTime(2025, 11, 30, 22, 57, 10, DateTimeKind.Utc), 9100000000L);
        await listener.Process(args3);

        // Assert
        Assert.That(storedEntries, Has.Count.EqualTo(3));
        // First two entries should have same timestamp (same minute)
        Assert.That(storedEntries[0].Timestamp, Is.EqualTo(new DateTime(2025, 11, 30, 22, 56, 0, DateTimeKind.Utc)));
        Assert.That(storedEntries[0].TotalCoinsRaised, Is.EqualTo(9000000000L));
        Assert.That(storedEntries[1].Timestamp, Is.EqualTo(new DateTime(2025, 11, 30, 22, 56, 0, DateTimeKind.Utc)));
        Assert.That(storedEntries[1].TotalCoinsRaised, Is.EqualTo(9050000000L));
        Assert.That(storedEntries[2].Timestamp, Is.EqualTo(new DateTime(2025, 11, 30, 22, 57, 0, DateTimeKind.Utc)));
        Assert.That(storedEntries[2].TotalCoinsRaised, Is.EqualTo(9100000000L));
    }

    private static MockedUpdateArgs CreateMayorAuraArgs(IMayorAuraService service, DateTime receivedAt, long coinsRaised)
    {
        // Format with commas as thousands separator (game format)
        var formattedCoins = coinsRaised.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = receivedAt,
                Chest = new ChestView()
                {
                    Name = "Mayor Aura",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§6Fundraising",
                            Description = $"§7Total Coins Raised: §6{formattedCoins}\n§7Your Participation: §60\n§7Your Gains: §60"
                        }
                    }
                }
            }
        };
        args.AddService<IMayorAuraService>(service);
        return args;
    }
}
