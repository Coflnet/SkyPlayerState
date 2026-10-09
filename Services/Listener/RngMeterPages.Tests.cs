using System;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class RngMeterPagesTests
{
    private static RngMeterItem Row(string chest, int index, string tag, string name = null, int minute = 0) => new()
    {
        ChestName = chest, ItemIndex = index, ItemTag = tag, ItemName = name ?? tag, LastUpdated = new DateTime(2026, 10, 1, 12, minute, 0)
    };

    [TestCase("(2/2) Catacombs (M7) RNG Meter", "Catacombs (M7) RNG Meter", 2)]
    [TestCase("(1/2) Catacombs (M7) RNG Meter", "Catacombs (M7) RNG Meter", 1)]
    [TestCase("Catacombs (M7) RNG Meter", "Catacombs (M7) RNG Meter", 1)]
    public void PagePrefix_IsStripped(string chest, string expected, int page)
    {
        RngMeterPages.BaseName(chest).Should().Be(expected);
        RngMeterPages.PageOf(chest).Should().Be(page);
    }

    [Test]
    public void AllThreeKeyShapes_AreOneMeter_WithNecronsHandleFromPageTwo()
    {
        var merged = RngMeterPages.Merge(
        [
            Row("Catacombs (M7) RNG Meter", 10, "STORM_THE_FISH"),
            Row("(1/2) Catacombs (M7) RNG Meter", 10, "STORM_THE_FISH", minute: 5),
            Row("(1/2) Catacombs (M7) RNG Meter", 11, "MAXOR_THE_FISH"),
            Row("(2/2) Catacombs (M7) RNG Meter", 12, "NECRON_HANDLE", "§6Necron's Handle"),
            Row("(2/2) Catacombs (M7) RNG Meter", 13, null, "§cClose"),
            Row("(2/2) Catacombs (M7) RNG Meter", 14, "AFTER_CLOSE"),
            Row("Garden RNG Meter", 1, "OTHER")
        ]);

        merged.Keys.Should().BeEquivalentTo("Catacombs (M7) RNG Meter", "Garden RNG Meter");
        var m7 = merged["Catacombs (M7) RNG Meter"];
        m7.Select(i => i.ItemTag).Should().Equal("STORM_THE_FISH", "MAXOR_THE_FISH", "NECRON_HANDLE");
        m7[0].LastUpdated.Minute.Should().Be(5, "the newest copy of an item seen on several keys is kept");
    }
}
