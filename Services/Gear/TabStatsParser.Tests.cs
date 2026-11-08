using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Line formats from SkyHanni (SkyblockStat.tablistPattern, FarmingFortuneDisplay, PetStoragePatterns), colors as the mod uploads them.</summary>
public class TabStatsParserTests
{
    [Test]
    public void StatsWidget_PrivateUseGlyphs_CommaNumbers_AnyOrder()
    {
        var reading = TabStatsParser.Parse([
            "§r §6Mining Fortune: §61,234§r",
            "§r§b[280] SomePlayer §r",
            "§r Mining Speed: 2,500",
            "§r Stats:",
            "§r Pristine: 3.5"
        ], "Ruby Mining");

        reading.Stats.Should().HaveCount(3);
        reading.Stats["Mining Fortune"].Should().Be(1234);
        reading.Stats["Mining Speed"].Should().Be(2500);
        reading.Stats["Pristine"].Should().Be(3.5);
    }

    [Test]
    public void LegacyGlyph_AndNoGlyph_KeepTheFullValue()
    {
        var reading = TabStatsParser.Parse([" Farming Fortune: ☘1,450", " Sea Creature Chance: 1234"], null);

        reading.Stats["Farming Fortune"].Should().Be(1450);
        reading.Stats["Sea Creature Chance"].Should().Be(1234, "the optional glyph must not swallow the first digit");
    }

    [Test]
    public void PercentAndTrailingText_AreTolerated()
    {
        var reading = TabStatsParser.Parse([" Double Hook Chance: 15.5%", " Fishing Speed: 120 (+20)"], null);

        reading.Stats["Double Hook Chance"].Should().Be(15.5);
        reading.Stats["Fishing Speed"].Should().Be(120);
    }

    [Test]
    public void MissingStats_AreAbsent_NotZero_AndNonWhitelistedLinesIgnored()
    {
        var reading = TabStatsParser.Parse([" Purse: 1,234,567", " Bank: 50,000", " Strength: 400", "Area: The Garden"], null);

        reading.Stats.Should().BeEmpty();
        reading.Stats.ContainsKey("Farming Fortune").Should().BeFalse();
    }

    [Test]
    public void CropLine_OnlyForTheTasksCrop()
    {
        string[] tab = [" Farming Fortune: 1,500", " Wheat Fortune: 321", " Melon Slice Fortune: 100"];

        var wheat = TabStatsParser.Parse(tab, "Wheat Farming");
        wheat.Stats.Should().ContainKey("Wheat Fortune").WhoseValue.Should().Be(321);
        wheat.Stats.Should().NotContainKey("Melon Slice Fortune");

        var melon = TabStatsParser.Parse(tab, "Melon Farming");
        melon.Stats.Should().NotContainKey("Wheat Fortune");
        melon.Stats["Melon Slice Fortune"].Should().Be(100);

        TabStatsParser.Parse(tab, null).Stats.Keys.Should().Equal("Farming Fortune");
    }

    [Test]
    public void Pests_AliveCountAndPenaltyFlag()
    {
        var reading = TabStatsParser.Parse(["Pests:", " Alive: 5", " Cooldown: 1m 58s", " Farming Fortune: 1,000"], "Wheat Farming");

        reading.PestsAlive.Should().Be(5);
        var values = TabStatsParser.SampleValues(reading);
        values[TabStatsParser.PestsAliveStat].Should().Be(5);
        values[TabStatsParser.PestsPenaltyStat].Should().Be(1);
        TabStatsParser.SampleValues(TabStatsParser.Parse([" Alive: 3"], null))[TabStatsParser.PestsPenaltyStat].Should().Be(0);
    }

    [Test]
    public void PetLine_LevelAndType_WithoutRarity()
    {
        var reading = TabStatsParser.Parse(["Pet:", "§r §6[Lvl 100] §6Mooshroom Cow ✦", " 33,915/179.7k XP (18.9%)"], null);

        reading.Pet!.Type.Should().Be("MOOSHROOM_COW");
        reading.Pet.Level.Should().Be(100);
        reading.Pet.Tier.Should().BeNull();
    }

    [Test]
    public void HeaderAndFooter_AreSkipped()
    {
        var reading = TabStatsParser.Parse([TabListUpdate.FooterPrefix + " Farming Fortune: 99"], null);

        reading.Stats.Should().BeEmpty();
    }
}
