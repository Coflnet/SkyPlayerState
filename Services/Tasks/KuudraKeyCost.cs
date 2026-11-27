using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Coin value of a Kuudra key, which has no market price (Co-op Soulbound) and never shows up as a consumed item: the
/// key is used from the Kuudra menu, not the inventory. Its value is what the Emissary shop charges, so a Kuudra tier
/// can charge it per run through <see cref="BaseKuudraTask"/>'s FormulaCosts.
/// <para>
/// Sources: NEU repo items/BARBARIAN_EMISSARY_NPC.json and MAGE_EMISSARY_NPC.json (npc_shop recipes, materials as
/// changed in SkyBlock 0.20.9, NEU commit 2bea137a2): 200k / 400k / 750k / 1.5M / 3M coins, 2 / 4 / 16 / 40 / 80
/// Enchanted Red Sand (Barbarian) or Enchanted Mycelium (Mage) and 2 Corrupted Nether Stars per key. The Mage Basic key's
/// 7,600 coins in NEU looks like a bad entry (the same file listed 38,000 for the Hot key until NEU fixed it to 400,000); the wiki
/// (https://hypixelskyblock.minecraft.wiki/w/Kuudra_Key) lists 200,000 for both. The wiki also states the Emissaries give
/// up to 20% off the coins by reputation (Barbarian_Emissary page: 1k / 3k / 7k / 12k reputation = 5 / 10 / 15 / 20%),
/// so every tier is charged the discount of the reputation it requires to be played: T5 needs 12,000, T5 keys cost
/// 2.4M coins.
/// </para>
/// The materials are valued with the caller's price lookup, so the key follows their market price; the faction
/// material is the cheaper of the two, since the faction is not known.
/// </summary>
public static class KuudraKeyCost
{
    public const string NetherStar = "CORRUPTED_NETHER_STAR";
    public const int NetherStarsPerKey = 2;
    public static readonly string[] FactionMaterials = ["ENCHANTED_RED_SAND", "ENCHANTED_MYCELIUM"];

    /// <summary>Shop price of one key: listed coins, the coin discount at the tier's reputation, faction material count.</summary>
    public record KeyPrice(string Tag, double ListedCoins, double CoinDiscount, int FactionMaterial)
    {
        public double Coins => ListedCoins * (1 - CoinDiscount);
    }

    /// <summary>Key prices by Kuudra tier, index 0 is Basic (T1).</summary>
    private static readonly KeyPrice[] Tiers =
    [
        new("KUUDRA_TIER_KEY", 200_000, 0, 2),
        new("KUUDRA_HOT_TIER_KEY", 400_000, 0.05, 4),
        new("KUUDRA_BURNING_TIER_KEY", 750_000, 0.10, 16),
        new("KUUDRA_FIERY_TIER_KEY", 1_500_000, 0.15, 40),
        new("KUUDRA_INFERNAL_TIER_KEY", 3_000_000, 0.20, 80),
    ];

    private static readonly Dictionary<string, KeyPrice> ByTag = Tiers.ToDictionary(t => t.Tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The key opening the paid chest of <paramref name="tier"/> (1 Basic .. 5 Infernal).</summary>
    public static KeyPrice OfTier(int tier) => Tiers[tier - 1];

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
        value = key.Coins + key.FactionMaterial * CheapestFactionMaterial(priceOf) + NetherStarsPerKey * PositivePrice(priceOf, NetherStar);
        return true;
    }

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
