using System;
using MessagePack;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Models;

public class PersistenceServiceTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

    [Test]
    public void InventoryReadsLegacyLimitsValueAtKeyEleven()
    {
        var playerUuid = Guid.NewGuid();
        var profileUuid = Guid.NewGuid();
        var legacyState = new LegacyStateObject
        {
            PlayerId = "player",
            McInfo = new McInfo { Uuid = playerUuid },
            Profiles = new() { new Profile { Uuid = profileUuid } },
            Limits = new LegacyLimitsSummary
            {
                Bazaar = new(),
                AuctionHouse = new(),
                Trade = new()
            }
        };
        var inventory = new Inventory
        {
            Serialized = MessagePackSerializer.Serialize(legacyState, Options)
        };

        var state = inventory.GetStateObject();

        Assert.That(state.PlayerId, Is.EqualTo("player"));
        Assert.That(state.McInfo.Uuid, Is.EqualTo(playerUuid));
        Assert.That(state.Profiles[0].Uuid, Is.EqualTo(profileUuid));
        Assert.That(state.LastTab, Is.Empty);
    }

    [Test]
    public void InventoryStillRoundTripsCurrentLastTab()
    {
        var inventory = new Inventory(new StateObject
        {
            PlayerId = "player",
            LastTab = new[] { "first", "second" }
        });

        Assert.That(inventory.GetStateObject().LastTab, Is.EqualTo(new[] { "first", "second" }));
    }
}
