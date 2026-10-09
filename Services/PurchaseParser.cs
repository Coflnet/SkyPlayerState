using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Kind of purchase <see cref="PurchaseParser"/> detected.
/// </summary>
public enum PurchaseKind
{
    /// <summary>Auction House purchase ("You purchased ... for ... coins!").</summary>
    Auction,
    /// <summary>Bazaar instant buy or buy-order claim ("[Bazaar] Bought/Claimed ...").</summary>
    Bazaar
}

/// <summary>
/// Parses chat lines that indicate the player SPENT coins buying something (Auction House purchase,
/// Bazaar instant buy, Bazaar buy-order claim) - see <see cref="PurchaseListener"/>, which feeds the
/// result into <see cref="Tasks.PseudoItems.AUCTION_PURCHASE"/>/<see cref="Tasks.PseudoItems.BAZAAR_PURCHASE"/>.
/// Kept as a standalone static class (mirroring <see cref="CoinCounterParser"/>) so every regex is
/// independently unit testable without the full listener/UpdateArgs plumbing.
/// </summary>
public static class PurchaseParser
{
    // "You purchased Hyperion for 970,000,000 coins!" - this exact line/shape is already relied on
    // by SkyModCommands/Services/PreApiService.cs:354, do not change its wording without checking there.
    private static readonly Regex AuctionPurchaseRegex = new(
        @"^You purchased (.+) for ([\d,\.kmb]+) coins!?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Never match a sell line - checked before the buy patterns below so a wording overlap can never
    // accidentally fall through into a false positive purchase.
    private static readonly Regex BazaarSellRegex = new(
        @"^\[Bazaar\] (Your .+ sold for|Sold |Sell Offer Setup)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Tolerant of "Bought"/"Claimed" and "for"/"worth" (exact Hypixel wording may vary slightly):
    // "[Bazaar] Bought 64x Enchanted Wheat for 20,000 coins!"
    // "[Bazaar] Claimed 64x Enchanted Wheat worth 20,000 coins bought for 312.5 each!"
    private static readonly Regex BazaarBuyRegex = new(
        @"^\[Bazaar\] (?:Bought|Claimed) ([\d,]+)x (.+?) (?:for|worth) ([\d,\.kmb]+) coins(?: bought for [\d,\.kmb]+ each)?!?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// <see cref="Core.CoinParser.ParseCoinAmount"/> returns tenths of a coin (it scales by 10 for the
    /// transaction domain), the purchase evidence is counted in whole coins - same conversion as
    /// CoinParser.TryParseFromDescription.
    /// </summary>
    private static long ParseCoins(string amount) => Core.CoinParser.ParseCoinAmount(amount) / 10;

    private static readonly Regex FormatCodeRegex = new(@"§.", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to parse a chat line as a coin-spending purchase.
    /// </summary>
    /// <param name="chatLine">raw (possibly §-formatted) chat line</param>
    /// <param name="kind">the kind of purchase, when one was found</param>
    /// <param name="coins">coins spent, when one was found</param>
    public static bool TryParse(string chatLine, out PurchaseKind kind, out long coins)
    {
        kind = default;
        coins = 0;
        if (string.IsNullOrWhiteSpace(chatLine))
            return false;

        var line = StripFormatting(chatLine).Trim();

        var auctionMatch = AuctionPurchaseRegex.Match(line);
        if (auctionMatch.Success)
        {
            kind = PurchaseKind.Auction;
            coins = ParseCoins(auctionMatch.Groups[2].Value);
            return true;
        }

        if (BazaarSellRegex.IsMatch(line))
            return false;

        var bazaarMatch = BazaarBuyRegex.Match(line);
        if (bazaarMatch.Success)
        {
            kind = PurchaseKind.Bazaar;
            coins = ParseCoins(bazaarMatch.Groups[3].Value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Like <see cref="TryParse"/> for the Bazaar buy lines only, additionally returning what was bought: the
    /// amount and the item display name ("[Bazaar] Bought 64x Enchanted Diamond for 10,240 coins!"). The name still
    /// has to be resolved to a tag (<c>BazaarOrderListener.GetTagForName</c>).
    /// </summary>
    public static bool TryParseBazaarBuy(string chatLine, out int amount, out string itemName, out long coins)
    {
        amount = 0;
        itemName = null;
        coins = 0;
        if (string.IsNullOrWhiteSpace(chatLine))
            return false;
        var line = StripFormatting(chatLine).Trim();
        if (BazaarSellRegex.IsMatch(line))
            return false;
        var match = BazaarBuyRegex.Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value.Replace(",", ""), out amount) || amount <= 0)
            return false;
        itemName = match.Groups[2].Value.Trim();
        coins = ParseCoins(match.Groups[3].Value);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryParse"/> for the Auction House line only, additionally returning the item display name as written in
    /// chat ("You purchased §6[Lvl 100] Golden Dragon for 1,000 coins!"), colour codes stripped. It is the same decorated name the
    /// item has in the "BIN Auction View"/"Confirm Purchase" screens, but carries no amount.
    /// </summary>
    public static bool TryParseAuctionBuy(string chatLine, out string itemName)
    {
        itemName = null;
        if (string.IsNullOrWhiteSpace(chatLine))
            return false;
        var match = AuctionPurchaseRegex.Match(StripFormatting(chatLine).Trim());
        if (!match.Success)
            return false;
        itemName = match.Groups[1].Value.Trim();
        return itemName.Length > 0;
    }

    /// <summary>Removes the section sign colour/format codes of a Minecraft display name.</summary>
    public static string StripFormatting(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : FormatCodeRegex.Replace(value, "");
}
