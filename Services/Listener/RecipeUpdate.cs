using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.Core;
using Coflnet.Sky.Sniper.Client.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RestSharp;

namespace Coflnet.Sky.PlayerState.Services;

public class RecipeUpdate : UpdateListener
{
    // recipe/npc-cost extraction only enriches shared pricing data; nothing in the player's own state
    // depends on it, so a failure here must never abort the update or block persistence.
    public override bool Optional => true;

    private readonly HashSet<string> alreadyProcessed = new HashSet<string>();
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.UserId != null && ReadRecipe(args.msg.Chest) is { } recipe)
            await SaveRecipe(args, recipe);
        if (args.msg.Chest?.Name == "Anvil")
            await CheckAnvilRecipe(args);
        if (args.msg.Chest?.Items.Count < 9 * 10 || args.msg.UserId == null
            || !(args.msg.Chest?.Items[10]?.Description?.Contains("Cost") ?? false)
            || !args.msg.Chest.Items[10].Description.Contains("Click to trade")) // npc purchases have click to trade on items
            return; // not a selling npc
        if (alreadyProcessed.Contains(args.msg.Chest.Name))
            return;
        var items = args.msg.Chest.Items.Take(9 * 5).Where(i => i.Tag != null);
        if (await HasSealOfFamily(args.currentState.McInfo.Uuid, args))
        {
            Logger.LogWarning("Seal of the family detected, skipping npc cost extraction for {chestName} for player {playerId}", args.msg.Chest.Name, args.msg.PlayerId);
            return; // prices are uncertain with seal of the family, skip this update
        }
        Logger.LogDebug("Extracting npc cost from {chestName} {items}", args.msg.Chest.Name, JsonConvert.SerializeObject(items, Formatting.Indented));
        foreach (var item in items)
        {
            // Parse costs from the item's description
            var description = item.Description;
            if (description == null || !description.Contains("Cost"))
                continue;

            var costs = ReadCosts(description);
            if (costs.Count > 0)
            {
                var npcCost = new NpcCost
                {
                    ItemTag = item.Tag,
                    NpcName = args.msg.Chest.Name,
                    Costs = costs,
                    Description = item.Description,
                    Stock = ReadStock(description),
                    ResultCount = item.Count ?? 1,
                    LastUpdatedBy = args.msg.UserId + "-" + args.msg.PlayerId
                };
                await args.GetService<RecipeService>().Save(npcCost);
                Logger.LogInformation("NPC cost update {npcName} {itemTag} {costs}", npcCost.NpcName, npcCost.ItemTag, JsonConvert.SerializeObject(npcCost.Costs));
                alreadyProcessed.Add(args.msg.Chest.Name);
            }
            else
                args.GetService<ILogger<RecipeUpdate>>().LogWarning("No costs found for item {ItemTag} in chest {ChestName} for player {PlayerId}", item.Tag, args.msg.Chest.Name, args.msg.PlayerId);
        }
    }

    /// <summary>
    /// The costs of a shop item by display name, from the lines after "Cost" up to the next empty
    /// line. The mod for current Minecraft versions drops the color code of a line with a single
    /// style ("Cost", "30 Coins", "Oak Log") and keeps the codes of the others ("§fDirt §8x4"), so
    /// the lines are read without color codes. Looking for "§7Cost" found no cost from 2026-06-25 on.
    /// </summary>
    internal static Dictionary<string, int> ReadCosts(string description)
    {
        var costs = new Dictionary<string, int>();
        // the header is a line of its own, ability lore has lines like "Mana Cost: 50"
        var costSection = description.Split('\n').Select(StripFormatting).SkipWhile(l => l != "Cost").Skip(1);
        foreach (var line in costSection)
        {
            // Stop if we reach Stock or an empty line
            if (string.IsNullOrWhiteSpace(line) || line.Contains("Stock"))
                break;

            // Match lines like "25 Coins" or "Enchanted Diamond x16" or "Rusty Coin" or "1,000,000 Coins"
            var match = Regex.Match(line, @"^(?:(?<amount>[\d,]+)\s+)?(?<name>.+?)(?:\s+x(?<amount2>[\d,]+))?$");
            if (!match.Success)
                continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Contains('.'))
                continue; // npc purchase has no partial coins
            var amountStr = match.Groups["amount"].Success ? match.Groups["amount"].Value : match.Groups["amount2"].Value;
            // No amount found, default to 1
            costs[name] = int.TryParse(amountStr.Replace(",", ""), out var amount) ? amount : 1;
        }
        return costs;
    }

    /// <summary>
    /// The remaining stock from lines like "Stock\n640 remaining", 0 when the item has none.
    /// </summary>
    internal static int ReadStock(string description)
    {
        var stockMatch = Regex.Match(StripFormatting(description), @"^(?<stock>[\d,]+) remaining$", RegexOptions.Multiline);
        return stockMatch.Success && int.TryParse(stockMatch.Groups["stock"].Value.Replace(",", ""), out var stock) ? stock : 0;
    }

    private static string StripFormatting(string value)
    {
        return Regex.Replace(value, "§.", string.Empty).Trim();
    }

    private async Task CheckAnvilRecipe(UpdateArgs args)
    {
        var texts = args.msg.Chest.Items.Take(9 * 5).Where(i => i.Tag != null).Select(i => i?.Description).ToList();
        if (texts.Count < 3 || args.msg.Chest.Items.Where(i => i.Tag != null).First().Tag != "ENCHANTED_BOOK")
            return;
        Logger.LogDebug("Checking book recipe with text: {text}", string.Join("\n", texts));
    }

    /// <summary>
    /// Check if the player has Seal of the Family in their profile
    /// will return true by default on errors as the primary use is to ignore uncertain npc prices
    /// </summary>
    /// <param name="uuid"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<bool> HasSealOfFamily(Guid uuid, UpdateArgs args)
    {
        var pricesApi = args.GetService<Api.Client.Api.IPricesApi>();
        var profileClient = new RestClient(args.GetService<IConfiguration>()["PROFILE_BASE_URL"] ?? throw new Exception("PROFILE_BASE_URL not configured"));
        var museumJson = await profileClient.ExecuteAsync(new RestRequest($"/api/profile/{uuid.ToString("n")}/current?maxAge={DateTime.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}"));
        Api.Client.Model.Member profile;
        try
        {
            profile = JsonConvert.DeserializeObject<Api.Client.Model.Member>(museumJson.Content);
        }
        catch (System.Exception ex)
        {
            // The generated Member model marks some fields (e.g. rift.inventory.wardrobe_equipped_slot)
            // as required; profiles that never touched that content fail to deserialize. This must not
            // abort the whole inventory update (which would skip persisting the player's state), so fall
            // back to the documented default of "true" - it only suppresses uncertain npc price extraction.
            args.GetService<ILogger<RecipeUpdate>>()?.LogWarning(ex, "Could not deserialize profile for uuid {Uuid} when checking seal of family, assuming present.", uuid);
            return true;
        }
        if (profile == null)
        {
            args.GetService<ILogger<RecipeUpdate>>()?.LogWarning("Profile for uuid {Uuid} returned null when checking seal of family.", uuid);
            return true;
        }

        var items = await pricesApi.ApiProfileItemsPostAsync(profile);
        if (items == null)
        {
            args.GetService<ILogger<RecipeUpdate>>()?.LogWarning("Prices API returned null items for profile {Uuid} when checking seal of family.", uuid);
            return true;
        }

        // Safely check nested collections for nulls
        try
        {
            return items.Any(i => (i.Value?.Any(it => it != null && it.Tag == "SEAL_OF_THE_FAMILY") ?? false));
        }
        catch (System.Exception ex)
        {
            args.GetService<ILogger<RecipeUpdate>>()?.LogError(ex, "Error while checking seal of the family for uuid {Uuid}.", uuid);
            return true;
        }
    }


    /// <summary>
    /// The recipe shown in a recipe view (see <see cref="CollectionListener.TryGetRecipe"/> for the
    /// layout), or null when the view is none or the Supercraft button has no description (some mod
    /// may block it). Recognising the button only as "§aSupercraft" recorded no recipe from 2026-06-25
    /// on, when the mod for current Minecraft versions started to upload it as "Supercraft".
    /// </summary>
    internal static Recipe? ReadRecipe(Models.ChestView? chest)
    {
        if (!CollectionListener.TryGetRecipe(chest, out var resultTag, out _))
            return null;
        var items = chest!.Items;
        var requirements = items[32].Description?.Split('\n').Where(l => l.Contains("Requires")).Select(NormalizeRequirement).ToList();
        if (requirements == null)
            return null;
        var ingredients = items.Skip(10).Take(3).Concat(items.Skip(19).Take(3)).Concat(items.Skip(28).Take(3))
            .Select(i => new KeyValuePair<string?, int>(i?.Tag, i?.Count ?? 0)).ToList();
        return new Recipe
        {
            Tag = resultTag,
            Ingredients = ingredients,
            LastUpdated = DateTime.UtcNow,
            Requirements = requirements,
            ResultCount = items[25].Count ?? 1
        };
    }

    /// <summary>
    /// The mod for current Minecraft versions drops the leading "§7" of a lore line. The requirements
    /// are part of <see cref="Recipe.ComparisonKey"/>, so without it every recipe with a requirement
    /// would be stored a second time next to the rows from older mod versions.
    /// </summary>
    private static string NormalizeRequirement(string line)
    {
        return line.StartsWith("§7") ? line : "§7" + line;
    }

    private async Task SaveRecipe(UpdateArgs args, Recipe recipe)
    {
        recipe.LastUpdatedBy = args.msg.UserId + "-" + args.msg.PlayerId;
        Logger.LogInformation("Recipe update {chestName} {ingredients} {requirements}", args.msg.Chest.Name, JsonConvert.SerializeObject(recipe.Ingredients), JsonConvert.SerializeObject(recipe.Requirements));
        await args.GetService<RecipeService>().Save(recipe);
    }

    private static void ExtractMuseumExp(UpdateArgs args)
    {
        if (!(args.msg.Chest?.Name?.Contains("Museum") ?? false))
            return;

        var existing = new Dictionary<string, int>();
        if (File.Exists("museum.json"))
        {
            existing = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText("museum.json"));
        }
        foreach (var item in args.msg.Chest.Items.Skip(9).Take(36))
        {
            if (item.Description == null || !item.Description.Contains("SkyBlock XP"))
                continue;
            var name = item.ItemName;
            // extract the exp from "§7Click on this item in your inventory to\n§7add it to your §9Museum§7!\n\n§7Reward: §b+5 SkyBlock XP"
            var exp = Regex.Match(item.Description, @"§7Reward: §b\+(\d+) SkyBlock XP").Groups[1].Value;
            args.GetService<ILogger<RecipeUpdate>>().LogDebug("Museum update {name} {exp}", name, exp);
            if (exp == "")
                continue;
            existing[name] = int.Parse(exp);
        }
        File.WriteAllText("museum.json", JsonConvert.SerializeObject(existing, Formatting.Indented));
    }

}

