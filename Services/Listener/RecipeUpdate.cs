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
using Coflnet.Sky.Sniper.Client.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class RecipeUpdate : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (!(args.msg.Chest?.Name?.EndsWith(" Recipe") ?? false))
            return;
        await ExtractRecipe(args);
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