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
    private readonly HashSet<string> alreadyProcessed = new HashSet<string>();
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Items.Count >= 9 * 10 && args.msg.UserId != null
            && args.msg.Chest?.Items[32].ItemName == "§aSupercraft")
            await ExtractRecipe(args);
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
            Console.WriteLine("Seal of the family detected, skipping npc cost extraction for " + args.msg.Chest.Name + " for player " + args.msg.PlayerId);
            return; // prices are uncertain with seal of the family, skip this update
        }
        Console.WriteLine("Extracting npc cost from " + args.msg.Chest.Name + JsonConvert.SerializeObject(items, Formatting.Indented));
        foreach (var item in items)
        {
            // Parse costs from the item's description
            var description = item.Description;
            if (description == null || !description.Contains("Cost"))
                continue;

            var costs = new Dictionary<string, int>();
            var costSection = Regex.Split(description, @"\n").SkipWhile(l => !l.Contains("§7Cost")).Skip(1);
            foreach (var line in costSection)
            {
                // Stop if we reach Stock or an empty line
                if (string.IsNullOrWhiteSpace(line) || line.Contains("Stock"))
                    break;

                // Match lines like "§625 Coins" or "§6Enchanted Diamond x16" or "§aRusty Coin §8x32" or "§aRusty Coin" or "§61,000,000 Coins"
                var match = Regex.Match(line, @"§.(?:(?<amount>[\d,]+)\s+)?(?<name>.*?)(?:\s+(?:§.x|x)(?<amount2>[\d,]+))?$");
                if (match.Success)
                {
                    var name = match.Groups["name"].Value.Trim();
                    if (name.Contains('.'))
                        continue; // npc purchase has no partial coins
                    // remove color codes from the name
                    name = Regex.Replace(name, @"§.", "");
                    var amountStr = match.Groups["amount"].Success ? match.Groups["amount"].Value : match.Groups["amount2"].Value;

                    // Remove thousand separators before parsing
                    amountStr = amountStr.Replace(",", "");

                    if (int.TryParse(amountStr, out var amount))
                    {
                        costs[name] = amount;
                    }
                    else
                    {
                        // No amount found, default to 1
                        costs[name] = 1;
                    }
                }
            }
            // Match lines like "§68 Coins\n\n§7Stock\n§6640 §7remaining\n\n§eClick to trade!"
            var stockMatch = Regex.Match(description, @"§6(?<stock>\d+)\s§7remaining");
            int stockCount = 0;
            if (stockMatch.Success)
                int.TryParse(stockMatch.Groups["stock"].Value, out stockCount);

            if (costs.Count > 0)
            {
                var npcCost = new NpcCost
                {
                    ItemTag = item.Tag,
                    NpcName = args.msg.Chest.Name,
                    Costs = costs,
                    Description = item.Description,
                    Stock = stockCount,
                    ResultCount = item.Count ?? 1,
                    LastUpdatedBy = args.msg.UserId + "-" + args.msg.PlayerId
                };
                await args.GetService<RecipeService>().Save(npcCost);
                Console.WriteLine($"NPC cost update {npcCost.NpcName} {npcCost.ItemTag} {JsonConvert.SerializeObject(npcCost.Costs)}");
                alreadyProcessed.Add(args.msg.Chest.Name);
            }
            else
                args.GetService<ILogger<RecipeUpdate>>().LogWarning("No costs found for item {ItemTag} in chest {ChestName} for player {PlayerId}", item.Tag, args.msg.Chest.Name, args.msg.PlayerId);
        }
    }

    private async Task CheckAnvilRecipe(UpdateArgs args)
    {
        var texts = args.msg.Chest.Items.Take(9 * 5).Where(i => i.Tag != null).Select(i => i?.Description).ToList();
        if (texts.Count < 3 || args.msg.Chest.Items.Where(i => i.Tag != null).First().Tag != "ENCHANTED_BOOK")
            return;
        Console.WriteLine($"Checking book recipe with text: {string.Join("\n", texts)}");
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
        var profile = JsonConvert.DeserializeObject<Api.Client.Model.Member>(museumJson.Content);
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


    private async Task ExtractRecipe(UpdateArgs args)
    {
        if (args.msg.Chest?.Items.Count < 9 * 10)
        {
            args.GetService<ILogger<RecipeUpdate>>().LogWarning("Recipe chest {ChestName} has less than 90 items, skipping from {player}", args.msg.Chest.Name, args.msg.PlayerId);
            return;
        }
        var ingredients = args.msg.Chest.Items.Skip(10).Take(3).Concat(args.msg.Chest.Items.Skip(19).Take(3)).Concat(args.msg.Chest.Items.Skip(28).Take(3))
            .Select(i => new KeyValuePair<string?, int>(i?.Tag, i?.Count ?? 0)).ToList();
        var requirements = args.msg.Chest.Items[32].Description?.Split('\n').Where(l => l.Contains("Requires")).ToList();
        if (requirements == null)
            return;// supercraft item not available some mod may block it, ignore this sample
        Console.WriteLine($"Recipe update {args.msg.Chest.Name} {JsonConvert.SerializeObject(ingredients)} {JsonConvert.SerializeObject(requirements)}");
        var recipe = new Recipe
        {
            Tag = args.msg.Chest.Items[25].Tag,
            Ingredients = ingredients,
            LastUpdated = DateTime.UtcNow,
            LastUpdatedBy = args.msg.UserId + "-" + args.msg.PlayerId,
            Requirements = requirements,
            ResultCount = args.msg.Chest.Items[25].Count ?? 1
        };
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
            Console.WriteLine($"Museum update {name} {exp}");
            if (exp == "")
                continue;
            existing[name] = int.Parse(exp);
        }
        File.WriteAllText("museum.json", JsonConvert.SerializeObject(existing, Formatting.Indented));
    }

}

public class RecipeService
{
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