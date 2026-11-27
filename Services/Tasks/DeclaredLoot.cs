using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Rare loot tables of tasks whose value is mostly rare drops, declared so that
/// <see cref="TaskPeriodFolder.UndeclaredExpensive"/> keeps them (see <see cref="MethodTask.DeclaredDrops"/>). The
/// Catacombs and slayer tables are the RNG meter drop lists of the NotEnoughUpdates repo
/// (constants/rngscore.json, upstream 2026-11-27), completed with the items a 7 day production audit (2026-11-27) found
/// in the task's periods and that are a plausible drop of it, in the NEU notation: "NAME;n" is the enchanted book NAME at level n
/// (ultimate enchantments at every level up to n, chests roll the lower ones too). Runes, the retired attribute shards and
/// the Wisp potion (its item tag is POTION_wisp_ice, declared by the Blaze Slayer task itself) are left out.
/// </summary>
public static class DeclaredLoot
{
    private static readonly Dictionary<string, string[]> CatacombsRngMeter = new()
    {
        ["F1"] =
        [
            "BONZO_STAFF", "ULTIMATE_JERRY;1", "FUMING_POTATO_BOOK", "ULTIMATE_BANK;1", "RECOMBOBULATOR_3000",
            "BONZO_MASK", "RED_NOSE", "BALLOON_SNAKE", "HOT_POTATO_BOOK", "INFINITE_QUIVER;6", "FEATHER_FALLING;6",
            "NECROMANCER_BROOCH", "ULTIMATE_COMBO;1", "ULTIMATE_NO_PAIN_NO_GAIN;1", "REJUVENATE;1"
        ],
        ["F2"] =
        [
            "RED_SCARF", "RECOMBOBULATOR_3000", "STONE_BLADE", "ULTIMATE_BANK;1", "ADAPTIVE_BELT",
            "FUMING_POTATO_BOOK", "SCARF_STUDIES", "HOT_POTATO_BOOK", "ULTIMATE_COMBO;1",
            "ULTIMATE_NO_PAIN_NO_GAIN;1", "NECROMANCER_BROOCH", "ULTIMATE_WISE;1", "REJUVENATE;1"
        ],
        ["F3"] =
        [
            "ADAPTIVE_CHESTPLATE", "ADAPTIVE_LEGGINGS", "ULTIMATE_JERRY;2", "ADAPTIVE_HELMET", "ADAPTIVE_BOOTS",
            "FUMING_POTATO_BOOK", "RECOMBOBULATOR_3000", "NECROMANCER_BROOCH", "HOT_POTATO_BOOK", "SUSPICIOUS_VIAL",
            "ULTIMATE_COMBO;1", "ULTIMATE_NO_PAIN_NO_GAIN;1", "ULTIMATE_WISE;1", "ULTIMATE_BANK;2",
            "ULTIMATE_WISDOM;1", "ULTIMATE_LAST_STAND;1", "REJUVENATE;2"
        ],
        ["F4"] =
        [
            "SPIRIT_SWORD", "ITEM_SPIRIT_BOW", "THORNS_BOOTS", "SPIRIT;4", "ULTIMATE_JERRY;2", "SPIRIT_WING",
            "SPIRIT_BONE", "SPIRIT_DECOY", "ULTIMATE_REND;2", "RECOMBOBULATOR_3000", "FUMING_POTATO_BOOK",
            "HOT_POTATO_BOOK", "ULTIMATE_COMBO;1", "ULTIMATE_NO_PAIN_NO_GAIN;1", "ULTIMATE_WISE;1", "ULTIMATE_BANK;2",
            "ULTIMATE_WISDOM;1", "ULTIMATE_LAST_STAND;1", "REJUVENATE;2"
        ],
        ["F5"] =
        [
            "SHADOW_FURY", "LAST_BREATH", "SHADOW_ASSASSIN_CHESTPLATE", "AOTE_STONE", "LIVID_DAGGER",
            "ULTIMATE_JERRY;3", "SHADOW_ASSASSIN_LEGGINGS", "SHADOW_ASSASSIN_CLOAK", "RECOMBOBULATOR_3000",
            "SHADOW_ASSASSIN_HELMET", "SHADOW_ASSASSIN_BOOTS", "FUMING_POTATO_BOOK", "OVERLOAD;1",
            "ULTIMATE_LEGION;1", "LETHALITY;6", "ULTIMATE_COMBO;2", "ULTIMATE_NO_PAIN_NO_GAIN;2", "ULTIMATE_BANK;3",
            "ULTIMATE_WISDOM;2", "DARK_ORB", "ULTIMATE_WISE;2", "REJUVENATE;3", "ULTIMATE_LAST_STAND;2"
        ],
        ["F6"] =
        [
            "PRECURSOR_EYE", "GIANTS_SWORD", "NECROMANCER_LORD_CHESTPLATE", "SUMMONING_RING", "FEL_SKULL",
            "NECROMANCER_SWORD", "NECROMANCER_LORD_LEGGINGS", "SOULWEAVER_GLOVES", "NECROMANCER_LORD_HELMET",
            "SADAN_BROOCH", "ULTIMATE_JERRY;3", "GIANT_TOOTH", "NECROMANCER_LORD_BOOTS", "RECOMBOBULATOR_3000",
            "ULTIMATE_SWARM;1", "ULTIMATE_NO_PAIN_NO_GAIN;2", "ULTIMATE_BANK;3", "FUMING_POTATO_BOOK",
            "ULTIMATE_COMBO;2", "ULTIMATE_WISDOM;2", "ULTIMATE_WISE;2", "REJUVENATE;3", "ULTIMATE_LAST_STAND;2"
        ],
        ["F7"] =
        [
            "STORM_THE_FISH", "MAXOR_THE_FISH", "GOLDOR_THE_FISH", "NECRON_HANDLE", "SHADOW_WARP_SCROLL",
            "WITHER_SHIELD_SCROLL", "IMPLOSION_SCROLL", "AUTO_RECOMBOBULATOR", "WITHER_CHESTPLATE",
            "ULTIMATE_ONE_FOR_ALL;1", "WITHER_LEGGINGS", "FEATHER_FALLING;7", "RECOMBOBULATOR_3000",
            "FUMING_POTATO_BOOK", "WITHER_CATALYST", "ULTIMATE_NO_PAIN_NO_GAIN;2", "WITHER_CLOAK", "WITHER_HELMET",
            "WITHER_BLOOD", "WITHER_BOOTS", "ULTIMATE_BANK;3", "ULTIMATE_WISDOM;2", "ULTIMATE_JERRY;3",
            "ULTIMATE_SOUL_EATER;1", "HOT_POTATO_BOOK", "ULTIMATE_WISE;2", "ULTIMATE_COMBO;2", "REJUVENATE;3",
            "ULTIMATE_LAST_STAND;2", "INFINITE_QUIVER;7", "PRECURSOR_GEAR"
        ],
        ["M1"] =
        [
            "BONZO_STAFF", "ULTIMATE_JERRY;1", "FUMING_POTATO_BOOK", "ULTIMATE_BANK;1", "MASTER_SKULL_TIER_1",
            "BONZO_MASK", "BALLOON_SNAKE", "RECOMBOBULATOR_3000", "RED_NOSE", "HOT_POTATO_BOOK", "INFINITE_QUIVER;6",
            "FEATHER_FALLING;6", "ULTIMATE_COMBO;1", "ULTIMATE_NO_PAIN_NO_GAIN;1", "NECROMANCER_BROOCH",
            "REJUVENATE;1"
        ],
        ["M2"] =
        [
            "RED_SCARF", "STONE_BLADE", "ULTIMATE_BANK;1", "RECOMBOBULATOR_3000", "MASTER_SKULL_TIER_2",
            "ADAPTIVE_BELT", "FUMING_POTATO_BOOK", "SCARF_STUDIES", "HOT_POTATO_BOOK", "ULTIMATE_COMBO;1",
            "ULTIMATE_NO_PAIN_NO_GAIN;1", "NECROMANCER_BROOCH", "ULTIMATE_WISE;1", "REJUVENATE;1"
        ],
        ["M3"] =
        [
            "ADAPTIVE_CHESTPLATE", "ADAPTIVE_LEGGINGS", "ULTIMATE_JERRY;2", "FIRST_MASTER_STAR", "ADAPTIVE_HELMET",
            "ADAPTIVE_BOOTS", "FUMING_POTATO_BOOK", "RECOMBOBULATOR_3000", "MASTER_SKULL_TIER_3",
            "NECROMANCER_BROOCH", "HOT_POTATO_BOOK", "SUSPICIOUS_VIAL", "ULTIMATE_COMBO;1",
            "ULTIMATE_NO_PAIN_NO_GAIN;1", "ULTIMATE_WISE;1", "ULTIMATE_BANK;2", "ULTIMATE_WISDOM;1",
            "ULTIMATE_LAST_STAND;1", "REJUVENATE;2"
        ],
        ["M4"] =
        [
            "ULTIMATE_LAST_STAND;1", "REJUVENATE;2", "ULTIMATE_WISE;1", "ULTIMATE_BANK;2", "ULTIMATE_WISDOM;1",
            "HOT_POTATO_BOOK", "ULTIMATE_COMBO;1", "ULTIMATE_NO_PAIN_NO_GAIN;1", "RECOMBOBULATOR_3000",
            "MASTER_SKULL_TIER_3", "FUMING_POTATO_BOOK", "SECOND_MASTER_STAR", "SPIRIT_WING", "SPIRIT_BONE",
            "SPIRIT_DECOY", "ULTIMATE_REND;2", "SPIRIT_SWORD", "ITEM_SPIRIT_BOW", "THORNS_BOOTS", "SPIRIT;4",
            "SPIRIT_MASK", "ULTIMATE_JERRY;2"
        ],
        ["M5"] =
        [
            "REJUVENATE;3", "ULTIMATE_LAST_STAND;2", "DARK_ORB", "ULTIMATE_WISE;2", "FUMING_POTATO_BOOK",
            "OVERLOAD;1", "ULTIMATE_LEGION;1", "LETHALITY;6", "SHADOW_ASSASSIN_BOOTS", "ULTIMATE_COMBO;2",
            "ULTIMATE_NO_PAIN_NO_GAIN;2", "ULTIMATE_WISDOM;2", "SHADOW_ASSASSIN_HELMET", "ULTIMATE_BANK;3",
            "RECOMBOBULATOR_3000", "SHADOW_ASSASSIN_LEGGINGS", "SHADOW_ASSASSIN_CLOAK", "MASTER_SKULL_TIER_4",
            "LIVID_DAGGER", "ULTIMATE_JERRY;3", "SHADOW_ASSASSIN_CHESTPLATE", "THIRD_MASTER_STAR", "AOTE_STONE",
            "SHADOW_FURY", "LAST_BREATH", "DYE_LIVID"
        ],
        ["M6"] =
        [
            "PRECURSOR_EYE", "GIANTS_SWORD", "NECROMANCER_LORD_CHESTPLATE", "FOURTH_MASTER_STAR", "SUMMONING_RING",
            "MASTER_SKULL_TIER_4", "FEL_SKULL", "NECROMANCER_SWORD", "NECROMANCER_LORD_LEGGINGS", "SOULWEAVER_GLOVES",
            "NECROMANCER_LORD_HELMET", "SADAN_BROOCH", "ULTIMATE_JERRY;3", "GIANT_TOOTH", "NECROMANCER_LORD_BOOTS",
            "RECOMBOBULATOR_3000", "ULTIMATE_SWARM;1", "ULTIMATE_NO_PAIN_NO_GAIN;2", "ULTIMATE_BANK;3",
            "FUMING_POTATO_BOOK", "ULTIMATE_COMBO;2", "ULTIMATE_WISDOM;2", "ULTIMATE_WISE;2", "REJUVENATE;3",
            "ULTIMATE_LAST_STAND;2"
        ],
        ["M7"] =
        [
            "PRECURSOR_GEAR", "ULTIMATE_COMBO;2", "REJUVENATE;3", "ULTIMATE_LAST_STAND;2", "INFINITE_QUIVER;7",
            "ULTIMATE_SOUL_EATER;1", "HOT_POTATO_BOOK", "ULTIMATE_WISE;2", "ULTIMATE_JERRY;3", "RECOMBOBULATOR_3000",
            "ULTIMATE_BANK;3", "ULTIMATE_WISDOM;2", "WITHER_CLOAK", "WITHER_HELMET", "WITHER_BLOOD", "WITHER_BOOTS",
            "FUMING_POTATO_BOOK", "WITHER_CATALYST", "ULTIMATE_NO_PAIN_NO_GAIN;2", "WITHER_LEGGINGS",
            "FEATHER_FALLING;7", "AUTO_RECOMBOBULATOR", "WITHER_CHESTPLATE", "ULTIMATE_ONE_FOR_ALL;1",
            "FIFTH_MASTER_STAR", "SHADOW_WARP_SCROLL", "WITHER_SHIELD_SCROLL", "IMPLOSION_SCROLL",
            "MASTER_SKULL_TIER_5", "THUNDERLORD;7", "NECRON_HANDLE", "DARK_CLAYMORE", "STORM_THE_FISH",
            "MAXOR_THE_FISH", "GOLDOR_THE_FISH", "DYE_NECRON"
        ],
    };

