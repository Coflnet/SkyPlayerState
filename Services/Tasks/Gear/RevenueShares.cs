using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// How a task's NET revenue splits by what drives it, as signed fractions summing to 1 (the community rate is
/// net of costs, so the shares must be too): Bulk is common drops minus the recipe cost they need (scales with the
/// bulk factor), Rare the magic find affected drops, Throughput the unitemized coins minus consumed items such as
/// keys or bait (scales with actions per hour). Taken from the merged all-bucket aggregate, so it holds even
/// when the community estimate publishes no drop list (fewer than 3 contributors). Itemized is false without
/// observed revenue;
/// NetPositive is false when the aggregate nets nothing or a loss, so no setup can be scaled from it.
/// </summary>
public readonly record struct RevenueShares(double Bulk, double Rare, double Throughput, bool Itemized, bool NetPositive = true)
{
    /// <summary>No observed revenue: everything is assumed to scale like the common drops.</summary>
    public static RevenueShares Unknown => new(1, 0, 0, false);

    /// <summary>Observed revenue that nets zero or less.</summary>
    public static RevenueShares NoNetRevenue => new(0, 0, 0, true, false);

    /// <summary>
    /// Shares from the aggregate, priced live like <see cref="TaskEstimator.RateParts"/>: positive item value of
    /// <paramref name="rareDropTags"/> is rare, other positive item value bulk, negative item value (consumed
    /// items) a throughput cost, the recipe cost (<see cref="MethodTask.ScaledFormulaCost"/>) a bulk cost, and the
    /// drift-scaled rare and residual pools rare and throughput revenue.
    /// </summary>
    public static RevenueShares FromAggregate(MethodTask? task, BucketAggregate? aggregate,
        Dictionary<string, double>? prices, IReadOnlyCollection<string>? rareDropTags)
    {
        if (aggregate == null || aggregate.WSeconds <= 0)
            return Unknown;
        var parts = TaskEstimator.RateParts(task!, aggregate, prices!); // both handle null
        var rareTags = new HashSet<string>(rareDropTags ?? [], StringComparer.OrdinalIgnoreCase);
        double rareItems = 0, bulkItems = 0, itemCosts = 0;
        foreach (var (tag, count) in aggregate.ItemCounts)
        {
            var value = count * (prices?.GetValueOrDefault(tag) ?? 0);
            if (value < 0)
                itemCosts -= value;
            else if (rareTags.Contains(tag))
                rareItems += value;
            else
                bulkItems += value;
        }
        return FromComponents(bulkItems, rareItems, parts.ScaledRareCoins, parts.ScaledResidualCoins,
            parts.FormulaCost, itemCosts);
    }

    /// <summary>The share math on coin totals (any common time basis); costs are positive numbers.</summary>
    public static RevenueShares FromComponents(double bulkItems, double rareItems, double rarePool, double residualPool,
        double formulaCost, double itemCosts)
    {
        var bulk = bulkItems - formulaCost;
        var rare = rareItems + rarePool;
        var throughput = residualPool - itemCosts;
        var activity = Math.Abs(bulkItems) + Math.Abs(rareItems) + Math.Abs(rarePool) + Math.Abs(residualPool)
                       + Math.Abs(formulaCost) + Math.Abs(itemCosts);
        if (activity <= 0 || !double.IsFinite(activity))
            return Unknown;
        var net = bulk + rare + throughput;
        if (!(net > 0) || !double.IsFinite(net))
            return NoNetRevenue;
        return new RevenueShares(bulk / net, rare / net, throughput / net, true);
    }
}
