using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Turns a chest title into the bounded key menus are sampled under.
/// Rules, applied in this order (real title shapes were taken from the chest names the listeners match on):
/// <list type="number">
/// <item>colour codes (§x) are removed, whitespace is collapsed and trimmed;</item>
/// <item>titles that embed a player name or a searched/viewed item name are cut down to their fixed part:
/// trade windows ("You          Player" -> "Trade"), guest profiles ("Steve's Profile [GUEST]" -> "&lt;player&gt;'s Profile [GUEST]"),
/// auction searches (Auctions: "term" -> Auctions: "*", also when the game cut the closing quote off),
/// bazaar product pages ("Bazaar ➜ Item" / "Item ➜ Instant Buy" -> "Bazaar ➜ *" / "* ➜ Instant Buy"),
/// drill-downs ("Shards ➜ Torrid Shard" -> "Shards ➜ *"), the item/tier named menus
/// ("Coal Minion VII" -> "* Minion #", "Mangrove Log VI Rewards" -> "* # Rewards", "X Collection", "X Sack", "X Recipes",
/// "X Minion Recipes", "Abiphone XIII Jade", "Visit name", "Profile: Fruit", "X Slayer LVL Rewards"),
/// any remaining quoted text ("..." -> "*"), a leading possessive player name for the known per-player
/// menus ("Player's Profile" style, see <see cref="PlayerMenus"/>);</item>
/// <item>numbers (also "1,234" and "1.5") become <c>#</c>, so "Backpack (Slot 12)" -> "Backpack (Slot #)". Two things are
/// kept: the dungeon floor of "(F7)"/"(M4)" and the current page of a page counter up to page <see cref="KeptPageUpTo"/>
/// ("(2/5) Loadouts" -> "(2/#) Loadouts", page 12 -> "(#/#)"), so page 2 of a menu is sampled on its own. Roman floors
/// ("Catacombs - Floor VII") stay as they are;</item>
/// <item>the result is cut to <see cref="MaxLength"/> characters.</item>
/// </list>
/// Anything else (an NPC name, a menu name) is kept as is; the store's cap on distinct names bounds the rest.
/// </summary>
public static class MenuTitleNormalizer
{
    public const int MaxLength = 64;
    /// <summary>Page numbers up to this one stay in the key.</summary>
    public const int KeptPageUpTo = 5;

    private const string Roman = "[IVXLC]+";
    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    // trade window titles are "You" + padding + the partner's name
    private static readonly Regex Trade = new(@"^You\s{3,}\S", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\".*\"", RegexOptions.Compiled);
    private static readonly Regex AuctionSearch = new("^(Auctions: )\".*$", RegexOptions.Compiled);
    private static readonly Regex BazaarProduct = new(@"^Bazaar ➜ .+$", RegexOptions.Compiled);
    private static readonly Regex BazaarAction = new(@"^.+ ➜ (Instant Buy|Instant Sell|Buy Order|Sell Offer|Custom Amount|Confirm.*)$", RegexOptions.Compiled);
    private static readonly Regex DrillDown = new(@"^(.+?) ➜ .+$", RegexOptions.Compiled);
    // "<name>'s Profile" style titles of menus that show another player's data
    private static readonly Regex PlayerMenus = new(@"^[A-Za-z0-9_]{1,16}'s? (Profile|Auctions|Island|Skyblock Profile|Stats)$", RegexOptions.Compiled);
    private static readonly Regex GuestProfile = new(@"^[A-Za-z0-9_]{1,16}'s? Profile \[GUEST\]$", RegexOptions.Compiled);
    private static readonly Regex PagePrefix = new(@"^(\(\d+/\d+\) )(.*)$", RegexOptions.Compiled);
    // (page counter) | (floor, kept) | any other number
    private static readonly Regex Numbers = new(@"\((\d+)/(\d+)\)|\(([FM]\d)\)|\d[\d,.]*", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Replacement)[] ItemMenus =
    [
        (new Regex($@"^.+ Minion ({Roman}|\d+)$", RegexOptions.Compiled), "* Minion #"),
        (new Regex(@"^.+ Slayer LVL Rewards$", RegexOptions.Compiled), "* Slayer LVL Rewards"),
        (new Regex($@"^.+ {Roman} Rewards$", RegexOptions.Compiled), "* # Rewards"),
        (new Regex(@"^.+ Collection$", RegexOptions.Compiled), "* Collection"),
        (new Regex(@"^.+ Minion Recipes$", RegexOptions.Compiled), "* Minion Recipes"),
        (new Regex(@"^.+ Recipes$", RegexOptions.Compiled), "* Recipes"),
        (new Regex(@"^.+ Sack$", RegexOptions.Compiled), "* Sack"),
        (new Regex($@"^Abiphone {Roman}$", RegexOptions.Compiled), "Abiphone #"),
        (new Regex($@"^Abiphone {Roman} .+$", RegexOptions.Compiled), "Abiphone # *"),
        (new Regex(@"^Visit .+$", RegexOptions.Compiled), "Visit *"),
        (new Regex(@"^Profile: .+? \(Co-op\)$", RegexOptions.Compiled), "Profile: * (Co-op)"),
        (new Regex(@"^Profile: .+$", RegexOptions.Compiled), "Profile: *"),
    ];

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
        var pageMatch = PagePrefix.Match(text);
        var prefix = pageMatch.Success ? pageMatch.Groups[1].Value : "";
        var body = pageMatch.Success ? pageMatch.Groups[2].Value : text;
        body = NormalizeBody(body);
        text = prefix + body;
        text = Numbers.Replace(text, KeepOrCollapse);
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    private static string KeepOrCollapse(Match m)
    {
        if (m.Groups[3].Success)
            return m.Value; // (F7) / (M4)
        if (m.Groups[1].Success)
            return int.TryParse(m.Groups[1].Value, out var page) && page <= KeptPageUpTo ? $"({page}/#)" : "(#/#)";
        return "#";
    }

    private static string NormalizeBody(string text)
    {
        if (GuestProfile.IsMatch(text))
            return "<player>'s Profile [GUEST]";
        if (AuctionSearch.IsMatch(text))
            return AuctionSearch.Replace(text, "$1\"*\"");
        if (BazaarProduct.IsMatch(text))
            return "Bazaar ➜ *";
        var action = BazaarAction.Match(text);
        if (action.Success)
            return "* ➜ " + action.Groups[1].Value;
        var drill = DrillDown.Match(text);
        if (drill.Success)
            return drill.Groups[1].Value + " ➜ *";
        foreach (var (pattern, replacement) in ItemMenus)
            if (pattern.IsMatch(text))
                return replacement;
        var player = PlayerMenus.Match(text);
        if (player.Success)
            return "<player>'s " + player.Groups[1].Value;
        return Quoted.Replace(text, "\"*\"");
    }
}
