using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core.Migrations;
using Coflnet.Sky.PlayerState.Tests;
using Confluent.Kafka;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ShensListenerTest
{
    [Test]
    public async Task ExtractAndStore()
    {
        var stored = File.ReadAllText("Mock/shens.json");
        var updateArg = new MockedUpdateArgs()
        {
            msg = new Models.UpdateMessage()
            {
                PlayerId = "test",
                Chest = JsonConvert.DeserializeObject<Models.ChestView>(stored),
                ReceivedAt = new DateTime(2025, 2, 19)
            }
        };
        ShenHistory capturedShenHistory = null;
        var storage = new Mock<IShenStorage>();
        storage.Setup(x => x.Store(It.IsAny<ShenHistory>())).Returns(Task.CompletedTask)
                .Callback<ShenHistory>(sh => capturedShenHistory = sh);
        updateArg.AddService<IShenStorage>(storage.Object);
        var listener = new ShensListener(NullLogger<ShensListener>.Instance);
        await listener.Process(updateArg);
        storage.Verify(x => x.Store(It.IsAny<ShenHistory>()), Times.Once);
        capturedShenHistory.Should().NotBeNull();
        capturedShenHistory.Reporter.Should().Be("test");
        capturedShenHistory.Year.Should().Be(402);
        capturedShenHistory.Offers.Should().HaveCount(5);
        Console.WriteLine(capturedShenHistory.Offers.First().Value);
        capturedShenHistory.Offers.First().Value.Should().Be("""
        {"§b[MVP§d+§b] kleinKolibri711":2000000000,"§b[MVP§d+§b] StarCatHorse":1000000000,"§b[MVP§f+§b] Imlosingmybrain":1000000000,"§b[MVP§2+§b] owenthebozo":552802243,"§b[MVP§9+§b] VivaVertigo":17750002,"§b[MVP§6+§b] ZEALOTSEVERYWHER":12000000,"§b[MVP§9+§b] RNCGaming":10000000,"§b[MVP§2+§b] MIS000":10000000,"§a[VIP] VariantSheep":8500001,"§b[MVP§9+§b] SithCriker":5000000}
        """);
    }

    [Test]
    public async Task BidBeyondLongRange_IsClampedInsteadOfFailingTheUpdate()
    {
        // production 2026-10-01: two bids displayed as long.MaxValue rounded up, long.Parse threw
        var description = "§7Accessory Power: §6+16\n-----------------\nYou haven't bid!\n"
            + "§7The top §a20§7 bidders win this item:\n(Right-click to collapse list)\n"
            + "§e§l1. §a[VIP§6+§a] rGliz §8- §69,223,372,036,854,776,000 Coins\n"
            + "§e§l2. §b[MVP§c+§b] methionyls §8- §6999,999,999,999,999 Coins\n"
            + "§e§l3. §b[MVP§c+§b] CryoCD §8- §61,000,000 Coins";
        var updateArg = new MockedUpdateArgs()
        {
            msg = new Models.UpdateMessage()
            {
                PlayerId = "test",
                Chest = new Models.ChestView()
                {
                    Name = "Shen's Auction",
                    Items = [new Models.Item() { Tag = "ARTIFACT_OF_CONTROL", ItemName = "Artifact of Control", Description = description }]
                },
                ReceivedAt = new DateTime(2026, 10, 1)
            }
        };
        ShenHistory captured = null;
        var storage = new Mock<IShenStorage>();
        storage.Setup(x => x.Store(It.IsAny<ShenHistory>())).Returns(Task.CompletedTask)
                .Callback<ShenHistory>(sh => captured = sh);
        updateArg.AddService<IShenStorage>(storage.Object);

        await new ShensListener(NullLogger<ShensListener>.Instance).Process(updateArg);

        captured.Should().NotBeNull();
        var bids = JsonConvert.DeserializeObject<Dictionary<string, long>>(captured.Offers["ARTIFACT_OF_CONTROL"]);
        bids["§a[VIP§6+§a] rGliz"].Should().Be(long.MaxValue);
        bids["§b[MVP§c+§b] methionyls"].Should().Be(999_999_999_999_999);
        bids["§b[MVP§c+§b] CryoCD"].Should().Be(1_000_000);
    }
}