public class RecipeService
{
    private static readonly Prometheus.Counter recipeUpdateCount = Prometheus.Metrics.CreateCounter(
        "sky_playerstate_recipe_update_total",
        "Total number of recipe updates saved.");
    private Table<Recipe> recipes;
    private Table<NpcCost> npcCosts;
    private ILogger<RecipeService> logger;

    public RecipeService(ISession session, ILogger<RecipeService> logger)
    {
        var mapping = new MappingConfiguration().Define(
            new Map<Recipe>()
                .TableName("recipes")
                .PartitionKey(r => r.Tag)
                .ClusteringKey(r => r.ComparisonKey)
                .Column(r => r.Tag, cm => cm.WithName("name"))
                .Column(r => r.Serialized, cm => cm.WithName("ingredients"))
                .Column(r => r.ResultCount, cm => cm.WithName("result_count"))
                .Column(r => r.Ingredients, cm => cm.Ignore())
                .Column(r => r.Requirements, cm => cm.WithName("requirements"))
        );
        recipes = new Table<Recipe>(session, mapping);
        recipes.CreateIfNotExists();
        var npcMapping = new MappingConfiguration().Define(
            new Map<NpcCost>()
                .TableName("npc_costs")
                .PartitionKey(n => n.ItemTag)
                .ClusteringKey(n => n.NpcName)
                .Column(n => n.ItemTag, cm => cm.WithName("item_tag"))
        );
        npcCosts = new Table<NpcCost>(session, npcMapping);
        npcCosts.CreateIfNotExists();
        this.logger = logger;
    }

