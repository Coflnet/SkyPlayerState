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
    public void BazaarExpiryClaimsAndObservationTimeSurvivePersistenceAndCopies()
    {
        var time = DateTime.UtcNow;
        var original = new StateObject { BazaarUpdatedAt = time, BazaarObservedAt = time.AddSeconds(-1), BazaarOffers = new() { new() {
            ItemName = "Gill Membrane", Amount = 1024, FilledAmount = 1024,
            ClaimedAmount = 512, PendingObservedClaims = 256, IsExpired = true, Created = time.AddDays(-1)
        } } };
        var stored = new Inventory(new StateObject(original)).GetStateObject();
        Assert.That(stored.BazaarUpdatedAt, Is.EqualTo(time));
        Assert.That(stored.BazaarObservedAt, Is.EqualTo(time.AddSeconds(-1)));
        Assert.That(stored.BazaarOffers[0].IsExpired, Is.True);
        Assert.That(stored.BazaarOffers[0].ClaimedAmount, Is.EqualTo(512));
        Assert.That(stored.BazaarOffers[0].PendingObservedClaims, Is.EqualTo(256));
        Assert.That(stored.BazaarOffers[0].FilledAmount, Is.EqualTo(1024));
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
