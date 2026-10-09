using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Turns a chest title into the bounded key menus are sampled under.
/// Rules, applied in this order (real title shapes were taken from the chest names the listeners match on):
/// <list type="number">
/// <item>colour codes (§x) are removed, whitespace is collapsed and trimmed;</item>
/// <item>titles that embed a player name or a searched/viewed item name are cut down to their fixed part:
/// trade windows ("You          Player" -> "Trade"), auction searches (Auctions: "term" -> Auctions: "*"),
/// bazaar product pages ("Bazaar ➜ Item" / "Item ➜ Instant Buy" -> "Bazaar ➜ *" / "* ➜ Instant Buy"),
/// any remaining quoted text ("..." -> "*"), a leading possessive player name for the known per-player
/// menus ("Player's Profile" style, see <see cref="PlayerMenus"/>);</item>
/// <item>every number (also "1,234" and "1.5") becomes <c>#</c>, so "(1/3) Loadouts" -> "(#/#) Loadouts",
/// "Backpack (Slot 12)" -> "Backpack (Slot #)", "Ender Chest (4/9)" -> "Ender Chest (#/#)";</item>
/// <item>the result is cut to <see cref="MaxLength"/> characters.</item>
/// </list>
/// Anything else (an NPC name, a menu name) is kept as is; the store's cap on distinct names bounds the rest.
/// </summary>
public static class MenuTitleNormalizer
{
    public const int MaxLength = 64;

    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    // trade window titles are "You" + padding + the partner's name
    private static readonly Regex Trade = new(@"^You\s{3,}\S", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\".*\"", RegexOptions.Compiled);
    private static readonly Regex BazaarProduct = new(@"^Bazaar ➜ .+$", RegexOptions.Compiled);
    private static readonly Regex BazaarAction = new(@"^.+ ➜ (Instant Buy|Instant Sell|Buy Order|Sell Offer|Custom Amount|Confirm.*)$", RegexOptions.Compiled);
    // "<name>'s Profile" style titles of menus that show another player's data
    private static readonly Regex PlayerMenus = new(@"^[A-Za-z0-9_]{1,16}'s? (Profile|Auctions|Island|Skyblock Profile|Stats)$", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"\d[\d,.]*", RegexOptions.Compiled);

    public static string StripColors(string text) => ColorCodes.Replace(text, "");

    /// <summary>The normalized key, or an empty string when the title has no content.</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        var stripped = StripColors(title).Trim();
        // checked before the whitespace collapse: the padding is what tells a trade from a menu named "You ..."
        if (Trade.IsMatch(stripped))
            return "Trade";
        var text = Whitespace.Replace(stripped, " ");
        if (BazaarProduct.IsMatch(text))
            text = "Bazaar ➜ *";
        else
        {
            var action = BazaarAction.Match(text);
            if (action.Success)
                text = "* ➜ " + action.Groups[1].Value;
        }
        var player = PlayerMenus.Match(text);
        if (player.Success)
            text = "<player>'s " + player.Groups[1].Value;
        text = Quoted.Replace(text, "\"*\"");
        text = Numbers.Replace(text, "#");
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }
}
