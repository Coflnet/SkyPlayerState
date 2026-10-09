using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Crafts.Client.Model;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.Items.Client.Model;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Which items are upgrades of which: WISE_WITHER_CHESTPLATE (Storm's Chestplate) is crafted from a WITHER_CHESTPLATE, so owning
/// the upgrade counts as owning the base (<see cref="GearOwnership.BuildOwnedTags"/>).
/// <para>
/// The rule: an item U upgrades B when B is an ingredient of U's recipe (the crafts service knows the recipes) AND both are
/// the same kind of gear, i.e. have the same <see cref="ItemCategory"/> out of <see cref="GearCategories"/> (chestplate with
/// chestplate, accessory with accessory, sword with sword). The category check keeps out items that merely consume
/// something: a sword crafted from enchanted diamonds, or a chestplate whose recipe takes a helmet. It is applied
/// transitively (an upgrade of an upgrade counts as the base too, cycles are ignored).
/// </para>
/// </summary>
public static class ItemUpgradeMap
{
    /// <summary>Categories whose items are worn, wielded or carried gear; other categories (materials, consumables, ...) never form upgrade chains.</summary>
    public static readonly IReadOnlySet<ItemCategory> GearCategories = new HashSet<ItemCategory>
    {
        ItemCategory.SWORD, ItemCategory.LONGSWORD, ItemCategory.BOW, ItemCategory.AXE, ItemCategory.PICKAXE, ItemCategory.DRILL,
        ItemCategory.HOE, ItemCategory.SPADE, ItemCategory.SHEARS, ItemCategory.FISHING_ROD, ItemCategory.FISHING_WEAPON,
        ItemCategory.WAND, ItemCategory.GAUNTLET, ItemCategory.HELMET, ItemCategory.CHESTPLATE, ItemCategory.LEGGINGS,
        ItemCategory.BOOTS, ItemCategory.ACCESSORY, ItemCategory.CLOAK, ItemCategory.NECKLACE, ItemCategory.BELT,
        ItemCategory.GLOVES, ItemCategory.BRACELET, ItemCategory.FARMING_TOOL, ItemCategory.VACUUM,
    };

    /// <summary>
    /// Upgraded tag -> every base item it counts as (transitive). Empty when there is no data.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Build(IEnumerable<(string ItemId, IEnumerable<string> Ingredients)> recipes,
        IReadOnlyDictionary<string, ItemCategory> categories)
    {
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (itemId, ingredients) in recipes ?? [])
        {
            if (string.IsNullOrEmpty(itemId) || !categories.TryGetValue(itemId, out var category) || !GearCategories.Contains(category))
                continue;
            foreach (var ingredient in ingredients ?? [])
            {
                if (string.IsNullOrEmpty(ingredient) || ingredient == itemId
                    || !categories.TryGetValue(ingredient, out var ingredientCategory) || ingredientCategory != category)
                    continue;
                if (!direct.TryGetValue(itemId, out var bases))
                    direct[itemId] = bases = new HashSet<string>(StringComparer.Ordinal);
                bases.Add(ingredient);
            }
        }
        var closed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var upgraded in direct.Keys)
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            var open = new Stack<string>(direct[upgraded]);
            while (open.Count > 0)
            {
                var current = open.Pop();
                if (current == upgraded || !all.Add(current))
                    continue;
                if (direct.TryGetValue(current, out var deeper))
                    foreach (var next in deeper)
                        open.Push(next);
            }
            if (all.Count > 0)
                closed[upgraded] = all;
        }
        return closed;
    }

    /// <summary>Adds the bases of every tag in <paramref name="owned"/> to it.</summary>
    public static void AddBases(HashSet<string> owned, IReadOnlyDictionary<string, HashSet<string>> upgradeBases)
    {
        if (upgradeBases == null || upgradeBases.Count == 0)
            return;
        foreach (var tag in owned.ToList())
            if (upgradeBases.TryGetValue(tag, out var bases))
                owned.UnionWith(bases);
    }
}

/// <summary>
/// Builds and caches the <see cref="ItemUpgradeMap"/> from the crafts (recipes) and items (categories) services. A failed
/// refresh keeps serving the previous map (or an empty one) and retries soon, so ownership checks never depend on it.
/// </summary>
public class ItemUpgradeService(ICraftsApi craftsApi, IItemsApi itemsApi, ILogger<ItemUpgradeService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private Dictionary<string, HashSet<string>> cached;
    private DateTime validUntil = DateTime.MinValue;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    /// <summary>Upgraded tag -> bases; empty when the services are unavailable. Never throws.</summary>
    public async Task<IReadOnlyDictionary<string, HashSet<string>>> GetBases(CancellationToken cancellationToken = default)
    {
        if (cached != null && DateTime.UtcNow < validUntil)
            return cached;
        // the first load may take a moment, later callers serve the old map instead of waiting
        if (!await refreshLock.WaitAsync(cached == null ? LoadTimeout : TimeSpan.Zero, cancellationToken))
            return cached ?? new();
        try
        {
            if (cached != null && DateTime.UtcNow < validUntil)
                return cached;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(LoadTimeout);
            var crafts = await craftsApi.GetAllAsync(cancellationToken: cts.Token);
            var items = await itemsApi.ItemsGetAsync(cancellationToken: cts.Token);
            var categories = new Dictionary<string, ItemCategory>(StringComparer.Ordinal);
            foreach (var item in items ?? [])
                if (item?.Tag != null && item.Category != null)
                    categories[item.Tag] = item.Category.Value;
            cached = ItemUpgradeMap.Build(
                (crafts ?? []).Select(c => (c.ItemId, (c.Ingredients ?? []).Select(i => i.ItemId))), categories);
            validUntil = DateTime.UtcNow + CacheDuration;
            logger.LogInformation("Built item upgrade map with {count} upgraded items", cached.Count);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Item upgrade map unavailable, {count} cached entries stay in use", cached?.Count ?? 0);
            cached ??= new();
            validUntil = DateTime.UtcNow + RetryAfterFailure;
        }
        finally
        {
            refreshLock.Release();
        }
        return cached;
    }
}
