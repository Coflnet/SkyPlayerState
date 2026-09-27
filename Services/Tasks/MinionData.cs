using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Loads the embedded <c>minion_data.json</c> (kept byte-identical to
/// <c>SkyBackendForFrontend/Services/minion_data.json</c> - the minion command's own source of truth,
/// see <c>MinionData.Resource.Tests.cs</c>) and derives the full set of item tags a minion can put
/// into a player's inventory: every raw product tag plus its "compacted"/enchanted forms
/// (<see cref="EnchantedForms"/>). Used by <c>MinionCollectionTask</c> (HiddenTasks.cs) so its
/// DetectionItems set reflects exactly what the minion command already knows about instead of a
/// hand-maintained list that can silently drift from it.
/// </summary>
internal static class MinionData
{
    /// <summary>Logical name registered for the embedded resource in SkyPlayerState.csproj.</summary>
    private const string ResourceName = "Coflnet.Sky.PlayerState.Tasks.minion_data.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<List<Minion>> LazyMinions = new(Load);
    private static readonly Lazy<HashSet<string>> LazyProductTags = new(() =>
        LazyMinions.Value
            .SelectMany(m => m.Products ?? [])
            .Select(p => p.Tag)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<HashSet<string>> LazyDetectionItems = new(BuildDetectionItems);

    public static IReadOnlyList<Minion> Minions => LazyMinions.Value;

    /// <summary>Every raw product tag across all minions (e.g. "WHEAT", "SLIME_BALL", "RAW_FISH:1").</summary>
    public static IReadOnlySet<string> ProductTags => LazyProductTags.Value;

    /// <summary>
    /// Raw product tags plus their compacted/enchanted forms - see
    /// <see cref="EnchantedForms.CompactedFormsOf"/>. This is the set <c>MinionCollectionTask</c>
    /// declares as its DetectionItems.
    /// </summary>
    public static IReadOnlySet<string> DetectionItems => LazyDetectionItems.Value;

    /// <summary>
    /// Minion outputs that minion_data.json does not list but production "Your Island" periods
    /// show being collected (Profit summary logs 2026-09-27): the Inferno Minion's main product
    /// (Crude Gabagool and its compacted form - the json only has its rare drops) and the
    /// Corrupt Soil upgrade outputs of mob minions.
    /// </summary>
    internal static readonly HashSet<string> ProductionVerifiedOutputs =
    [
        "CRUDE_GABAGOOL", "VERY_CRUDE_GABAGOOL",
        "CORRUPTED_FRAGMENT", "SULPHUR_ORE", "ENCHANTED_SULPHUR",
    ];

    private static HashSet<string> BuildDetectionItems()
    {
        var result = new HashSet<string>(ProductionVerifiedOutputs, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in ProductTags)
        {
            result.Add(tag);
            foreach (var form in EnchantedForms.CompactedFormsOf(tag))
                result.Add(form);
        }
        return result;
    }

    private static List<Minion> Load()
    {
        var assembly = typeof(MinionData).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{ResourceName}' not found - check SkyPlayerState.csproj EmbeddedResource LogicalName");
        return JsonSerializer.Deserialize<List<Minion>>(stream, JsonOptions) ?? [];
    }

    public class Minion
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public List<MinionProduct> Products { get; set; } = [];
    }

    public class MinionProduct
    {
        public string ItemName { get; set; } = "";
        public double PerTime { get; set; }
        public double NpcPrice { get; set; }
        public string Tag { get; set; } = "";
    }
}

/// <summary>
/// Maps a minion's RAW product tag to its "compacted"/enchanted forms - the items a player actually
/// collects when they craft the raw drops into their enchanted/block tier before selling (the minion
/// command only tracks the raw production rate, but a player's inventory/collection log sees the
/// compacted items instead). Verified against
/// <c>NotEnoughUpdates-REPO/items/&lt;TAG&gt;.json</c> (see <c>MinionData.EnchantedForms.Tests.cs</c>) -
/// entries that turned out not to exist (ENCHANTED_COOKED_CHICKEN, ENCHANTED_COOKED_BEEF,
/// ENCHANTED_CHILI_PEPPER) were dropped rather than guessed at. ENCHANTED_WHEAT and
/// ENCHANTED_SUNFLOWER are missing from that repo but are real production tags (seen in the
/// "Profit summary" logs 2026-09-27) - see <see cref="ProductionVerified"/>. A raw tag with no entry here either has no enchanted form (e.g. RED_ROSE, YELLOW_FLOWER)
/// or is tracked as itself only (the INFERNO_*/HEMOVIBE/GABAGOOL_THE_FISH/REAPER_PEPPER/
/// CAPSAICIN_EYEDROPS group, and now CHILI_PEPPER) - either way <see cref="MinionData"/>
/// already includes the raw tag itself in DetectionItems, so no entry is needed for those.
/// </summary>
internal static class EnchantedForms
{
    /// <summary>
    /// Tags that are absent from NotEnoughUpdates-REPO/items but occur in production periods, the
    /// logs are the authority for what the collection tracking actually reports.
    /// </summary>
    internal static readonly HashSet<string> ProductionVerified = ["ENCHANTED_WHEAT", "ENCHANTED_SUNFLOWER"];

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SLIME_BALL"] = ["ENCHANTED_SLIME_BALL", "ENCHANTED_SLIME_BLOCK"],
        ["RAW_FISH"] = ["ENCHANTED_RAW_FISH", "ENCHANTED_COOKED_FISH"],
        ["RAW_FISH:1"] = ["ENCHANTED_RAW_SALMON", "ENCHANTED_COOKED_SALMON"],
        ["RAW_FISH:2"] = ["ENCHANTED_CLOWNFISH"],
        ["RAW_FISH:3"] = ["ENCHANTED_PUFFERFISH"],
        ["INK_SACK:4"] = ["ENCHANTED_LAPIS_LAZULI", "ENCHANTED_LAPIS_LAZULI_BLOCK"],
        ["INK_SACK:3"] = ["ENCHANTED_COCOA"],
        ["LOG"] = ["ENCHANTED_OAK_LOG"],
        ["LOG:1"] = ["ENCHANTED_SPRUCE_LOG"],
        ["LOG:2"] = ["ENCHANTED_BIRCH_LOG"],
        ["LOG:3"] = ["ENCHANTED_JUNGLE_LOG"],
        ["LOG_2"] = ["ENCHANTED_ACACIA_LOG"],
        ["LOG_2:1"] = ["ENCHANTED_DARK_OAK_LOG"],
        ["CARROT_ITEM"] = ["ENCHANTED_CARROT", "ENCHANTED_GOLDEN_CARROT"],
        ["POTATO_ITEM"] = ["ENCHANTED_POTATO", "ENCHANTED_BAKED_POTATO"],
        ["NETHER_STALK"] = ["ENCHANTED_NETHER_STALK", "MUTANT_NETHER_STALK"],
        ["SULPHUR"] = ["ENCHANTED_GUNPOWDER", "ENCHANTED_FIREWORK_ROCKET"],
        ["SNOW_BALL"] = ["ENCHANTED_SNOW_BLOCK"],
        ["MYCEL"] = ["ENCHANTED_MYCELIUM", "ENCHANTED_MYCELIUM_CUBE"],
        ["SAND:1"] = ["ENCHANTED_RED_SAND", "ENCHANTED_RED_SAND_CUBE"],
        ["ENDER_STONE"] = ["ENCHANTED_ENDSTONE"],
        ["PORK"] = ["ENCHANTED_PORK", "ENCHANTED_GRILLED_PORK"],
        ["RABBIT"] = ["ENCHANTED_RABBIT"],
        ["RABBIT_HIDE"] = ["ENCHANTED_RABBIT_HIDE"],
        ["RABBIT_FOOT"] = ["ENCHANTED_RABBIT_FOOT"],
        ["GLOWSTONE_DUST"] = ["ENCHANTED_GLOWSTONE_DUST", "ENCHANTED_GLOWSTONE"],
        ["QUARTZ"] = ["ENCHANTED_QUARTZ", "ENCHANTED_QUARTZ_BLOCK"],
        ["CLAY_BALL"] = ["ENCHANTED_CLAY_BALL"],
        ["WOOL"] = ["ENCHANTED_WOOL"],
        ["HARD_STONE"] = ["ENCHANTED_HARD_STONE"],
        ["MITHRIL_ORE"] = ["ENCHANTED_MITHRIL"],
        ["BROWN_MUSHROOM"] = ["ENCHANTED_BROWN_MUSHROOM", "ENCHANTED_HUGE_MUSHROOM_2"],
        ["RED_MUSHROOM"] = ["ENCHANTED_RED_MUSHROOM", "ENCHANTED_HUGE_MUSHROOM_1"],
        ["CACTUS"] = ["ENCHANTED_CACTUS_GREEN", "ENCHANTED_CACTUS"],
        ["SUGAR_CANE"] = ["ENCHANTED_SUGAR", "ENCHANTED_SUGAR_CANE"],
        ["MELON"] = ["ENCHANTED_MELON", "ENCHANTED_MELON_BLOCK"],
        ["PUMPKIN"] = ["ENCHANTED_PUMPKIN"],
        ["WHEAT"] = ["ENCHANTED_WHEAT", "ENCHANTED_HAY_BLOCK"],
        ["SEEDS"] = ["ENCHANTED_SEEDS"],
        ["COAL"] = ["ENCHANTED_COAL", "ENCHANTED_COAL_BLOCK"],
        ["IRON_INGOT"] = ["ENCHANTED_IRON", "ENCHANTED_IRON_BLOCK"],
        ["GOLD_INGOT"] = ["ENCHANTED_GOLD", "ENCHANTED_GOLD_BLOCK"],
        ["DIAMOND"] = ["ENCHANTED_DIAMOND", "ENCHANTED_DIAMOND_BLOCK"],
        ["EMERALD"] = ["ENCHANTED_EMERALD", "ENCHANTED_EMERALD_BLOCK"],
        ["REDSTONE"] = ["ENCHANTED_REDSTONE", "ENCHANTED_REDSTONE_BLOCK"],
        ["OBSIDIAN"] = ["ENCHANTED_OBSIDIAN"],
        ["ICE"] = ["ENCHANTED_ICE", "ENCHANTED_PACKED_ICE"],
        ["SAND"] = ["ENCHANTED_SAND"],
        ["COBBLESTONE"] = ["ENCHANTED_COBBLESTONE"],
        // GRAVEL minion also drops flint (FLINT is a separate product already) - both compact to the
        // same ENCHANTED_FLINT, harmless duplication once merged into the DetectionItems HashSet.
        ["GRAVEL"] = ["ENCHANTED_FLINT"],
        ["FLINT"] = ["ENCHANTED_FLINT"],
        ["PRISMARINE_SHARD"] = ["ENCHANTED_PRISMARINE_SHARD"],
        ["PRISMARINE_CRYSTALS"] = ["ENCHANTED_PRISMARINE_CRYSTALS"],
        ["SPONGE"] = ["ENCHANTED_SPONGE"],
        ["ROTTEN_FLESH"] = ["ENCHANTED_ROTTEN_FLESH"],
        ["BONE"] = ["ENCHANTED_BONE", "ENCHANTED_BONE_BLOCK"],
        ["STRING"] = ["ENCHANTED_STRING"],
        ["SPIDER_EYE"] = ["ENCHANTED_SPIDER_EYE", "ENCHANTED_FERMENTED_SPIDER_EYE"],
        ["ENDER_PEARL"] = ["ENCHANTED_ENDER_PEARL", "ENCHANTED_EYE_OF_ENDER"],
        ["GHAST_TEAR"] = ["ENCHANTED_GHAST_TEAR"],
        ["MAGMA_CREAM"] = ["ENCHANTED_MAGMA_CREAM"],
        ["BLAZE_ROD"] = ["ENCHANTED_BLAZE_POWDER", "ENCHANTED_BLAZE_ROD"],
        ["FEATHER"] = ["ENCHANTED_FEATHER"],
        ["EGG"] = ["ENCHANTED_EGG", "ENCHANTED_CAKE"],
        // RAW_CHICKEN -> ENCHANTED_COOKED_CHICKEN does NOT exist - dropped, raw form only kept.
        ["RAW_CHICKEN"] = ["ENCHANTED_RAW_CHICKEN"],
        // RAW_BEEF -> ENCHANTED_COOKED_BEEF does NOT exist - dropped, raw form only kept.
        ["RAW_BEEF"] = ["ENCHANTED_RAW_BEEF"],
        ["LEATHER"] = ["ENCHANTED_LEATHER"],
        ["MUTTON"] = ["ENCHANTED_MUTTON", "ENCHANTED_COOKED_MUTTON"],
        ["POISONOUS_POTATO"] = ["ENCHANTED_POISONOUS_POTATO"],
        // CHILI_PEPPER -> ENCHANTED_CHILI_PEPPER does NOT exist - dropped, tracked as itself only.
        ["DOUBLE_PLANT"] = ["ENCHANTED_SUNFLOWER"],
    };

    /// <summary>Compacted/enchanted forms of <paramref name="rawTag"/>, or empty if it has none tracked.</summary>
    public static IEnumerable<string> CompactedFormsOf(string rawTag) =>
        rawTag != null && Map.TryGetValue(rawTag, out var forms) ? forms : [];

    /// <summary>Public accessor for tests - the raw tag -&gt; enchanted forms map itself.</summary>
    internal static IReadOnlyDictionary<string, string[]> MapForTest => Map;
}
