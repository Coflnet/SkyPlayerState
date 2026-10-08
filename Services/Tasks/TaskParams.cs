using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.DependencyInjection;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskParams
{
    public DateTime TestTime { get; set; }
    public ExtractedInfo ExtractedInfo { get; set; }
    /// <summary>
    /// Format provider for prices and times.
    /// </summary>
    public ITaskFormatProvider Formatter { get; set; }
    public ConcurrentDictionary<Type, CalculationCache> Cache { get; set; }
    public long MaxAvailableCoins { get; set; } = 1000000000; // Default to 1 billion coins
    public Dictionary<string, Period[]> LocationProfit { get; set; }
    public Dictionary<string, long> CleanPrices { get; set; }
    public List<ItemPrice> BazaarPrices { get; set; }
    public Dictionary<string, string> Names { get; set; }
    /// <summary>
    /// Current mayor name (lowercase), used for accessibility checks on mayor-dependent tasks.
    /// Null if unknown.
    /// </summary>
    public string CurrentMayor { get; set; }

    /// <summary>
    /// Community-aggregated average drop rates per method name.
    /// Used as a middle tier between player-specific data and static formulas.
    /// Key = method name, Value = aggregated drops per hour from all users.
    /// </summary>
    public Dictionary<string, List<AverageDrop>> GlobalAverageDrops { get; set; }

    /// <summary>
    /// Stat aware, saturation adjusted coins per hour estimates computed by SkyUserState,
    /// keyed by task name. This supersedes <see cref="GlobalAverageDrops"/> as the community tier.
    /// Null when the estimate service was unavailable, in which case tasks fall back to formula.
    /// </summary>
    public Dictionary<string, TaskEstimate> ServerEstimates { get; set; }

    /// <summary>
    /// DI service provider for resolving external clients (Kat, Forge, Composter etc.)
    /// </summary>
    public IServiceProvider ServiceProvider { get; set; }

    /// <summary>
    /// Player UUID, available in both WebSocket and REST paths.
    /// </summary>
    public string PlayerUuid { get; set; }

    /// <summary>
    /// Player name, may be same as PlayerUuid when not known.
    /// </summary>
    public string PlayerName { get; set; }

    /// <summary>
    /// Item tags the player state shows (inventory, hunting weapon/toolkit, pets as PET_&lt;TYPE&gt;, and the
    /// persisted storage containers), built once per request by <see cref="TaskExecutionService"/>.
    /// Null when no player state is available, which makes <see cref="RequiredItem.Owned"/> null everywhere.
    /// </summary>
    public HashSet<string> OwnedItemTags { get; set; }

    /// <summary>
    /// True when the storage containers (ender chest, backpacks) could not be read, so an item missing from
    /// <see cref="OwnedItemTags"/> is not proof the player lacks it and its Owned stays null.
    /// </summary>
    public bool OwnedItemsIncomplete { get; set; }

    public T GetService<T>() where T : class
    {
        return ServiceProvider?.GetService<T>();
    }

    /// <summary>
    /// Shard counts from player state (e.g. attribute shards collected)
    /// </summary>
    public Dictionary<string, int> Shards => ExtractedInfo?.ShardCounts ?? new();

    /// <summary>
    /// Attribute/stat levels from player state
    /// </summary>
    public Dictionary<string, int> Stats => ExtractedInfo?.AttributeLevel ?? new();

    public Dictionary<string, float> GetPrices()
    {
        var combined = new Dictionary<string, float>();
        foreach (var price in CleanPrices)
        {
            combined[price.Key] = price.Value;
        }
        foreach (var itemPrice in BazaarPrices)
        {
            if (!combined.ContainsKey(itemPrice.ProductId))
            {
                combined[itemPrice.ProductId] = (float)itemPrice.BuyPrice;
            }
        }
        return combined;
    }

    public class CalculationCache
    {
        public object Data { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}

/// <summary>
/// Aggregated average drop rate from community data for a specific item in a specific method.
/// </summary>
public record AverageDrop(string ItemTag, double RatePerHour, int SampleCount);
