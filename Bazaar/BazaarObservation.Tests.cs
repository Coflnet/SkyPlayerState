using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarObservationTests
{
    [Test]
    public void FillTotalComesFromOrderDescriptionNotRecentCustomerList()
    {
        var offer = BazaarListener.ParseOfferFromItem(new Item {
            ItemName = "§6§lSELL Wheat", Tag = "WHEAT",
            Description = "§7Offer amount: §a1,024§7x\n§7Price per unit: §610.0 coins\n§7Filled: §6512§7/1,024 §a50%"
        });
        Assert.That(offer.Amount, Is.EqualTo(1024));
        Assert.That(offer.FilledAmount, Is.EqualTo(512));
        Assert.That(offer.Customers, Is.Empty);
    }
    [TestCase("§a§lBUY §aAgatha's Coupon", "Agatha's Coupon")]
    [TestCase("§6§lSELL §aEnchanted Cobblestone", "Enchanted Cobblestone")]
    public void ParsesOrderNamesBeforeRemovingBuyOrSellPrefix(string name, string expected)
    {
        var offer = BazaarListener.ParseOfferFromItem(new Item {
            ItemName = name,
            Description = "§7Order amount: §a64§7x\n§7Price per unit: §610.0 coins"
        });
        Assert.That(offer.ItemName, Is.EqualTo(expected));
    }

    [Test]
    public async Task MissingMenuItemTagIsResolvedBeforeSendingWholeView()
    {
        var args = new MockedUpdateArgs { currentState = new(), msg = new() {
            UserId = "1", ReceivedAt = DateTime.UtcNow,
            Chest = new() { Name = "Co-op Bazaar Orders", Items = new() { new Item {
                ItemName = "§6§lSELL §aEnchanted Cobblestone",
                Description = "§7Offer amount: §a640§7x\n§7Price per unit: §6500.9 coins\n§7Filled: §6320§7/640"
            } } }
        }};
        args.currentState.McInfo.Name = "Ekwav";
        var items = new Mock<Coflnet.Sky.Items.Client.Api.IItemsApi>();
        items.Setup(i => i.ItemsSearchTermGetAsync("Enchanted Cobblestone", null, 0, default))
            .ReturnsAsync(new List<Coflnet.Sky.Items.Client.Model.SearchResult> {
                new() { Tag = "ENCHANTED_COBBLESTONE", Text = "Enchanted Cobblestone" }
            });
        args.AddService(items.Object);
        var sync = new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient());
        sync.Setup(s => s.Observe(args)).Returns(Task.CompletedTask);
        args.AddService(sync.Object);
        args.AddService<Microsoft.Extensions.Logging.ILogger<BazaarListener>>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BazaarListener>.Instance);
        await new BazaarListener().Process(args);
        Assert.That(args.currentState.BazaarOffers.Single().ItemTag, Is.EqualTo("ENCHANTED_COBBLESTONE"));
        Assert.That(args.currentState.BazaarOffers.Single().FilledAmount, Is.EqualTo(320));
        sync.Verify(s => s.Observe(args), Times.Once);
    }

    [Test]
    public async Task RepeatedOrderViewsReconcileEvenWhenOrderCountIsUnchanged()
    {
        var args = new MockedUpdateArgs { currentState = new(), msg = new() {
            UserId = "1", ReceivedAt = DateTime.UtcNow,
            Chest = new() { Name = "Your Bazaar Orders", Items = new() { new Item {
                ItemName = "§6§lSELL Wheat", Tag = "WHEAT",
                Description = "§7Offer amount: §a64§7x\n§7Price per unit: §610.0 coins\n§7Filled: §616§7/64"
            } } }
        }};
        args.currentState.McInfo.Name = "Ekwav";
        var sync = new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient());
        sync.Setup(s => s.Observe(args)).Returns(Task.CompletedTask);
        args.AddService(sync.Object);
        args.AddService<Microsoft.Extensions.Logging.ILogger<BazaarListener>>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BazaarListener>.Instance);
        await new BazaarListener().Process(args);
        var created = args.currentState.BazaarOffers.Single().Created;
        args.msg.Chest.Items[0].Description = args.msg.Chest.Items[0].Description!.Replace("§616§7/64", "§632§7/64");
        args.msg.ReceivedAt = args.msg.ReceivedAt.AddSeconds(1);
        await new BazaarListener().Process(args);
        sync.Verify(s => s.Observe(args), Times.Exactly(2));
        Assert.That(args.currentState.BazaarOffers.Single().FilledAmount, Is.EqualTo(32));
        Assert.That(args.currentState.BazaarOffers.Single().Created, Is.EqualTo(created));
    }

}
