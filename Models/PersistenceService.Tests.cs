using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Bazaar;
using Coflnet.Sky.PlayerState.Services;
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

    [Test]
    public void KnownItemUuidsCapSurvivesPersistenceAndCopyRoundTrip()
    {
        var original = new StateObject { PlayerId = "player" };
        for (var i = 0; i < 1100; i++)
            CollectionListener.RegisterKnownItemUuids(original, new ChestView { Items = new()
            {
                new Item { Tag = "SOMETHING", ItemName = "Something",
                    ExtraAttributes = new Dictionary<string, object> { { "uuid", Guid.NewGuid().ToString() } } }
            } });
        Assert.That(original.KnownItemUuids, Has.Count.EqualTo(1024));

        var stored = new Inventory(new StateObject(original)).GetStateObject();

        Assert.That(stored.KnownItemUuids, Has.Count.EqualTo(1024));
        Assert.That(stored.KnownItemUuids, Is.EqualTo(original.KnownItemUuids));
    }

    /// <summary>
    /// Mirrors <see cref="LegacyStateObject"/>'s role but for the field added in this task (Key 16,
    /// <see cref="StateObject.KnownItemUuids"/>) - everything up to (and including) Key 15 present,
    /// Key 16 simply absent, exactly like a state persisted before this task shipped.
    /// </summary>
    [MessagePackObject(AllowPrivate = true)]
    internal class StateObjectBeforeKnownItemUuids
    {
        [Key(0)] public List<Item> Inventory = new();
        [Key(1)] public List<List<Item>> Storage = new();
        [Key(2)] public Queue<ChestView> RecentViews = new();
        [Key(3)] public Queue<ChatMessage> ChatHistory = new();
        [Key(4)] public Queue<PurseUpdate> PurseHistory = new();
        [Key(5)] public McInfo McInfo = new();
        [Key(6)] public string PlayerId = string.Empty;
        [Key(7)] public List<Profile> Profiles = new();
        [Key(8)] public List<Offer> BazaarOffers = new();
        [Key(9)] public ExtractedInfo ExtractedInfo = new();
        [Key(10)] public StateSettings Settings = new();
        [Key(11)] public string[] LastTab = Array.Empty<string>();
        [Key(12)] public Dictionary<string, int> ItemsCollectedRecently = new();
        [Key(13)] public HashSet<Achievement> UnlockedAchievements = new();
        [Key(14)] public DateTime BazaarUpdatedAt;
        [Key(15)] public DateTime BazaarObservedAt;
        // Key 16 (KnownItemUuids) intentionally absent.
    }

    [Test]
    public void OldPersistedStateWithoutKnownItemUuidsFieldStillLoads()
    {
        var old = new StateObjectBeforeKnownItemUuids { PlayerId = "legacy-player" };
        var serialized = MessagePackSerializer.Serialize(old, Options);

        var state = MessagePackSerializer.Deserialize<StateObject>(serialized, Options);

        Assert.That(state.PlayerId, Is.EqualTo("legacy-player"));
        // A key missing from the serialized array leaves the property at its field-initializer
        // default (empty, not null) rather than throwing - verified here rather than assumed.
        Assert.That(state.KnownItemUuids, Is.Not.Null.And.Empty);
        // the listener helper must still work against such a state regardless (defensive ??= lazy-init)
        CollectionListener.RegisterKnownItemUuids(state, new ChestView { Items = new()
        {
            new Item { Tag = "SOMETHING", ItemName = "Something",
                ExtraAttributes = new Dictionary<string, object> { { "uuid", Guid.NewGuid().ToString() } } }
        } });
        Assert.That(state.KnownItemUuids, Has.Count.EqualTo(1));
    }
}
