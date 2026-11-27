using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Coin value of a Kuudra key, which has no market price (Co-op Soulbound). Opening a Kuudra paid chest buys the key on the
/// spot: Odin's KuudraTracker takes a counted paid chest back on "You cannot afford this!" in the chest GUI, and
/// production never shows a key leaving the inventory. <see cref="Services.DungeonRewardListener"/> therefore books the
/// coins of a confirmed paid chest open; <see cref="BaseKuudraTask.ScaledFormulaCost"/> adds that open's materials, charges a
/// key taken from the inventory and every unconfirmed paid chest in full. Keys are valued nowhere else: a key gained
/// outside Kuudra (lava fishing treasure) must not become loot.
/// <para>
/// Sources: the coins are what the Kuudra profit trackers of Odin (Vesuvius.kt keys: 155,200 / 310,400 / 582,000 /
/// 1,164,000 / 2,328,000) and vexo (KuudraProfitTracker.kt: 2,328,000 for Infernal) charge, i.e. the listed Emissary
/// prices (NEU npc_shop recipes in items/BARBARIAN_EMISSARY_NPC.json: 200k / 400k / 750k / 1.5M / 3M) times 0.776, the
/// price at full reputation (the wiki states up to 20% off by reputation). Being the lowest price a player pays, it is
/// also the purse drop that confirms an open. Materials per key (NEU, as changed in SkyBlock 0.20.9, commit 2bea137a2;
/// Odin uses the same counts): 2 / 4 / 16 / 40 / 80 Enchanted Red Sand (Barbarian) or Enchanted Mycelium (Mage) and 2
/// Corrupted Nether Stars.
/// </para>
/// The materials are valued with the caller's price lookup, so the key follows their market price; the faction
/// material is the cheaper of the two, since the faction is not known.
/// </summary>
public static class KuudraKeyCost
{
    public const string NetherStar = "CORRUPTED_NETHER_STAR";
    public const int NetherStarsPerKey = 2;
    public static readonly string[] FactionMaterials = ["ENCHANTED_RED_SAND", "ENCHANTED_MYCELIUM"];
    /// <summary>Share of the listed coins paid at full reputation, see the class summary.</summary>
    public const double FullReputationPriceShare = 0.776;

    /// <summary>One key: its tier, tag, name in the paid chest's Cost section, listed coins and faction material count.</summary>
    public record KeyPrice(int Tier, string Tag, string Name, long ListedCoins, int FactionMaterial)
    {
        /// <summary>Coins paid for the key at full reputation.</summary>
        public long Coins => (long)Math.Round(ListedCoins * FullReputationPriceShare);
        /// <summary>The most a key may cost: its listed price (no reputation) plus 1% tolerance.</summary>
        public long MaxCoins => (long)(ListedCoins * 1.01);
    }

    /// <summary>Key prices by Kuudra tier, index 0 is Basic (T1).</summary>
    private static readonly KeyPrice[] Tiers =
    [
        new(1, "KUUDRA_TIER_KEY", "Kuudra Key", 200_000, 2),
        new(2, "KUUDRA_HOT_TIER_KEY", "Hot Kuudra Key", 400_000, 4),
        new(3, "KUUDRA_BURNING_TIER_KEY", "Burning Kuudra Key", 750_000, 16),
        new(4, "KUUDRA_FIERY_TIER_KEY", "Fiery Kuudra Key", 1_500_000, 40),
        new(5, "KUUDRA_INFERNAL_TIER_KEY", "Infernal Kuudra Key", 3_000_000, 80),
    ];

    private static readonly Dictionary<string, KeyPrice> ByTag = Tiers.ToDictionary(t => t.Tag, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, KeyPrice> ByName = Tiers.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key tag, Basic first.</summary>
    public static IEnumerable<string> KeyTags => Tiers.Select(t => t.Tag);

    /// <summary>The key opening the paid chest of <paramref name="tier"/> (1 Basic .. 5 Infernal), null for any other tier.</summary>
    public static KeyPrice OfTier(int tier) => tier >= 1 && tier <= Tiers.Length ? Tiers[tier - 1] : null;

    /// <summary>The key a paid chest's Cost line names ("Infernal Kuudra Key"), null for any other line.</summary>
    public static KeyPrice OfName(string costLine)
        => costLine != null && ByName.TryGetValue(costLine.Trim(), out var key) ? key : null;

    public static bool IsKey(string tag) => tag != null && ByTag.ContainsKey(tag);

    /// <summary>
    /// Coins plus the current value of the materials of one <paramref name="tag"/> key, false for anything that is no
    /// Kuudra key. <paramref name="priceOf"/> prices the materials, an unpriced material adds nothing.
    /// </summary>
    public static bool TryGetValue(string tag, Func<string, double> priceOf, out double value)
    {
        value = 0;
        if (tag == null || !ByTag.TryGetValue(tag, out var key))
            return false;
        value = key.Coins + MaterialValue(key, priceOf);
        return true;
    }

    /// <summary>Current value of the materials one <paramref name="key"/> takes besides its coins.</summary>
    public static double MaterialValue(KeyPrice key, Func<string, double> priceOf)
        => key.FactionMaterial * CheapestFactionMaterial(priceOf) + NetherStarsPerKey * PositivePrice(priceOf, NetherStar);

    /// <summary>
    /// <paramref name="priceOf"/> with the Kuudra keys resolved to <see cref="TryGetValue"/> first, before whatever price the
    /// lookup has for them.
    /// </summary>
    public static Func<string, double> WithKeyValues(Func<string, double> priceOf)
        => tag => TryGetValue(tag, priceOf, out var value) ? value : priceOf(tag);

    private static double CheapestFactionMaterial(Func<string, double> priceOf)
    {
        var priced = FactionMaterials.Select(m => PositivePrice(priceOf, m)).Where(p => p > 0).ToList();
        return priced.Count == 0 ? 0 : priced.Min();
    }

    private static double PositivePrice(Func<string, double> priceOf, string tag)
    {
        var price = priceOf?.Invoke(tag) ?? 0;
        return price > 0 ? price : 0;
    }
}
