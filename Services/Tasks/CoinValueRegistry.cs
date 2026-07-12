using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Resolves the coin equivalent value of any tracked resource tag.
/// Resolution order:
/// 1. explicit override from the resource_coin_values table (powder and other non market resources)
/// 2. market price passed by the caller (merged bazaar sell + auction clean prices)
/// 3. zero, counted in a metric so new unpriced resources are visible instead of silently dropped
/// </summary>
public class CoinValueRegistry
{
    private Table<ResourceCoinValue> overrideTable;
    private readonly ISession session;
    private readonly ILogger<CoinValueRegistry> logger;
    private Dictionary<string, double> cachedOverrides;
    private DateTime overridesFetchedAt = DateTime.MinValue;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Prometheus.Counter UnpricedCounter = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_unpriced_resource_total",
        "Resources that could not be converted to coins because no price or override exists",
        "tag");

    /// <summary>
    /// Default conversion rates written to the override table when it is empty.
    /// Pseudo tags (powder) have no market price; values are coins per unit.
    /// </summary>
    private static readonly Dictionary<string, double> SeedValues = new()
    {
        { "MITHRIL_POWDER", 0.2 },
        { "GEMSTONE_POWDER", 0.5 },
        { "GLACITE_POWDER", 0.5 },
    };

    public CoinValueRegistry(ISession session, ILogger<CoinValueRegistry> logger)
    {
        this.session = session;
        this.logger = logger;
    }

    /// <summary>
    /// Coin equivalent for one unit of <paramref name="tag"/>.
    /// <paramref name="marketPrices"/> is the merged clean price lookup the caller already has.
    /// </summary>
    public double Value(string tag, Dictionary<string, double> marketPrices)
    {
        var overrides = cachedOverrides;
        if (overrides != null && overrides.TryGetValue(tag, out var overrideValue))
            return overrideValue;
        if (marketPrices != null && marketPrices.TryGetValue(tag, out var marketPrice) && marketPrice > 0)
            return marketPrice;
        UnpricedCounter.WithLabels(tag).Inc();
        return 0;
    }

    /// <summary>
    /// Refreshes the override cache if it is stale. Call before batch valuations;
    /// <see cref="Value"/> itself is synchronous and never blocks on IO.
    /// </summary>
    public async Task EnsureFresh()
    {
        if (DateTime.UtcNow - overridesFetchedAt < CacheDuration)
            return;
        await refreshLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - overridesFetchedAt < CacheDuration)
                return;
            if (overrideTable == null)
                await Setup();
            var rows = (await overrideTable!.ExecuteAsync()).ToList();
            cachedOverrides = rows.ToDictionary(r => r.Tag, r => r.CoinsPerUnit);
            overridesFetchedAt = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            // keep serving the stale cache, retry in a minute
            overridesFetchedAt = DateTime.UtcNow - CacheDuration + TimeSpan.FromMinutes(1);
            logger.LogError(e, "could not refresh resource coin values, serving {count} stale entries", cachedOverrides?.Count ?? 0);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task SetOverride(string tag, double coinsPerUnit, string note = null)
    {
        if (overrideTable == null)
            await Setup();
        await overrideTable!.Insert(new ResourceCoinValue
        {
            Tag = tag,
            CoinsPerUnit = coinsPerUnit,
            Note = note,
            UpdatedAt = DateTime.UtcNow
        }).ExecuteAsync();
        overridesFetchedAt = DateTime.MinValue; // force refresh on next EnsureFresh
    }

    private async Task Setup()
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<ResourceCoinValue>()
                .PartitionKey(v => v.Tag)
            );
        overrideTable = new Table<ResourceCoinValue>(session, mapping, "resource_coin_values");
        await overrideTable.CreateIfNotExistsAsync();
        var any = (await overrideTable.Take(1).ExecuteAsync()).Any();
        if (!any)
        {
            foreach (var seed in SeedValues)
            {
                await overrideTable.Insert(new ResourceCoinValue
                {
                    Tag = seed.Key,
                    CoinsPerUnit = seed.Value,
                    Note = "seed default",
                    UpdatedAt = DateTime.UtcNow
                }).ExecuteAsync();
            }
        }
    }
}

public class ResourceCoinValue
{
    public string Tag { get; set; }
    public double CoinsPerUnit { get; set; }
    public string Note { get; set; }
    public DateTime UpdatedAt { get; set; }
}
