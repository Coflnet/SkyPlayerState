using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ScoreboardParserTests
{
    // legacy benzene ring (U+23E3) and the newer private-use glyph (U+E067)
    private const string LegacyLine = " ⏣ Lotus Atoll";
    private const string PrivateUseLine = "  Lotus Atoll";

    [TestCase(LegacyLine)]
    [TestCase(PrivateUseLine)]
    public void ExtractArea_ReadsEitherGlyph(string areaLine)
    {
        var scoreboard = new List<string>
        {
            "[SKYBLOCK]", "07/15/26 m79DF", " Late Summer 15th", areaLine, "Purse: 40,859,741"
        };
        ScoreboardParser.ExtractArea(scoreboard).Should().Be("Lotus Atoll");
    }

    [Test]
    public void ExtractArea_NullWhenNoAreaLine()
    {
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", "Purse: 1" }).Should().BeNull();
        ScoreboardParser.ExtractArea(null).Should().BeNull();
    }

    [Test]
    public void IsAreaLine_DoesNotMatchOtherIndentedLines()
    {
        // other scoreboard lines also start with a space but are not area markers
        ScoreboardParser.IsAreaLine(" 2:00am ☽").Should().BeFalse();
        ScoreboardParser.IsAreaLine(" Late Summer 15th").Should().BeFalse();
    }

    [TestCase(" ⏣ Kuudra's Hollow")]
    [TestCase("  Kuudra's Hollow")]
    public void IsAreaLine_MatchesKuudraWithEitherGlyph(string line)
    {
        (ScoreboardParser.IsAreaLine(line) && line.EndsWith("Kuudra's Hollow")).Should().BeTrue();
    }

    [Test]
    public void ParsePurse_ReadsPlainPurse()
    {
        ScoreboardParser.ParsePurse(new[] { "[SKYBLOCK]", "Purse: 40,859,741", "Hub" }).Should().Be(40_859_741);
    }

    [Test]
    public void ParsePurse_ReadsPiggyWithInterestSuffix()
    {
        // while coins sit in the piggy bank the label changes and shows the interest gained
        ScoreboardParser.ParsePurse(new[] { "Piggy: 1,234,567 (+12,345)" }).Should().Be(1_234_567);
    }

    [Test]
    public void ParsePurse_IgnoresFormattingCodes()
    {
        ScoreboardParser.ParsePurse(new[] { "§7Purse: §640,859,741" }).Should().Be(40_859_741);
    }

    [Test]
    public void ParsePurse_NullWhenAbsent()
    {
        ScoreboardParser.ParsePurse(new[] { "[SKYBLOCK]", "Bits: 10,920" }).Should().BeNull();
        ScoreboardParser.ParsePurse(null).Should().BeNull();
    }

    [Test]
    public void ParseBits_ReadsBits()
    {
        ScoreboardParser.ParseBits(new[] { "Bits: 10,920", "Purse: 5" }).Should().Be(10_920);
    }

    [Test]
    public void ParseBits_NullWhenAbsent()
    {
        ScoreboardParser.ParseBits(new[] { "Purse: 5" }).Should().BeNull();
    }
}
