using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class LoreStatsTests
{
    /// <summary>
    /// Regression guard for the dungeon lore format: the parenthesized values (reforge, dungeon boost) are never added,
    /// only the first (base) value counts.
    /// </summary>
    [Test]
    public void DungeonItemLine_OnlyTheFirstValueCounts()
    {
        var stats = LoreStats.Parse("§7Gear Score: §d565 §8(2,961)\n§7Intelligence: §a+350 §9(+100) §8(+350)\n§7Crit Damage: §c+0%",
            GearModifierTable.Capture.Stats);

        stats.Should().BeEquivalentTo(new Dictionary<string, double> { ["Intelligence"] = 350, ["Crit Damage"] = 0 });
    }
}
