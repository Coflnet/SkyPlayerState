using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class ForgeListenerTests
{
    [Test]
    public async Task TestForgeListener()
    {
        var handler = new ForgeListener();
        var time = handler.ParseTimeFromDescription(new Item
        {
            ItemName = "Refined Mithril",
            Description = "§7Time Remaining: §a55m 6s"
        });
        time.Should().BeCloseTo(DateTime.Now.AddMinutes(55).AddSeconds(6), TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task TestForgeListenerWithHours()
    {
        var handler = new ForgeListener();
        var time = handler.ParseTimeFromDescription(new Item
        {
            ItemName = "Refined Mithril",
            Description = "§7Time Remaining: §a4h 11m for item §5Refined Mithril"
        });
        time.Should().BeCloseTo(DateTime.Now.AddHours(4).AddMinutes(11), TimeSpan.FromSeconds(1));
    }
}
