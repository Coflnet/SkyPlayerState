using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ScoreboardParserTests
{
    // legacy benzene ring (U+23E3) and the newer private-use glyph (U+E067)
    private const string LegacyLine = " ⏣ Lotus Atoll";
    private const string PrivateUseLine = "  Lotus Atoll";

    [Test]
    public async Task DateOnlyScoreboardLineDoesNotThrow()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Scoreboard,
                Scoreboard = new[] { "[SKYBLOCK]", System.DateTime.UtcNow.ToString("MM/dd/yy"), "Purse: 1" }
            }
        };

        await new CollectionListener().Process(args);
    }

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

    [TestCase(" ⏣ None")]
    public void ExtractArea_TreatsLiteralNoneAsNoArea_LegacyGlyph(string noneLine)
    {
        // Hypixel shows the literal area name "None" at some spots (confirmed in production logs,
        // player Ekwav, on several islands) instead of omitting the area line - that must not be
        // treated as a real area (see HandleScoreboard, which would otherwise end/restart location
        // tracking and store a period for "None"). Uses a real area-marker line (glyph included),
        // not just an absent one, so this actually exercises the "None" special case.
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", noneLine, "Purse: 1" }).Should().BeNull();
    }

    [Test]
    public void ExtractArea_TreatsLiteralNoneAsNoArea_PrivateUseGlyph()
    {
        var noneLine = $" {ScoreboardParser.AreaGlyphPrivateUse} None";
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", noneLine, "Purse: 1" }).Should().BeNull();
    }

    [Test]
    public async Task NoneAreaScoreboardLine_DoesNotChangeLocationOrThrow()
    {
        // No services are registered on MockedUpdateArgs, so if HandleScoreboard treated "None" as
        // a real area it would throw trying to store the previous location's profit/session.
        var noneLine = $" {ScoreboardParser.AreaGlyphPrivateUse} None";
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Scoreboard,
                Scoreboard = new[] { "[SKYBLOCK]", System.DateTime.UtcNow.ToString("MM/dd/yy") + " m1", noneLine, "Purse: 1" }
            }
        };

        await new CollectionListener().Process(args);

        args.currentState.ExtractedInfo.CurrentLocation.Should().Be("Unknown",
            "\"None\" is not a real area and must not overwrite CurrentLocation or trigger a location change");
    }

    // ── Rift scoreboard: area glyph ф (U+0444) and "Motes:" instead of the purse ──

    [Test]
    public void ExtractArea_ReadsRiftGlyph()
    {
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", " ф Wyld Woods", "Motes: 12,345" }).Should().Be("Wyld Woods");
    }

    [Test]
    public void ExtractArea_RiftWithUnknownPrivateUseGlyph_FallsBackViaMotes()
    {
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", " \uE100 Wyld Woods", "Motes: 1,234" }).Should().Be("Wyld Woods");
    }

    [Test]
    public void ExtractArea_UnknownGlyphWithoutMotes_StaysNull()
    {
        ScoreboardParser.ExtractArea(new[] { "[SKYBLOCK]", " \uE100 Wyld Woods", "Purse: 1,234" }).Should().BeNull();
    }

    [Test]
    public void ExtractArea_MotesFallbackIgnoresLettersAndDigitsAsGlyph()
    {
        ScoreboardParser.ExtractArea(new[] { " 2 Something", " a Other", "Motes: 5" }).Should().BeNull();
    }

    [Test]
    public void IsRiftScoreboard_DetectsMotesOrRiftGlyph()
    {
        ScoreboardParser.IsRiftScoreboard(new[] { "§7Motes: §d12,345" }).Should().BeTrue();
        ScoreboardParser.IsRiftScoreboard(new[] { " ф Wyld Woods" }).Should().BeTrue();
        ScoreboardParser.IsRiftScoreboard(new[] { " ⏣ Hub", "Purse: 5" }).Should().BeFalse();
        ScoreboardParser.IsRiftScoreboard(new[] { " ф" }).Should().BeFalse();
        ScoreboardParser.IsRiftScoreboard(null).Should().BeFalse();
    }

    [Test]
    public void ExtractArea_RiftNone_IsStillNull()
    {
        ScoreboardParser.ExtractArea(new[] { " ф None", "Motes: 1" }).Should().BeNull();
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

    [TestCase("Copper: 3,416", 3416)]
    [TestCase("§7Copper: §c3,416", 3416)]
    [TestCase("Copper: 1,234 (+12)", 1234)]
    [TestCase("§cCopper: §c1,234 §a(+12)", 1234)]
    public void ParseCopper_ReadsCopper(string line, long expected)
    {
        ScoreboardParser.ParseCopper(new[] { "Purse: 5", line }).Should().Be(expected);
    }

    [Test]
    public void ParseCopper_NullWhenAbsent()
    {
        ScoreboardParser.ParseCopper(new[] { "Purse: 5", "Bits: 10" }).Should().BeNull();
        ScoreboardParser.ParseCopper(null).Should().BeNull();
    }

    private static async Task<StateObject> CopperScoreboard(StateObject state, string line, string? server = null)
    {
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Scoreboard, PlayerId = "p1", Scoreboard = server == null ? new[] { "[SKYBLOCK]", line } : new[] { "[SKYBLOCK]", System.DateTime.UtcNow.ToString("MM/dd/yy") + " " + server, line }, ReceivedAt = System.DateTime.UtcNow }
        };
        args.AddService<Microsoft.Extensions.Logging.ILogger<CollectionListener>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionListener>.Instance);
        await new CollectionListener().Process(args);
        return state;
    }

    [Test]
    public async Task Copper_IsStoredAndBookedOnlyOnIncrease()
    {
        var state = new StateObject();
        // first reading: stored, nothing booked
        await CopperScoreboard(state, "Copper: 1,000");
        state.ExtractedInfo.Copper.Should().Be(1000);
        state.ItemsCollectedRecently.Should().NotContainKey(Tasks.PseudoItems.COPPER);

        // increase books the difference (gain suffix ignored)
        await CopperScoreboard(state, "§cCopper: §c1,120 §a(+120)");
        state.ExtractedInfo.Copper.Should().Be(1120);
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(120);

        // spending at SkyMart books nothing
        await CopperScoreboard(state, "Copper: 20");
        state.ExtractedInfo.Copper.Should().Be(20);
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(120);

        // and a second gain accumulates
        await CopperScoreboard(state, "Copper: 50");
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(150);
    }

    [Test]
    public async Task Copper_JumpOfNinePointFiveThousandOnSameServer_IsBooked()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500");
        await CopperScoreboard(state, "Copper: 10,000");
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(9_500);
    }

    [Test]
    public async Task Copper_GainAboveFiftyThousand_IsNotBooked()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500");
        await CopperScoreboard(state, "Copper: 60,000");
        state.ExtractedInfo.Copper.Should().Be(60_000);
        state.ItemsCollectedRecently.Should().NotContainKey(Tasks.PseudoItems.COPPER);
    }

    [Test]
    public async Task Copper_AnalyzerSizedGainWithinAMinute_IsBooked()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500");
        state.ExtractedInfo.CopperSeenAt = System.DateTime.UtcNow.AddSeconds(-50);
        await CopperScoreboard(state, "Copper: 4,500");
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(4_000);
    }

    [Test]
    public async Task Copper_GainAfterServerChange_IsNotBookedButNextOneIs()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500", "mini11A");
        await CopperScoreboard(state, "Copper: 520", "mini22B");
        state.ExtractedInfo.Copper.Should().Be(520);
        state.ItemsCollectedRecently.Should().NotContainKey(Tasks.PseudoItems.COPPER);

        await CopperScoreboard(state, "Copper: 540", "mini22B");
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(20);
    }

    [Test]
    public async Task Copper_GainAfterTwoHourGap_IsNotBooked()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500");
        state.ExtractedInfo.CopperSeenAt = System.DateTime.UtcNow.AddHours(-2);
        await CopperScoreboard(state, "Copper: 620");
        state.ExtractedInfo.Copper.Should().Be(620);
        state.ItemsCollectedRecently.Should().NotContainKey(Tasks.PseudoItems.COPPER);
    }

    [Test]
    public async Task Copper_NormalGainWithinAMinute_IsBooked()
    {
        var state = new StateObject();
        await CopperScoreboard(state, "Copper: 500");
        state.ExtractedInfo.CopperSeenAt = System.DateTime.UtcNow.AddSeconds(-40);
        await CopperScoreboard(state, "Copper: 620");
        state.ItemsCollectedRecently[Tasks.PseudoItems.COPPER].Should().Be(120);
    }

    [Test]
    public async Task GetCurrencies_ReturnsCopper()
    {
        var state = new StateObject();
        state.ExtractedInfo.Copper = 777;
        var persistence = new Moq.Mock<Coflnet.Sky.PlayerState.Models.IPersistenceService>();
        persistence.Setup(p => p.GetStateObject("p1")).Returns(Task.FromResult(state));
        var controller = new Coflnet.Sky.PlayerState.Controllers.PlayerStateController(persistence.Object, null!);

        var currencies = await controller.GetCurrencies("p1");

        currencies.Copper.Should().Be(777);
    }

    [Test]
    public async Task Copper_GainOnAlphaNetwork_IsNotBookedButSetsTheBaseline()
    {
        var state = new StateObject();
        state.ExtractedInfo.OnAlphaNetwork = true;
        await CopperScoreboard(state, "Copper: 500");
        state.ExtractedInfo.CopperSeenAt = System.DateTime.UtcNow.AddSeconds(-30);
        await CopperScoreboard(state, "Copper: 620");
        state.ExtractedInfo.Copper.Should().Be(620);
        state.ItemsCollectedRecently.Should().NotContainKey(Tasks.PseudoItems.COPPER);
    }
}