    private static readonly Dictionary<string, string[]> SlayerRngMeter = new()
    {
        ["Revenant Horror"] =
        [
            "DYE_MATCHA", "FOUL_FLESH", "UNDEAD_CATALYST", "SMITE;6", "BEHEADED_HORROR", "REVENANT_CATALYST",
            "FESTERING_MAGGOT", "REVENANT_VISCERA", "SCYTHE_BLADE", "SEVERED_HAND", "SHARD_OF_THE_SHREDDED",
            "WARDEN_HEART"
        ],
        ["Tarantula Broodfather"] =
        [
            "DYE_BRICK_RED", "TOXIC_ARROW_POISON", "SPIDER_CATALYST", "TARANTULA_CATALYST", "BANE_OF_ARTHROPODS;6",
            "FLY_SWATTER", "TARANTULA_TALISMAN", "VIAL_OF_VENOM", "DIGESTED_MOSQUITO", "SHRIVELED_WASP",
            "ENSNARED_SNAIL", "TARANTULA_SILK", "PRIMORDIAL_EYE"
        ],
        ["Sven Packmaster"] =
        [
            "DYE_CELESTE", "HAMSTER_WHEEL", "FURBALL", "CRITICAL;6", "RED_CLAW_EGG", "GRIZZLY_BAIT",
            "OVERFLUX_CAPACITOR"
        ],
        ["Voidgloom Seraph"] =
        [
            "DYE_BYZANTIUM", "TWILIGHT_ARROW_POISON", "SUMMONING_EYE", "MANA_STEAL;1", "TRANSMISSION_TUNER",
            "HAZMAT_ENDERMAN", "NULL_ATOM", "POCKET_ESPRESSO_MACHINE", "SMARTY_PANTS;1", "HANDY_BLOOD_CHALICE",
            "SINFUL_DICE", "EXCEEDINGLY_RARE_ENDER_ARTIFACT_UPGRADER", "ETHERWARP_MERGER", "PET_SKIN_ENDERMAN_SLAYER",
            "ENDSTONE_IDOL", "JUDGEMENT_CORE"
        ],
        ["Inferno Demonlord"] =
        [
            "DYE_FLAME", "ENCHANTED_BLAZE_POWDER", "ARROW_BUNDLE_MAGMA", "MANA_DISINTEGRATOR",
            "SCORCHED_BOOKS", "KELVIN_INVERTER", "CRUDE_GABAGOOL_DISTILLATE", "SCORCHED_POWER_CRYSTAL",
            "ARCHFIEND_DICE", "SMOLDERING;6", "FINE_OPAL_GEM", "ULTIMATE_REITERATE;1", "HIGH_CLASS_ARCHFIEND_DICE",
            "WILSON_ENGINEERING_PLANS", "SUBZERO_INVERTER"
        ],
        ["Riftstalker Bloodfiend"] =
        [
            "DYE_SANGRIA", "ENCHANTED_BOOK_BUNDLE_QUANTUM", "BUBBA_BLISTER", "CHOCOLATE_CHIP", "GUARDIAN_LUCKY_BLOCK",
            "MCGRUBBER_BURGER", "UNFANGED_VAMPIRE_PART", "ENCHANTED_BOOK_BUNDLE_THE_ONE"
        ],
    };

