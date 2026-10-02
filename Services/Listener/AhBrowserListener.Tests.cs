using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerName.Client.Api;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class AhBrowserListenerTests
{
    private static (MockedUpdateArgs args, Mock<IPlayerNameApi> names, Mock<IPlayerApi> player) Setup(string description)
    {
        var names = new Mock<IPlayerNameApi>();
        names.Setup(n => n.PlayerNameUuidNameGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync("");
        var player = new Mock<IPlayerApi>();
        var args = new MockedUpdateArgs
        {
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.INVENTORY,
                Chest = new ChestView
                {
                    Name = "Auction View",
                    Items = new List<Item> { new Item { ItemName = "Sword", Description = description } }
                }
            }
        };
        args.AddService(names.Object);
        args.AddService(player.Object);
        return (args, names, player);
    }

    [Test]
    public async Task SoldItemWithoutBuyerLineDoesNotCallNameApi()
    {
        var (args, names, player) = Setup("§7Seller: §6[MVP+] Someone\nSold for: §6100 coins");

        await new AhBrowserListener().Process(args);

        names.Verify(n => n.PlayerNameUuidNameGetAsync(It.IsAny<string>(), It.IsAny<int>(), default), Times.Never);
        player.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SoldItemWithBlankBuyerDoesNotCallNameApi()
    {
        var (args, names, _) = Setup("Sold for: §6100 coins\n§7Buyer: ");

        await new AhBrowserListener().Process(args);

        names.Verify(n => n.PlayerNameUuidNameGetAsync(It.IsAny<string>(), It.IsAny<int>(), default), Times.Never);
    }

    [Test]
    public async Task SoldItemWithBuyerStillResolvesAndCachesName()
    {
        var (args, names, player) = Setup("Sold for: §6100 coins\n§7Buyer: §6[MVP+] Buyer1");

        await new AhBrowserListener().Process(args);

        names.Verify(n => n.PlayerNameUuidNameGetAsync("Buyer1", 0, default), Times.Once);
        player.Verify(p => p.ApiPlayerPlayerUuidNamePostAsync("Buyer1", 0, default), Times.Once);
    }

    [Test]
    public void ListenerIsOptionalSoItCannotDropTheWholeUpdate()
    {
        Assert.That(new AhBrowserListener().Optional, Is.True);
    }
}
