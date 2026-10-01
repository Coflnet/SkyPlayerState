using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class SniperServiceTests
{
    [Test]
    public void NullGemsEntryDoesNotThrowAndKeepsTagAndSourceUnchanged()
    {
        // trigger: ExtraAttributes containing {"gems": null} made NBT.FlattenNbtData call gems.GetType() on null
        var attributes = new Dictionary<string, object> { { "gems", null }, { "tier", "LEGENDARY" }, { "uuid", "abc" } };
        var item = new Item { Tag = "HYPERION", ItemName = "Hyperion", Count = 1, ExtraAttributes = attributes };

        var auction = SniperService.ToAuctionRepresent(item);

        auction.Tag.Should().Be("HYPERION");
        attributes.Should().HaveCount(3);
        attributes.Should().ContainKey("gems");
        attributes["uuid"].Should().Be("abc");
    }

    [Test]
    public void NullEffectsEntryDoesNotThrow()
    {
        var item = new Item { Tag = "POTION", ItemName = "Potion", Count = 1, ExtraAttributes = new() { { "effects", null }, { "necromancer_souls", null } } };
        var auction = SniperService.ToAuctionRepresent(item);
        auction.Tag.Should().Be("POTION");
    }

    [Test]
    public void FlattenDoesNotMutateSourceAttributes()
    {
        var item = new Item
        {
            Tag = "ROD", ItemName = "Rod", Count = 1,
            ExtraAttributes = new() { { "sinker", new Dictionary<string, object> { { "uuid", "u" }, { "part", "p" } } } }
        };
        SniperService.ToAuctionRepresent(item);
        item.ExtraAttributes.Keys.Should().BeEquivalentTo(new[] { "sinker" });
    }

    [Test]
    public void TradeInfoListenerIsOptional()
    {
        new TradeInfoListener(null).Optional.Should().BeTrue("a pricing failure must not drop the inventory state save");
    }
}