    /// <summary>
    /// Chest loot the RNG meter does not list but which is worth more than the rare threshold, seen in production periods of
    /// the floor (7 day audit 2026-11-27): the Giant fragments on Sadan's and the Wither floors.
    /// </summary>
    private static readonly Dictionary<string, string[]> CatacombsExtra = new()
    {
        ["F6"] = ["GIANT_FRAGMENT_LASER", "GIANT_FRAGMENT_BOULDER", "GIANT_FRAGMENT_DIAMOND", "GIANT_FRAGMENT_BIGFOOT"],
        ["M6"] = ["GIANT_FRAGMENT_LASER", "GIANT_FRAGMENT_BOULDER", "GIANT_FRAGMENT_DIAMOND", "GIANT_FRAGMENT_BIGFOOT"],
        ["F7"] = ["GIANT_FRAGMENT_LASER"],
        ["M7"] = ["GIANT_FRAGMENT_LASER", "GIANT_FRAGMENT_DIAMOND"]
    };

    /// <summary>
    /// Loot every floor's chests (or secrets) can give: the dyes and the General's Medallion found in secrets, the
    /// Recombobulator and potato books, and the ultimate books of the reward chests (audit 2026-11-27: Legion I on every
    /// floor from F4 up).
    /// </summary>
    private static readonly string[] CatacombsShared =
    [
        "DYE_BONE", "DYE_SECRET", "GENERAL_MEDALLION", "RECOMBOBULATOR_3000", "FUMING_POTATO_BOOK", "HOT_POTATO_BOOK",
        "ULTIMATE_LEGION;1", "ULTIMATE_WISDOM;2", "ULTIMATE_LAST_STAND;2", "ULTIMATE_BANK;3", "ULTIMATE_COMBO;2"
    ];

