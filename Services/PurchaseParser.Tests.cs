using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class PurchaseParserTests
{
    // Regression: Coflnet.Sky.Core.CoinParser.ParseCoinAmount returns tenths of a coin (x10, see
    // CoinCounterParser.Tests.cs). Purchase evidence weighs against the coin value of collected items
    // in TaskClassifier, so it must be the literal chat amount in whole coins, not 10x of it.

    [Test]
    public void AuctionPurchase_Parses()
    {
        PurchaseParser.TryParse("You purchased Hyperion for 970,000,000 coins!", out var kind, out var coins).Should().BeTrue();
        kind.Should().Be(PurchaseKind.Auction);
        coins.Should().Be(970_000_000);
    }

    [Test]
    public void BazaarBought_Parses()
    {
        PurchaseParser.TryParse("[Bazaar] Bought 64x Enchanted Wheat for 20,000 coins!", out var kind, out var coins).Should().BeTrue();
        kind.Should().Be(PurchaseKind.Bazaar);
        coins.Should().Be(20_000);
    }

    [Test]
    public void BazaarClaimed_Parses()
    {
        PurchaseParser.TryParse("[Bazaar] Claimed 64x Enchanted Wheat worth 20,000 coins bought for 312.5 each!", out var kind, out var coins).Should().BeTrue();
        kind.Should().Be(PurchaseKind.Bazaar);
        coins.Should().Be(20_000);
    }

    [TestCase("[Bazaar] Your Enchanted Wheat x10 sold for 6,000 coins!")]
    [TestCase("[Bazaar] Sold 64x Enchanted Wheat for 20,000 coins!")]
    [TestCase("[Bazaar] Sell Offer Setup!")]
    public void SellLines_NeverMatch(string line)
    {
        PurchaseParser.TryParse(line, out _, out _).Should().BeFalse("a sell line must never be treated as a purchase");
    }

    [Test]
    public void FormattedLine_WithColorCodes_StillParses()
    {
        PurchaseParser.TryParse("§aYou purchased §6Hyperion §afor §6970,000,000 coins§a!", out var kind, out var coins).Should().BeTrue();
        kind.Should().Be(PurchaseKind.Auction);
        coins.Should().Be(970_000_000);
    }

    [Test]
    public void AbbreviatedAmount_1_5M_Parses()
    {
        // "M"/"B" (uppercase) and "k" (lowercase) are the only abbreviation suffixes ParseCoinAmount
        // recognizes - matches real Hypixel chat formatting ("1.5M", not "1.5m").
        PurchaseParser.TryParse("You purchased Midas' Sword for 1.5M coins!", out _, out var coins).Should().BeTrue();
        coins.Should().Be(1_500_000);
    }

    [Test]
    public void CommaAmount_12345_Parses()
    {
        PurchaseParser.TryParse("[Bazaar] Bought 1x Jasper Crystal for 12,345 coins!", out _, out var coins).Should().BeTrue();
        coins.Should().Be(12_345);
    }

    [Test]
    public void UnrelatedLine_DoesNotMatch()
    {
        PurchaseParser.TryParse("Welcome to SkyBlock!", out _, out _).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void EmptyOrWhitespace_DoesNotMatch(string line)
    {
        PurchaseParser.TryParse(line, out _, out _).Should().BeFalse();
    }
}
