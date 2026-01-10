using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class BitListenerTests
{
    [Test]
    public void ParseBitValue_ValidCostDescription_ReturnsAmount()
    {
        // Arrange
        var description = "§7Cost\n§b1,500 Bits";

        // Act
        var result = BitListener.ParseBitValue(description);

        // Assert
        Assert.That(result, Is.EqualTo(1500L));
    }

    [Test]
    public void ParseBitValue_CostWithCommas_ReturnsAmount()
    {
        // Arrange
        var description = "§7Cost\n§b1,350 Bits";

        // Act
        var result = BitListener.ParseBitValue(description);

        // Assert
        Assert.That(result, Is.EqualTo(1350L));
    }

    [Test]
    public void ParseBitValue_LargeCostWithCommas_ReturnsAmount()
    {
        // Arrange
        var description = "§7Cost\n§b2,500,000 Bits";

        // Act
        var result = BitListener.ParseBitValue(description);

        // Assert
        Assert.That(result, Is.EqualTo(2500000L));
    }

    [Test]
    public void ParseBitValue_AdditionalSampleFormats_ReturnsAmounts()
    {
        // Ditto Blob: Cost newline format
        var ditto = "§7Cost\n§b600 Bits";
        Assert.That(BitListener.ParseBitValue(ditto), Is.EqualTo(600L));

        // Builder's Wand: Cost newline with comma
        var builders = "§7Cost\n§b12,000 Bits";
        Assert.That(BitListener.ParseBitValue(builders), Is.EqualTo(12000L));

        // Block Zapper: Cost newline with comma
        var zapper = "§7Cost\n§b5,000 Bits";
        Assert.That(BitListener.ParseBitValue(zapper), Is.EqualTo(5000L));

        // Bits Talisman: Cost newline large amount
        var talisman = "§7Cost\n§b15,000 Bits";
        Assert.That(BitListener.ParseBitValue(talisman), Is.EqualTo(15000L));

        // Bitbug: inline 'Cost:' variant
        var bitbug = "§7Cost: §b5,000 Bits";
        Assert.That(BitListener.ParseBitValue(bitbug), Is.EqualTo(5000L));
    }

    [Test]
    public void ParseBitValue_InvalidDescription_ReturnsNull()
    {
        // Arrange
        var description = "§7Some random item description";

        // Act
        var result = BitListener.ParseBitValue(description);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseBitValue_EmptyString_ReturnsNull()
    {
        // Arrange
        var description = "";

        // Act
        var result = BitListener.ParseBitValue(description);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Process_CommunityShopChest_StoresBitMapping()
    {
        // Arrange
        BitTagMapping? capturedMapping = null;
        var service = new Mock<IBitService>();
        service.Setup(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()))
            .Returns(Task.CompletedTask)
            .Callback<BitTagMapping>(m => capturedMapping = m);

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = DateTime.UtcNow,
                Chest = new ChestView()
                {
                    Name = "Community Shop",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§cGod Potion",
                            Tag = "GOD_POTION_2",
                            Description = "§7Consume this potion to receive an\n§7assortment of positive §dpotion effects§7!\n\n§7Duration: §a15h 7m\n§a+12h §7Default\n§a+3h §bAlchemy Level\n\n§7The duration of God Potions can be stacked!\n\n§eRight-click to consume!\n\n§c§lSPECIAL\n\n§7Cost\n§b1,500 Bits\n\n§eClick to trade!"
                        }
                    }
                }
            }
        };
        args.AddService<IBitService>(service.Object);

        var listener = new BitListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()), Times.Once);
        Assert.That(capturedMapping, Is.Not.Null);
        Assert.That(capturedMapping!.ShopName, Is.EqualTo("Community Shop"));
        Assert.That(capturedMapping.ItemTag, Is.EqualTo("GOD_POTION_2"));
        Assert.That(capturedMapping.BitValue, Is.EqualTo(1500L));
    }

    [Test]
    public async Task Process_BitsShopChest_StoresBitMapping()
    {
        // Arrange
        BitTagMapping? capturedMapping = null;
        var service = new Mock<IBitService>();
        service.Setup(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()))
            .Returns(Task.CompletedTask)
            .Callback<BitTagMapping>(m => capturedMapping = m);

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = DateTime.UtcNow,
                Chest = new ChestView()
                {
                    Name = "Bits Shop",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§9Kismet Feather",
                            Tag = "KISMET_FEATHER",
                            Description = "§7Allows you to reroll a §cDungeon\n§c§7or §cKuudra§7 reward chest.\n\n§7Keep this feather in your\n§7inventory or §6Dungeon Sack§7 and\n§7open a reward chest to use.\n\n§7§8You may only reroll 1 reward\n§8chest per run!\n\n§9§lRARE\n\n§7Cost\n§b1,350 Bits\n\n§eClick to trade!"
                        }
                    }
                }
            }
        };
        args.AddService<IBitService>(service.Object);

        var listener = new BitListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()), Times.Once);
        Assert.That(capturedMapping, Is.Not.Null);
        Assert.That(capturedMapping!.ShopName, Is.EqualTo("Bits Shop"));
        Assert.That(capturedMapping.ItemTag, Is.EqualTo("KISMET_FEATHER"));
        Assert.That(capturedMapping.BitValue, Is.EqualTo(1350L));
    }

    [Test]
    public async Task Process_NotBitShopChest_DoesNotStore()
    {
        // Arrange
        var service = new Mock<IBitService>();
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
        args.AddService<IBitService>(service.Object);

        var listener = new BitListener();

        // Act
        await listener.Process(args);

        // Assert
        service.Verify(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()), Times.Never);
    }

    [Test]
    public async Task Process_MultipleItems_StoresAllMappings()
    {
        // Arrange
        var capturedMappings = new List<BitTagMapping>();
        var service = new Mock<IBitService>();
        service.Setup(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()))
            .Returns(Task.CompletedTask)
            .Callback<BitTagMapping>(m => capturedMappings.Add(m));

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = DateTime.UtcNow,
                Chest = new ChestView()
                {
                    Name = "Community Shop",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§cGod Potion",
                            Tag = "GOD_POTION_2",
                            Description = "§7Cost\n§b1,500 Bits"
                        },
                        new Item()
                        {
                            ItemName = "§9Kismet Feather",
                            Tag = "KISMET_FEATHER",
                            Description = "§7Cost\n§b1,350 Bits"
                        },
                        new Item()
                        {
                            ItemName = "§eAnother Item",
                            Tag = "ANOTHER_ITEM",
                            Description = "§7Cost\n§b2,500 Bits"
                        }
                    }
                }
            }
        };
        args.AddService<IBitService>(service.Object);

        var listener = new BitListener();

        // Act
        await listener.Process(args);

        // Assert
        Assert.That(capturedMappings, Has.Count.EqualTo(3));
        Assert.That(capturedMappings[0].ItemTag, Is.EqualTo("GOD_POTION_2"));
        Assert.That(capturedMappings[0].BitValue, Is.EqualTo(1500L));
        Assert.That(capturedMappings[1].ItemTag, Is.EqualTo("KISMET_FEATHER"));
        Assert.That(capturedMappings[1].BitValue, Is.EqualTo(1350L));
        Assert.That(capturedMappings[2].ItemTag, Is.EqualTo("ANOTHER_ITEM"));
        Assert.That(capturedMappings[2].BitValue, Is.EqualTo(2500L));
    }

    [Test]
    public async Task Process_ItemWithoutBitCost_SkipsItem()
    {
        // Arrange
        var capturedMappings = new List<BitTagMapping>();
        var service = new Mock<IBitService>();
        service.Setup(x => x.StoreTagToBitMapping(It.IsAny<BitTagMapping>()))
            .Returns(Task.CompletedTask)
            .Callback<BitTagMapping>(m => capturedMappings.Add(m));

        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject(),
            msg = new UpdateMessage()
            {
                ReceivedAt = DateTime.UtcNow,
                Chest = new ChestView()
                {
                    Name = "Community Shop",
                    Items = new List<Item>()
                    {
                        new Item()
                        {
                            ItemName = "§cGod Potion",
                            Tag = "GOD_POTION_2",
                            Description = "§7Cost\n§b1,500 Bits"
                        },
                        new Item()
                        {
                            ItemName = "§dInvalid Item",
                            Tag = "INVALID_ITEM",
                            Description = "§7No cost here"
                        }
                    }
                }
            }
        };
        args.AddService<IBitService>(service.Object);

        var listener = new BitListener();

        // Act
        await listener.Process(args);

        // Assert
        Assert.That(capturedMappings, Has.Count.EqualTo(1));
        Assert.That(capturedMappings[0].ItemTag, Is.EqualTo("GOD_POTION_2"));
    }
}
