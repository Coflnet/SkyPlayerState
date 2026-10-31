using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Coin value of one copper (Garden currency), derived from what SkyMart sells for copper. Pure derivation
/// (<see cref="Derive"/>) plus the last derived value, kept in a static so the synchronous
/// <see cref="CoinValueRegistry.Value"/> can read it without IO; <see cref="CopperValueRefresher"/> updates it hourly.
/// </summary>
public static class CopperValue
{
    /// <summary>Used until a derivation succeeded or when fewer than <see cref="MinimumSamples"/> items qualify.</summary>
    public const double Fallback = 1000;
    public const int MinimumSamples = 3;
    /// <summary>Cheap items have absurd, illiquid ratios, only items costing at least this much copper count.</summary>
    public const int MinimumCopperCost = 100;
    private const string CopperCostKey = "Copper";

    private static CopperValueInfo current = new() { CoinsPerCopper = Fallback, IsFallback = true, DerivedAt = DateTime.MinValue };

    /// <summary>Current coins per copper.</summary>
    public static double Current => current.CoinsPerCopper;

    /// <summary>Current value together with the items it was derived from.</summary>
    public static CopperValueInfo Info => current;

    public static void Set(CopperValueInfo info) => current = info;

    /// <summary>Resets to the fallback (tests).</summary>
    internal static void Reset() => current = new() { CoinsPerCopper = Fallback, IsFallback = true, DerivedAt = DateTime.MinValue };

    /// <summary>
    /// Median of price * resultCount / copper over the qualifying shop rows: copper is the ONLY cost, copper cost
    /// at least <see cref="MinimumCopperCost"/>, not an ENCHANTED_BOOK (the tag does not say which enchant), market
    /// price above 0, one sample per item tag. Fewer than <see cref="MinimumSamples"/> samples fall back to <see cref="Fallback"/>.
    /// </summary>
    public static CopperValueInfo Derive(IEnumerable<NpcCost> rows, IReadOnlyDictionary<string, double> prices)
    {
        var samples = new Dictionary<string, CopperSample>();
        foreach (var row in rows ?? Enumerable.Empty<NpcCost>())
        {
            if (row?.ItemTag == null || row.ItemTag == "ENCHANTED_BOOK" || row.Costs == null)
                continue;
            if (row.Costs.Count != 1 || !row.Costs.TryGetValue(CopperCostKey, out var copper) || copper < MinimumCopperCost)
                continue;
            if (prices == null || !prices.TryGetValue(row.ItemTag, out var price) || !(price > 0))
                continue;
            if (samples.ContainsKey(row.ItemTag))
                continue;
            var count = Math.Max(1, row.ResultCount);
            samples[row.ItemTag] = new CopperSample
            {
                ItemTag = row.ItemTag,
                Copper = copper,
                ResultCount = count,
                Price = price,
                CoinsPerCopper = price * count / copper
            };
        }
        var ordered = samples.Values.OrderBy(s => s.CoinsPerCopper).ToList();
        if (ordered.Count < MinimumSamples)
            return new CopperValueInfo { CoinsPerCopper = Fallback, IsFallback = true, DerivedAt = DateTime.UtcNow, Items = ordered };
        var mid = ordered.Count / 2;
        var median = ordered.Count % 2 == 1
            ? ordered[mid].CoinsPerCopper
            : (ordered[mid - 1].CoinsPerCopper + ordered[mid].CoinsPerCopper) / 2;
        return new CopperValueInfo { CoinsPerCopper = median, IsFallback = false, DerivedAt = DateTime.UtcNow, Items = ordered };
    }
}

public class CopperValueInfo
{
    /// <summary>Coins one copper is worth.</summary>
    public double CoinsPerCopper { get; set; }
    /// <summary>True when too few SkyMart items qualified and the constant fallback is used.</summary>
    public bool IsFallback { get; set; }
    public DateTime DerivedAt { get; set; }
    /// <summary>The items the value was derived from, cheapest ratio first.</summary>
    public List<CopperSample> Items { get; set; } = new();
}

public class CopperSample
{
    public string ItemTag { get; set; }
    public int Copper { get; set; }
    public int ResultCount { get; set; }
    public double Price { get; set; }
    public double CoinsPerCopper { get; set; }
}

/// <summary>
/// Recomputes <see cref="CopperValue"/> hourly in the background so no update ever waits on IO.
/// </summary>
public class CopperValueRefresher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly RecipeService recipes;
    private readonly TaskPriceService prices;
    private readonly ILogger<CopperValueRefresher> logger;

    public CopperValueRefresher(RecipeService recipes, TaskPriceService prices, ILogger<CopperValueRefresher> logger)
    {
        this.recipes = recipes;
        this.prices = prices;
        this.logger = logger;
    }

    /// <summary>A fallback result (outage, too few samples) never replaces an earlier derived value.</summary>
    internal static bool ShouldReplace(CopperValueInfo existing, CopperValueInfo derived)
        => !derived.IsFallback || existing.IsFallback;

    private static async Task DelayInterval(CancellationToken token)
    {
        try { await Task.Delay(Interval, token); } catch (OperationCanceledException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rows = (await recipes.GetNpcCosts()).ToList();
                var info = CopperValue.Derive(rows, await prices.GetPrices(stoppingToken));
                if (!ShouldReplace(CopperValue.Info, info))
                {
                    logger.LogWarning("copper derivation fell back (price outage?), keeping {value}", CopperValue.Current);
                    await DelayInterval(stoppingToken);
                    continue;
                }
                CopperValue.Set(info);
                logger.LogInformation("Copper is worth {value} coins from {count} SkyMart items (fallback {fallback})",
                    info.CoinsPerCopper, info.Items.Count, info.IsFallback);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "could not derive the copper value, keeping {value}", CopperValue.Current);
            }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }
}