    /// <summary>
    /// Loot of every floor from the fourth up (Normal and Master): the Treasure Talisman and the Ice Spray Wand (a dungeon
    /// item with a gear score; audit 2026-11-27: 6-7 players on F6 and F7).
    /// </summary>
    private static readonly string[] CatacombsFromFloorFour = ["TREASURE_TALISMAN", "ICE_SPRAY_WAND"];

    private const string MasterSkull = "MASTER_SKULL_TIER_";

    /// <summary>The rare chest loot of one Catacombs floor ("F1".."F7", "M1".."M7"); empty for an unknown floor.</summary>
    public static HashSet<string> CatacombsFloor(string floor)
    {
        if (floor == null || !CatacombsRngMeter.TryGetValue(floor, out var rngMeter))
            return [];
        return Expand(rngMeter.Concat(CatacombsExtra.GetValueOrDefault(floor, [])).Concat(CatacombsShared)
            .Concat(floor[1] >= '4' ? CatacombsFromFloorFour : [])
            .Concat(LowerMasterSkulls(rngMeter)));
    }

    /// <summary>A Master floor's chests also roll the lower Master Skull tiers (audit 2026-11-27: tier 3 at M5, 6 players).</summary>
    private static IEnumerable<string> LowerMasterSkulls(IEnumerable<string> loot)
        => loot.Where(tag => tag.StartsWith(MasterSkull, StringComparison.Ordinal))
            .SelectMany(tag => Enumerable.Range(1, int.Parse(tag[MasterSkull.Length..]) - 1).Select(tier => MasterSkull + tier));

