using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Tier = Coflnet.Sky.Core.Tier;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Reduces an uploaded item to a <see cref="GearPiece"/> holding only the modifiers <see cref="GearModifierTable"/> lists
/// for a category. Without a category the union of all categories is kept (<see cref="GearModifierTable.Capture"/>), so
/// gear captured before the task is known can later be narrowed with <see cref="Filter"/>.
/// </summary>
public static class GearNormalizer
{
    private static readonly Regex GemSlot = new(@"^([A-Z]+)_(\d+)$", RegexOptions.Compiled);
    private static readonly Regex PetLevel = new(@"\[Lvl (\d+)\]", RegexOptions.Compiled);
    private static readonly HashSet<string> GenericGemSlots = new(StringComparer.Ordinal) { "COMBAT", "OFFENSIVE", "DEFENSIVE", "UNIVERSAL", "MINING", "CHISEL" };
    private static readonly HashSet<string> GemQualities = new(StringComparer.Ordinal) { "ROUGH", "FLAWED", "FINE", "FLAWLESS", "PERFECT" };
    private static readonly string[] RodParts = ["hook", "line", "sinker"];

    /// <summary>The item's rate relevant modifiers, null for an empty slot.</summary>
    public static GearPiece? Normalize(Item? item, string? category = null, string? task = null)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Tag))
            return null;
        var piece = ReadAll(item);
        return category == null ? piece : Filter(piece, category, task);
    }

    /// <summary>
    /// Narrows a captured piece to a category: drops modifiers the category does not list and every Turbo enchant but
    /// the task's crop (all Turbo enchants stay while the crop is unknown).
    /// </summary>
    public static GearPiece Filter(GearPiece piece, string? category, string? task)
    {
        var spec = GearModifierTable.For(category);
        var turbo = FarmingCrops.ForTask(task)?.TurboEnchant;
        return new GearPiece
        {
            Tag = piece.Tag,
            Rarity = piece.Rarity,
            Reforge = piece.Reforge,
            ItemType = piece.ItemType,
            Stars = spec.Stars ? piece.Stars : null,
            HotPotatoCount = spec.HotPotato ? piece.HotPotatoCount : null,
            Recombobulated = spec.Recombobulated ? piece.Recombobulated : null,
            Enchants = Keep(piece.Enchants, name => KeepsEnchant(spec, name, turbo)),
            Gems = spec.Gems ? new(piece.Gems ?? new()) : new(),
            Parts = Keep(piece.Parts, spec.Parts.Contains),
            Counters = Keep(piece.Counters, spec.Counters.Contains),
            Applied = Keep(piece.Applied, spec.Applied.Contains),
            Stats = FilterStats(piece.Stats, category, task)
        };
    }

    /// <summary>The category's stats; crop fortunes only for the task's crop (all while the crop is unknown).</summary>
    public static Dictionary<string, double> FilterStats(Dictionary<string, double>? stats, string? category, string? task)
    {
        var spec = GearModifierTable.For(category);
        var crop = FarmingCrops.ForTask(task)?.FortuneStat;
        return Keep(stats, name => spec.Stats.Contains(name)
            && (crop == null || !FarmingCrops.CropFortuneStats.Contains(name) || name == crop));
    }

    /// <summary>The pet with only the category's stats.</summary>
    public static GearPet FilterPet(GearPet pet, string? category, string? task)
        => new(pet) { Stats = FilterStats(pet.Stats, category, task) };

    private static bool KeepsEnchant(GearCategorySpec spec, string name, string? turbo)
    {
        if (!FarmingCrops.IsTurbo(name))
            return spec.Enchants.Contains(name);
        return spec.Turbo && (turbo == null || turbo == name);
    }

    private static Dictionary<string, T> Keep<T>(Dictionary<string, T>? values, Func<string, bool> keep)
        => (values ?? new()).Where(v => keep(v.Key)).ToDictionary(v => v.Key, v => v.Value);

    private static GearPiece ReadAll(Item item)
    {
        var attributes = item.ExtraAttributes;
        var spec = GearModifierTable.Capture;
        var (footerRarity, itemType) = ItemFooter.Read(item);
        return new GearPiece
        {
            Tag = item.Tag,
            Rarity = ReadRarity(attributes) ?? footerRarity,
            ItemType = itemType,
            Reforge = GearAttributes.GetString(attributes, "modifier")?.ToLowerInvariant(),
            Stars = Positive(GearAttributes.GetLong(attributes, "upgrade_level") ?? GearAttributes.GetLong(attributes, "dungeon_item_level")),
            HotPotatoCount = Positive(GearAttributes.GetLong(attributes, "hot_potato_count")),
            Recombobulated = GearAttributes.GetLong(attributes, "rarity_upgrades") > 0 ? true : null,
            Enchants = ReadEnchants(item, spec),
            Gems = ReadGems(GearAttributes.AsDictionary(GearAttributes.Get(attributes, "gems"))),
            Parts = ReadParts(attributes),
            Counters = ReadNumbers(attributes, spec.Counters).ToDictionary(c => c.Key, c => c.Value),
            Applied = ReadNumbers(attributes, spec.Applied).ToDictionary(c => c.Key, c => (int)Math.Min(int.MaxValue, c.Value)),
            Stats = LoreStats.Parse(item.Description, spec.Stats)
        };
    }

    private static int? Positive(long? value) => value > 0 ? (int)Math.Min(int.MaxValue, value.Value) : null;

    /// <summary>The pipeline's "tier" (Coflnet <see cref="Tier"/>, numeric or named), recombobulation included.</summary>
    private static string? ReadRarity(IReadOnlyDictionary<string, object>? attributes)
    {
        var raw = GearAttributes.GetString(attributes, "tier");
        if (raw == null || !Enum.TryParse<Tier>(raw, true, out var tier) || tier == Tier.UNKNOWN || !Enum.IsDefined(tier))
            return null;
        return tier.ToString();
    }

    private static Dictionary<string, int> ReadEnchants(Item item, GearCategorySpec spec)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, level) in item.Enchantments ?? new())
            AddEnchant(result, spec, name, level);
        var nested = GearAttributes.AsDictionary(GearAttributes.Get(item.ExtraAttributes, "enchantments"));
        foreach (var (name, level) in nested ?? new())
            AddEnchant(result, spec, name, GearAttributes.ToLong(level));
        return result;
    }

    private static void AddEnchant(Dictionary<string, int> result, GearCategorySpec spec, string? name, long? level)
    {
        if (string.IsNullOrWhiteSpace(name) || level is not > 0)
            return;
        var key = name.Trim().ToLowerInvariant();
        if (spec.Enchants.Contains(key) || FarmingCrops.IsTurbo(key))
            result[key] = (int)Math.Min(int.MaxValue, level.Value);
    }

    /// <summary>
    /// "JASPER_0": "FINE" (typed slot) or "COMBAT_0": "PERFECT" + "COMBAT_0_gem": "JASPER" (generic slot), the quality
    /// optionally wrapped as {"quality": ..., "uuid": ...} -> slot to FINE_JASPER_GEM. unlocked_slots is ignored.
    /// </summary>
    internal static Dictionary<string, string> ReadGems(Dictionary<string, object>? gems)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (gems == null)
            return result;
        foreach (var (slot, value) in gems)
        {
            var tag = GemTag(gems, slot, value);
            if (tag != null)
                result[slot] = tag;
        }
        return result;
    }

    private static string? GemTag(Dictionary<string, object> gems, string slot, object value)
    {
        var match = GemSlot.Match(slot);
        if (!match.Success)
            return null;
        var quality = GemQuality(value);
        if (quality == null)
            return null;
        var type = GearAttributes.GetString(gems, slot + "_gem")?.ToUpperInvariant() ?? match.Groups[1].Value;
        if (GenericGemSlots.Contains(type))
            return null;
        return $"{quality}_{type}_GEM";
    }

    private static string? GemQuality(object value)
    {
        var nested = GearAttributes.AsDictionary(value);
        var raw = nested != null ? GearAttributes.GetString(nested, "quality") : GearAttributes.AsString(value);
        var quality = raw?.ToUpperInvariant();
        return quality != null && GemQualities.Contains(quality) ? quality : null;
    }

    private static Dictionary<string, string> ReadParts(IReadOnlyDictionary<string, object>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in GearModifierTable.Capture.Parts.Where(p => p.StartsWith("drill_part_", StringComparison.Ordinal)))
            AddPart(result, key, GearAttributes.GetString(attributes, key));
        foreach (var part in RodParts)
            AddPart(result, part, RodPart(attributes, part));
        return result;
    }

    /// <summary>Rod parts are compounds {"part": "speedy_line", ...}; the auction pipeline flattens them to "line.part".</summary>
    private static string? RodPart(IReadOnlyDictionary<string, object>? attributes, string part)
    {
        var nested = GearAttributes.AsDictionary(GearAttributes.Get(attributes, part));
        return (nested != null ? GearAttributes.GetString(nested, "part") : null)
            ?? GearAttributes.GetString(attributes, part + ".part");
    }

    private static void AddPart(Dictionary<string, string> result, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            result[key] = value.Trim().ToUpperInvariant();
    }

    private static IEnumerable<KeyValuePair<string, long>> ReadNumbers(IReadOnlyDictionary<string, object>? attributes, IEnumerable<string> keys)
    {
        foreach (var key in keys)
            if (GearAttributes.GetLong(attributes, key) is > 0 and var value)
                yield return new(key, value);
    }

    /// <summary>
    /// The pet of a menu slot item (petInfo compound or json string; without petInfo the PET_ tag and the footer rarity),
    /// null when the item is no pet.
    /// </summary>
    public static GearPet? NormalizePet(Item? item)
    {
        var info = GearAttributes.AsDictionary(GearAttributes.Get(item?.ExtraAttributes, "petInfo"));
        var type = info == null ? TypeFromTag(item?.Tag) : GearAttributes.GetString(info, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;
        return new GearPet
        {
            Type = type.ToUpperInvariant(),
            Tier = (info == null ? ItemFooter.Read(item).Rarity : GearAttributes.GetString(info, "tier"))?.ToUpperInvariant(),
            Level = ParsePetLevel(item?.ItemName),
            HeldItem = GearAttributes.GetString(info, "heldItem"),
            Stats = LoreStats.Parse(item?.Description, GearModifierTable.Capture.Stats)
        };
    }

    /// <summary>"PET_GRANDMA_WOLF" -> GRANDMA_WOLF, for a pet item whose petInfo did not arrive.</summary>
    private static string? TypeFromTag(string? tag)
        => tag != null && tag.StartsWith("PET_", StringComparison.Ordinal) && tag.Length > 4 ? tag[4..] : null;

    /// <summary>The pet item is the summoned one (petInfo.active).</summary>
    public static bool IsActivePet(Item? item)
    {
        var info = GearAttributes.AsDictionary(GearAttributes.Get(item?.ExtraAttributes, "petInfo"));
        var active = info == null ? null : GearAttributes.Get(info, "active");
        return active is true || GearAttributes.ToLong(active) == 1;
    }

    public static GearPet? FromPetState(ExtractedInfo.PetState? pet)
    {
        if (string.IsNullOrWhiteSpace(pet?.Type))
            return null;
        return new GearPet { Type = pet.Type.ToUpperInvariant(), Tier = pet.Tier?.ToUpperInvariant(), Level = pet.Level, HeldItem = pet.HeldItem };
    }

    private static int ParsePetLevel(string? name)
    {
        var match = name == null ? Match.Empty : PetLevel.Match(MenuTitleNormalizer.StripColors(name));
        return match.Success && int.TryParse(match.Groups[1].Value, out var level) ? level : 0;
    }
}
