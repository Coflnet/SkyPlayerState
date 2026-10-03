using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>One way to craft an item: ingredient totals per craft (the grid slots summed per tag) and the output count.</summary>
public record CraftRecipe(Dictionary<string, int> Ingredients, int ResultCount, DateTime LastUpdated);

/// <summary>
/// In-memory copy of the recorded recipes (<see cref="RecipeService"/>) for the synchronous
/// <see cref="CollectionListener"/> to recognise crafts at the crafting table. <see cref="Get"/> never
/// blocks and never throws: it returns what is loaded so far and triggers a background (re)load when
/// the data is older than <see cref="RefreshInterval"/>.
/// </summary>
public class CraftRecipeCache
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly Func<Task<IEnumerable<Recipe>>>? loader;
    private readonly ILogger<CraftRecipeCache>? logger;
    private ConcurrentDictionary<string, IReadOnlyList<CraftRecipe>> recipes = new();
    private int loading;
    private long nextLoadTicks;

    public CraftRecipeCache(RecipeService? recipeService, ILogger<CraftRecipeCache>? logger)
        : this(recipeService == null ? null : () => recipeService.GetRecipes(), logger)
    {
    }

    internal CraftRecipeCache(Func<Task<IEnumerable<Recipe>>>? loader, ILogger<CraftRecipeCache>? logger = null)
    {
        this.loader = loader;
        this.logger = logger;
    }

    /// <summary>The recipes of <paramref name="tag"/>, newest first; empty while nothing is loaded.</summary>
    public IReadOnlyList<CraftRecipe> Get(string tag)
    {
        try
        {
            TriggerLoad();
            return tag != null && recipes.TryGetValue(tag, out var list) ? list : Array.Empty<CraftRecipe>();
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Could not read craft recipes for {tag}", tag);
            return Array.Empty<CraftRecipe>();
        }
    }

    private void TriggerLoad()
    {
        if (loader == null || DateTime.UtcNow.Ticks < Interlocked.Read(ref nextLoadTicks))
            return;
        if (Interlocked.CompareExchange(ref loading, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadInternal();
            }
            finally
            {
                Interlocked.Exchange(ref loading, 0);
            }
        });
    }

    /// <summary>Loads all recipes now, never throws (a failure is logged and retried after <see cref="RetryInterval"/>).</summary>
    internal async Task LoadInternal()
    {
        if (loader == null)
            return;
        try
        {
            var all = await loader();
            var fresh = new ConcurrentDictionary<string, IReadOnlyList<CraftRecipe>>();
            foreach (var group in all.Where(r => r?.Tag != null).GroupBy(r => r.Tag))
                fresh[group.Key] = Build(group.Key, group.Select(r => ToCraftRecipe(r, group.Key)).OfType<CraftRecipe>());
            // recipes saved while loading are not lost: merge what the live cache has into the new one
            foreach (var (tag, existing) in recipes)
                fresh.AddOrUpdate(tag, existing, (_, loaded) => Build(tag, loaded.Concat(existing)));
            recipes = fresh;
            Interlocked.Exchange(ref nextLoadTicks, (DateTime.UtcNow + RefreshInterval).Ticks);
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Failed to load craft recipes, retrying later");
            Interlocked.Exchange(ref nextLoadTicks, (DateTime.UtcNow + RetryInterval).Ticks);
        }
    }

    /// <summary>Puts a recipe into the cache (freshly saved or set by a test).</summary>
    internal void Add(Recipe recipe)
    {
        try
        {
            var craft = ToCraftRecipe(recipe, recipe.Tag);
            if (craft == null)
                return;
            recipes.AddOrUpdate(recipe.Tag, _ => Build(recipe.Tag, new[] { craft }),
                (tag, existing) => Build(tag, existing.Append(craft)));
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Could not cache recipe {tag}", recipe?.Tag);
        }
    }

    private static CraftRecipe? ToCraftRecipe(Recipe recipe, string tag)
    {
        if (recipe.Ingredients == null)
            return null;
        var totals = new Dictionary<string, int>();
        foreach (var (ingredient, count) in recipe.Ingredients)
        {
            if (string.IsNullOrEmpty(ingredient) || count <= 0)
                continue;
            totals[ingredient] = totals.GetValueOrDefault(ingredient) + count;
        }
        if (totals.Count == 0 || totals.ContainsKey(tag))
            return null;
        return new CraftRecipe(totals, Math.Max(1, recipe.ResultCount), recipe.LastUpdated);
    }

    /// <summary>Newest first, variants with the same ingredient totals and result count collapsed.</summary>
    private static IReadOnlyList<CraftRecipe> Build(string tag, IEnumerable<CraftRecipe> variants)
    {
        var result = new List<CraftRecipe>();
        var seen = new HashSet<string>();
        foreach (var variant in variants.OrderByDescending(v => v.LastUpdated))
        {
            var key = variant.ResultCount + ";" + string.Join(",", variant.Ingredients.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key + "=" + i.Value));
            if (seen.Add(key))
                result.Add(variant);
        }
        return result;
    }
}

/// <summary>
/// Recognises crafts at the crafting table: the output grew while the ingredients shrank by what the
/// recipe needs. Used for the diff between the crafting table view and the next one, where the
/// ingredients have left the inventory and the output is no loot.
/// </summary>
public static class CraftDetection
{
    public static (Dictionary<string, int> CraftedOutput, Dictionary<string, int> Consumed) Detect(
        IReadOnlyDictionary<string, int> previous, IReadOnlyDictionary<string, int> current,
        Func<string, IReadOnlyList<CraftRecipe>> recipeLookup)
    {
        var craftedOutput = new Dictionary<string, int>();
        var consumed = new Dictionary<string, int>();
        foreach (var (tag, count) in current.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var gain = count - previous.GetValueOrDefault(tag);
            if (gain <= 0)
                continue;
            foreach (var recipe in recipeLookup(tag))
            {
                var crafts = (gain + recipe.ResultCount - 1) / recipe.ResultCount;
                foreach (var (ingredient, quantity) in recipe.Ingredients)
                {
                    var lost = previous.GetValueOrDefault(ingredient) - current.GetValueOrDefault(ingredient) - consumed.GetValueOrDefault(ingredient);
                    crafts = Math.Min(crafts, Math.Max(0, lost) / quantity);
                    if (crafts < 1)
                        break;
                }
                if (crafts < 1)
                    continue;
                craftedOutput[tag] = Math.Min(gain, crafts * recipe.ResultCount);
                foreach (var (ingredient, quantity) in recipe.Ingredients)
                    consumed[ingredient] = consumed.GetValueOrDefault(ingredient) + crafts * quantity;
                break;
            }
        }
        return (craftedOutput, consumed);
    }
}