    public async Task<IEnumerable<Recipe>> GetRecipes(string tag)
    {
        return await recipes.Where(r => r.Tag == tag).ExecuteAsync();
    }

    internal async Task Save(Recipe recipe)
    {
        try
        {
            await recipes.Insert(recipe).ExecuteAsync();
            recipeUpdateCount.Inc();
        }
        catch (System.Exception e)
        {
            logger.LogError(e, "Failed to save recipe {Tag} {full}", recipe.Tag, JsonConvert.SerializeObject(recipe));
            throw; // rethrow the exception to let the caller handle it
        }
    }

    public async Task<IEnumerable<NpcCost>> GetNpcCost(string itemTag)
    {
        return await npcCosts.Where(n => n.ItemTag == itemTag).ExecuteAsync();
    }

    public async Task Save(NpcCost npcCost)
    {
        try
        {
            await npcCosts.Insert(npcCost).ExecuteAsync();
        }
        catch (System.Exception e)
        {
            logger.LogError(e, "Failed to save NPC cost for item {ItemTag} {full}", npcCost.ItemTag, JsonConvert.SerializeObject(npcCost));
            throw; // rethrow the exception to let the caller handle it
        }
    }

    internal async Task<IEnumerable<NpcCost>> GetNpcCosts()
    {
        return await npcCosts.ExecuteAsync();
    }

    internal async Task<IEnumerable<Recipe>> GetRecipes()
    {
        return await recipes.ExecuteAsync();
    }
}

public class NpcCost
{
    public string ItemTag { get; set; }
    public string NpcName { get; set; }
    public Dictionary<string, int> Costs { get; set; } = new();
    public string? Description { get; set; }
    public int Stock { get; set; } = 0;
    public int ResultCount { get; set; } = 1;
    public string LastUpdatedBy { get; set; } = string.Empty;
}

public class Recipe
{
    public string Tag { get; set; }
    public List<KeyValuePair<string?, int>> Ingredients { get; set; }
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string Serialized { get => JsonConvert.SerializeObject(Ingredients); set => Ingredients = JsonConvert.DeserializeObject<List<KeyValuePair<string?, int>>>(value); }
    public int ResultCount { get; set; }
    public List<string> Requirements { get; set; }
    public string ComparisonKey { get => Encoding.UTF8.GetString(MD5.HashData(Encoding.UTF8.GetBytes(Tag + Serialized + string.Join(',', Requirements)))); set { } }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string LastUpdatedBy { get; set; }
}