    /// <summary>
    /// The rare drops of one slayer boss, named like the RNG meter ("Revenant Horror", "Voidgloom Seraph"), plus the Paragon
    /// shard every slayer's periods held in production (audit 2026-11-27, 9-42 players per slayer).
    /// </summary>
    public static HashSet<string> Slayer(string boss) => Expand(SlayerRngMeter[boss].Append("SHARD_PARAGON"));

    /// <summary>
    /// Kuudra paid chest loot besides <see cref="KuudraRewardAttribution.ClaimLootTags"/>: the chest's armor and equipment,
    /// the Kuudra books and the rare extras (wiki "Kuudra" rewards). Its shards are declared by family, see
    /// <see cref="BaseKuudraTask"/>.
    /// </summary>
    public static readonly HashSet<string> KuudraChest = Expand(
    [
        .. KuudraRewardAttribution.ClaimLootTags,
        .. new[] { "CRIMSON", "AURORA", "TERROR", "FERVOR", "HOLLOW" }
            .SelectMany(set => new[] { "HOT_", "BURNING_", "FIERY_", "INFERNAL_" }
                .SelectMany(tier => new[] { "HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS" }.Select(piece => $"{tier}{set}_{piece}"))),
        "MOLTEN_BELT", "MOLTEN_BRACELET", "MOLTEN_CLOAK", "MOLTEN_NECKLACE",
        "HOLLOW_WAND", "RUNIC_STAFF", "ENRAGER", "TORMENTOR", "MANDRAA", "WHEEL_OF_FATE", "BURNING_KUUDRA_CORE", "TENTACLE_DYE",
        "ULTIMATE_FATAL_TEMPO;5", "ULTIMATE_INFERNO;5",
        .. new[] { "STRONG_MANA", "FEROCIOUS_MANA", "HARDENED_MANA", "MANA_VAMPIRE" }
            .SelectMany(book => Enumerable.Range(1, 10).Select(level => $"{book};{level}"))
    ]);

