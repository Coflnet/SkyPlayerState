using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>What a category keeps of an item, see <see cref="GearModifierTable"/>.</summary>
public record GearCategorySpec(
    string Id,
    IReadOnlySet<string> Enchants,
    IReadOnlySet<string> Counters,
    IReadOnlySet<string> Applied,
    IReadOnlySet<string> Parts,
    IReadOnlySet<string> ToolTypes,
    bool Turbo = false,
    bool Gems = false,
    bool HotPotato = false,
    bool Recombobulated = false,
    bool Stars = false,
    bool Bait = false)
{
    /// <summary>
    /// Lore and menu stats that matter for the category (crop fortunes are narrowed to the task's crop by
    /// <see cref="GearNormalizer.Filter"/>). Names as the game shows them (SkyHanni SkyblockStat display names).
    /// </summary>
    public IReadOnlySet<string> Stats { get; init; } = new HashSet<string>();
}

/// <summary>
/// Rate relevant item modifiers per gear category (research-measured-gear.md part B and the plan addendum; NBT keys
/// confirmed in dev/Data/NBT.cs and dev/Data/Flipper/Constants.cs, levelable_* only from the dossier). Unknown keys are
/// dropped. Combat categories (dungeon, kuudra and every unbound task, "other") keep tag, rarity, reforge and stars, and
/// the combat stats (Damage, Strength, Crit, ...). Every category keeps Magic Find (rare drops).
/// Category ids match the gear profile ids, see <see cref="GearCategories"/>.
/// </summary>
public static class GearModifierTable
{
    public const string Farming = "farming";
    public const string Foraging = "foraging";
    public const string MiningOre = "mining-ore";
    public const string MiningGem = "mining-gem";
    public const string FishingWater = "fishing-water";
    public const string FishingLava = "fishing-lava";
    public const string Dungeon = "dungeon";
    public const string Kuudra = "kuudra";
    /// <summary>Tasks without a gear profile binding.</summary>
    public const string Other = "other";

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> None = Set();
    private static readonly IReadOnlySet<string> CombatTools = Set("SWORD", "BOW", "LONGSWORD");
    private static readonly IReadOnlySet<string> MiningTools = Set("DRILL", "PICKAXE", "GAUNTLET");
    private static readonly IReadOnlySet<string> DrillParts = Set("drill_part_engine", "drill_part_fuel_tank", "drill_part_upgrade_module");
    private static readonly IReadOnlySet<string> MiningApplied = Set("mithril_infusion", "polarvoid", "divan_powder_coating", "tuned_transmission");

    private static IReadOnlySet<string> StatSet(params string[] values) => Set([.. values, "Magic Find"]);

    private static readonly IReadOnlySet<string> MiningStats = StatSet("Mining Speed", "Mining Fortune", "Ore Fortune", "Block Fortune",
        "Dwarven Metal Fortune", "Gemstone Fortune", "Pristine", "Mining Spread", "Gemstone Spread", "Breaking Power");
    private static readonly IReadOnlySet<string> CombatStats = StatSet("Damage", "Strength", "Crit Chance", "Crit Damage", "Attack Speed", "Ferocity",
        "Intelligence", "Ability Damage");

    private static readonly GearCategorySpec FarmingSpec = new(Farming,
        Set("harvesting", "cultivating", "dedication", "sunder", "replenish", "delicate", "feast", "pesterminator", "green_thumb"),
        Set("farmed_cultivating", "mined_crops", "levelable_lvl"),
        Set("farming_for_dummies_count", "levelable_overclocks"),
        None, Set("FARMING TOOL"),
        Turbo: true, Gems: true, HotPotato: true, Recombobulated: true)
    {
        Stats = StatSet([.. new[] { "Farming Fortune", "Bonus Pest Chance", "Overbloom" }, .. FarmingCrops.CropFortuneStats])
    };

    private static readonly GearCategorySpec ForagingSpec = new(Foraging,
        Set("absorb", "petalfall", "karma", "efficiency"),
        Set("absorb_logs_chopped"), None, None, Set("AXE"),
        Gems: true, HotPotato: true, Recombobulated: true)
    {
        Stats = StatSet("Foraging Fortune", "Fig Fortune", "Mangrove Fortune", "Helix Fortune", "Sweep", "Timber")
    };

    private static readonly GearCategorySpec MiningOreSpec = new(MiningOre,
        Set("efficiency", "fortune", "compact", "ultimate_flowstate"),
        Set("compact_blocks"), MiningApplied, DrillParts, MiningTools,
        Gems: true, HotPotato: true, Recombobulated: true)
    {
        Stats = MiningStats
    };

    private static readonly GearCategorySpec MiningGemSpec = MiningOreSpec with
    {
        Id = MiningGem,
        Enchants = Set("efficiency", "fortune", "compact", "ultimate_flowstate", "pristine", "lapidary")
    };

    private static readonly GearCategorySpec FishingSpec = new(FishingWater,
        Set("angler", "lure", "piscary", "luck_of_the_sea", "blessing", "charm", "corruption", "quick_bite", "ultimate_flash", "caster", "expertise"),
        Set("expertise_kills"), None, Set("hook", "line", "sinker"), Set("FISHING ROD"),
        Gems: true, HotPotato: true, Recombobulated: true, Bait: true)
    {
        Stats = StatSet("Fishing Speed", "Sea Creature Chance", "Double Hook Chance", "Trophy Chance", "Treasure Chance")
    };

    private static GearCategorySpec Combat(string id) => new(id, None, None, None, None, CombatTools, Stars: true) { Stats = CombatStats };

    private static readonly Dictionary<string, GearCategorySpec> Specs = new GearCategorySpec[]
    {
        FarmingSpec, ForagingSpec, MiningOreSpec, MiningGemSpec,
        FishingSpec, FishingSpec with { Id = FishingLava },
        Combat(Dungeon), Combat(Kuudra), Combat(Other)
    }.ToDictionary(s => s.Id, StringComparer.Ordinal);

    /// <summary>
    /// Rods that fish in lava: every NotEnoughUpdates-REPO item (upstream/master fac572e18) whose lore has the "Lava Rod" line.
    /// Lava categories only use these, water categories never.
    /// </summary>
    public static readonly IReadOnlySet<string> LavaRods = Set("BINGO_LAVA_ROD", "ELLES_LAVA_ROD", "HELLFIRE_ROD", "INFERNO_ROD",
        "MAGMA_ROD", "POLISHED_TOPAZ_ROD", "STARTER_LAVA_ROD");

    /// <summary>Union of every category: what the capture keeps before the task is known.</summary>
    public static readonly GearCategorySpec Capture = Union(Specs.Values);

    /// <summary>The spec of a category id, <see cref="Other"/>'s for unknown or null ids.</summary>
    public static GearCategorySpec For(string? category)
        => category != null && Specs.TryGetValue(category, out var spec) ? spec : Specs[Other];

    private static GearCategorySpec Union(IEnumerable<GearCategorySpec> specs)
    {
        var all = specs.ToList();
        IReadOnlySet<string> Join(Func<GearCategorySpec, IReadOnlySet<string>> pick)
            => all.SelectMany(pick).ToHashSet(StringComparer.Ordinal);
        return new GearCategorySpec("capture", Join(s => s.Enchants), Join(s => s.Counters), Join(s => s.Applied),
            Join(s => s.Parts), Join(s => s.ToolTypes),
            Turbo: true, Gems: true, HotPotato: true, Recombobulated: true, Stars: true, Bait: true)
        {
            Stats = Join(s => s.Stats)
        };
    }
}
