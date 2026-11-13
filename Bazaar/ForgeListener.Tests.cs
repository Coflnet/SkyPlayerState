using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using AwesomeAssertions;
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
        time.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(55).AddSeconds(6), TimeSpan.FromSeconds(1));
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
        time.Should().BeCloseTo(DateTime.UtcNow.AddHours(4).AddMinutes(11), TimeSpan.FromSeconds(1));
    }
    [TestCase("4h 11m", 15060)]
    [TestCase("55m", 3300)]
    [TestCase("2d 4h", 187200)]
    public void TrailingMinutesAndHoursDoNotRequireASpace(string remaining, int seconds)
    {
        var time = new ForgeListener().ParseTimeFromDescription(new Item
        {
            ItemName = "Refined Mithril", Description = "§7Time Remaining: §a" + remaining
        });
        time.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(seconds), TimeSpan.FromSeconds(1));
    }
}