    /// <summary>
    /// Sea creature drops worth more than the rare threshold that every fishing method can catch (wiki: Lord Jawbus'
    /// Radioactive Vial, the Fishing Festival shark's tooth, the Yeti's sword and pet, the Flying Fish and Megalodon pets).
    /// </summary>
    public static readonly HashSet<string> SeaCreatures =
    [
        "RADIOACTIVE_VIAL", "GREAT_WHITE_SHARK_TOOTH", "YETI_SWORD", "PET_BABY_YETI", "PET_FLYING_FISH", "PET_MEGALODON",
        // NEU: "Sources: Fishing (I)"
        "ENCHANTMENT_QUICK_BITE_1"
    ];

    /// <summary>
    /// Lava sea creature drops of the Crimson Isle (both Crimson Fishing tasks, audit 2026-11-27: 4-31 players each):
    /// the Fireproof Witch's powder, Ragnarok's texts and dye, the Fiery Scuttler's shell, the Plhlegblast and Lord Jawbus
    /// rod parts, Magmarizer VI (NEU: "Sources: Fishing (VI)").
    /// </summary>
    public static readonly HashSet<string> LavaSeaCreatures = new(SeaCreatures)
    {
        "SINGED_POWDER", "BURNT_TEXTS", "DYE_CARMINE", "SCUTTLER_SHELL", "SEVERED_PINCER", "CHAIN_END_TIMES",
        "BRIMSTONE_HANDLE", "ENCHANTMENT_MAGMARIZER_6"
    };

    /// <summary>Backwater Bayou sea creature and hotspot drops (both Bayou Fishing tasks and the Bayou hotspot).</summary>
    public static readonly HashSet<string> BayouSeaCreatures = new(SeaCreatures)
    {
        "TITANOBOA_SHED", "TIKI_MASK", "SNAKE_EYES", "TROUBLED_BUBBLE", "BAYOU_WATER_ORB", "MOBY_DUCK", "HOTSPOT_HOOK"
    };

    /// <summary>Spooky sea creature and hotspot rod parts (both Spooky Fishing tasks), besides the Spooky evidence drops.</summary>
    public static readonly HashSet<string> SpookySeaCreatures = new(SeaCreatures) { "HOTSPOT_HOOK", "SHREDDED_LINE" };

    /// <summary>Expands the NEU notation ("ULTIMATE_BANK;3", "REJUVENATE;2") into item tags.</summary>
    internal static HashSet<string> Expand(IEnumerable<string> entries)
        => new(entries.SelectMany(ExpandOne), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ExpandOne(string entry)
    {
        var split = entry.Split(';');
        if (split.Length != 2 || !int.TryParse(split[1], out var level))
            return [entry];
        var name = split[0];
        if (!name.StartsWith("ULTIMATE_", StringComparison.Ordinal))
            return [$"ENCHANTMENT_{name}_{level}"];
        return Enumerable.Range(1, level).Select(l => $"ENCHANTMENT_{name}_{l}");
    }
}
