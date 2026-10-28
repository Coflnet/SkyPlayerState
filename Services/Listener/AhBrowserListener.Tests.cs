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
        => Setup(description, out _);

    private static (MockedUpdateArgs args, Mock<IPlayerNameApi> names, Mock<IPlayerApi> player) Setup(string description, out Mock<ISearchApi> search)
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
        search = new Mock<ISearchApi>();
        // unknown names (nicked) are not found
        search.Setup(s => s.ApiSearchPlayerPlayerNameGetAsync(It.IsAny<string>(), 0, default))
            .ThrowsAsync(new Coflnet.Sky.Api.Client.Client.ApiException(400, "player_not_found"));
        args.AddService(search.Object);
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
    public async Task SoldItemWithUnknownNameBuyerResolvesUuidBeforeCaching()
    {
        // regression: the buyer's display name was posted to the uuid endpoint and always failed with invalid_uuid
        var (args, names, player) = Setup("Sold for: §6100 coins\n§7Buyer: §6[MVP+] Buyer1", out var search);
        var uuid = System.Guid.NewGuid().ToString("N");
        search.Setup(s => s.ApiSearchPlayerPlayerNameGetAsync("Buyer1", 0, default))
            .ReturnsAsync(new List<Coflnet.Sky.Api.Client.Model.PlayerResult> { new("buyer1", uuid, 0) });

        await new AhBrowserListener().Process(args);

        names.Verify(n => n.PlayerNameUuidNameGetAsync("Buyer1", 0, default), Times.Once);
        player.Verify(p => p.ApiPlayerPlayerUuidNamePostAsync(uuid, 0, default), Times.Once);
    }

    [Test]
    public async Task NickedBuyerThatCannotBeResolvedIsSkipped()
    {
        var (args, _, player) = Setup("Sold for: §6100 coins\n§7Buyer: §6[MVP+] not_real_e");

        await new AhBrowserListener().Process(args);

        player.Verify(p => p.ApiPlayerPlayerUuidNamePostAsync(It.IsAny<string>(), 0, default), Times.Never);
    }

    [Test]
    public async Task SoldItemWithUuidBuyerCachesName()
    {
        var uuid = System.Guid.NewGuid().ToString("N");
        var (args, _, player) = Setup("Sold for: §6100 coins\n§7Buyer: " + uuid);

        await new AhBrowserListener().Process(args);

        player.Verify(p => p.ApiPlayerPlayerUuidNamePostAsync(uuid, 0, default), Times.Once);
    }

    [Test]
    public async Task InvalidUuidApiResponseDoesNotLogError()
    {
        var uuid = System.Guid.NewGuid().ToString("N");
        var (args, _, player) = Setup("Sold for: §6100 coins\n§7Buyer: " + uuid);
        player.Setup(p => p.ApiPlayerPlayerUuidNamePostAsync(It.IsAny<string>(), 0, default))
            .ThrowsAsync(new System.Exception("{\"slug\":\"invalid_uuid\"}"));
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger>();
        var listener = new AhBrowserListener();
        listener.SetLogger(logger.Object);

        await listener.Process(args);

        logger.Verify(l => l.Log(Microsoft.Extensions.Logging.LogLevel.Error, It.IsAny<Microsoft.Extensions.Logging.EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<System.Exception>(), It.IsAny<System.Func<It.IsAnyType, System.Exception?, string>>()), Times.Never);
    }

    [Test]
    public void ListenerIsOptionalSoItCannotDropTheWholeUpdate()
    {
        Assert.That(new AhBrowserListener().Optional, Is.True);
    }
}
