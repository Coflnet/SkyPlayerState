using System.Collections.Generic;
using Coflnet.Sky.Bazaar.Client.Model;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The one place that merges bazaar and clean (auction) prices into the lookup used for valuing items.
/// Bazaar sell prices win when they are positive, clean prices only fill tags the bazaar has no price for.
/// </summary>
public static class PriceMerge
{
    public static Dictionary<string, double> Merge(IEnumerable<ItemPrice> bazaar,
        IReadOnlyDictionary<string, double> clean, double copperValue)
    {
        var prices = new Dictionary<string, double>();
        AddBazaar(prices, bazaar);
        AddCleanForMissing(prices, clean);
        // copper is no market item: value it from the SkyMart derivation
        prices[PseudoItems.COPPER] = copperValue;
        return prices;
    }

    private static void AddBazaar(Dictionary<string, double> prices, IEnumerable<ItemPrice> bazaar)
    {
        if (bazaar == null)
            return;
        foreach (var item in bazaar)
        {
            // products without a sell price come as 0, which would value the item as worthless
            if (item?.ProductId != null && item.SellPrice > 0)
                prices[item.ProductId] = (double)item.SellPrice;
        }
    }

    private static void AddCleanForMissing(Dictionary<string, double> prices, IReadOnlyDictionary<string, double> clean)
    {
        if (clean == null)
            return;
        foreach (var item in clean)
        {
            if (item.Value > 0 && !prices.ContainsKey(item.Key))
                prices[item.Key] = item.Value;
        }
    }
}
