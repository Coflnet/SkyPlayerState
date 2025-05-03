using FluentAssertions;
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
}