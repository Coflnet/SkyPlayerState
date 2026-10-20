using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Tests;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class KuudraListenerTests
{
    [Test]
    public void ParseEssenceKey()
    {
        KuudraListener.GetTag(new() { ItemName = "§dCrimson Essence §8x400" })
            .Should().Be("ESSENCE_CRIMSON");
    }

    private class CapturingArgs : MockedUpdateArgs
    {
        public List<string> Messages = new();
        public override void SendMessage(string text, string? clickAction = null, string? source = null)
            => Messages.Add(text);
    }

    private class StubSniper : SniperService
    {
        public int Calls;
        public StubSniper() : base(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SNIPER_BASE_URL"] = "http://127.0.0.1:1" }).Build(),
            NullLogger<SniperService>.Instance)
        { }

        public override Task<List<Sniper.Client.Model.PriceEstimate>> GetPrices(IEnumerable<Item> items)
        {
            Calls++;
            return Task.FromResult(items.Select(_ => new Sniper.Client.Model.PriceEstimate { Median = 1000 }).ToList());
        }
    }

    private static (CapturingArgs args, StubSniper sniper) Setup(List<Item> items)
    {
        var args = new CapturingArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY,
                PlayerId = "p1",
                Chest = new ChestView { Name = "Paid Chest", Items = items }
            }
        };
        var sniper = new StubSniper();
        args.AddService<SniperService>(sniper);
        return (args, sniper);
    }

    /// <summary>
    /// Snapshots taken before Hypixel filled the chest have only empty slots (all fields null);
    /// they used to throw a NullReferenceException in the essence lookup and must send nothing.
    /// </summary>
    [TestCase(90)]
    [TestCase(10)]
    public async Task PaidChestSnapshotBeforeItemsArriveIsIgnored(int slotCount)
    {
        var items = Enumerable.Range(0, slotCount).Select(_ => new Item()).ToList();
        if (slotCount == 90)
            items[60] = new Item { ItemName = "§aSkyBlock Menu §7(Click)", Tag = "SKYBLOCK_MENU" };
        var (args, sniper) = Setup(items);

        await new KuudraListener().Process(args);

        args.Messages.Should().BeEmpty();
        sniper.Calls.Should().Be(0);
    }

    [Test]
    public async Task PopulatedPaidChestSendsWorthMessage()
    {
        var items = Enumerable.Range(0, 54).Select(_ => new Item()).ToList();
        items[3] = new Item { ItemName = "§5Some Armor", Tag = "SOME_ARMOR" };
        items[5] = new Item { ItemName = "§dCrimson Essence x10" };
        items[31] = new Item { ItemName = "§aKey", Description = "§7line\n§6Infernal Kuudra Key" };
        var (args, sniper) = Setup(items);

        await new KuudraListener().Process(args);

        sniper.Calls.Should().Be(1);
        args.Messages.Should().ContainSingle().Which.Should().Contain("Kuudra chest is worth");
    }
}
