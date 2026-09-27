using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Synthetic "item" tags injected into <c>ItemsCollected</c>/<c>ItemsCollectedRecently</c> alongside
/// real drops, so the existing collection/classification/folding pipeline can carry non-drop economic
/// signals (money spent buying, money spent on a fixed cost) without every caller needing its own
/// special case. Two kinds:
/// <list type="bullet">
/// <item>
/// <b>EVIDENCE</b> tags (<see cref="BAZAAR_PURCHASE"/>, <see cref="AUCTION_PURCHASE"/>) - a positive
/// count equal to coins SPENT buying something (a Bazaar/Auction House purchase shows up as
/// "collected" the exact same way a real drop does, once the bought item lands in the inventory).
/// Coin value for profit/valuation purposes is always 0 (buying something is not profit - see
/// <see cref="TryGetCoinValue"/> and <see cref="CoinValueRegistry.Value"/>, which must not count this
/// as an unpriced resource either), but the classifier still needs to be able to tell "the player
/// mostly bought stuff" apart from "the player mostly grinded stuff" in the SAME window, so
/// <see cref="ClassifierWeight"/> weighs each of these 1 coin per unit (i.e. by coins spent) purely
/// for tie-breaking, never for coin totals.
/// </item>
/// <item>
/// <b>COST</b> tags (<see cref="DUNGEON_CHEST_COST"/>) - a NEGATIVE count of coins actually spent on
/// a fixed, real cost of doing a task (e.g. opening a dungeon chest). Unlike evidence tags this DOES
/// represent real economic activity, so its coin value is 1 per unit everywhere (profit accounting,
/// <see cref="CoinValueRegistry"/>, classifier weighting) - the sign of the (already negative) count
/// is what turns it into a cost rather than a gain. Defined now for a follow-up change that will
/// produce periods carrying it; nothing yet writes this tag into ItemsCollected.
/// </item>
/// </list>
/// Every task/breakdown that renders raw <c>ItemsCollected</c> to a player (MethodTask.ComputeFromPlayerData)
/// must exclude EVIDENCE tags entirely (they are not drops) and render COST tags as a cost line, not
/// a drop - see the pseudo-tag filtering there. Pseudo tags only ever "belong" to the hidden
/// accounting tasks in HiddenTasks.cs, but a real task's period can legitimately contain them too
/// (e.g. a farmer who also bought seeds on the Bazaar during the same window).
/// </summary>
public static class PseudoItems
{
    public const string BAZAAR_PURCHASE = "BAZAAR_PURCHASE";
    public const string AUCTION_PURCHASE = "AUCTION_PURCHASE";
    public const string DUNGEON_CHEST_COST = "DUNGEON_CHEST_COST";

    private static readonly HashSet<string> EvidenceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        BAZAAR_PURCHASE,
        AUCTION_PURCHASE
    };

    private static readonly HashSet<string> CostTags = new(StringComparer.OrdinalIgnoreCase)
    {
        DUNGEON_CHEST_COST
    };

    /// <summary>True for any pseudo tag (evidence or cost), used to keep them out of user-facing drop breakdowns.</summary>
    public static bool IsPseudo(string tag) => IsEvidence(tag) || IsCost(tag);

    /// <summary>True for an EVIDENCE pseudo tag (coins spent buying something) - see class doc.</summary>
    public static bool IsEvidence(string tag) => tag != null && EvidenceTags.Contains(tag);

    /// <summary>True for a COST pseudo tag (a real, negative-count coin cost) - see class doc.</summary>
    public static bool IsCost(string tag) => tag != null && CostTags.Contains(tag);

    /// <summary>
    /// Coin value for one unit of a pseudo tag: 0 for evidence, 1 for cost. Returns false (and leaves
    /// <paramref name="value"/> at 0) for a real item tag, so callers fall back to their own market
    /// price lookup - this never claims to know the value of a real drop.
    /// </summary>
    public static bool TryGetCoinValue(string tag, out double value)
    {
        if (IsEvidence(tag))
        {
            value = 0;
            return true;
        }
        if (IsCost(tag))
        {
            value = 1;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Weight used by <see cref="TaskClassifier"/> when tie-breaking which task a window belongs to.
    /// Pseudo tags (both evidence and cost) weigh 1 coin per unit - since their COUNT already IS
    /// coins (spent or cost), that lets a purchase-dominated window out-weigh a small real grind
    /// without needing a market price. Real item tags fall back to <paramref name="prices"/>.
    /// </summary>
    public static double ClassifierWeight(string tag, Dictionary<string, double>? prices) =>
        IsPseudo(tag) ? 1 : (prices?.GetValueOrDefault(tag) ?? 0);
}
