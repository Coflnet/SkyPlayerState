using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Coflnet.Sky.PlayerState.Tasks;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// A special effect of gear or a pet that is not a lore stat line (a set bonus, a pet perk, an equipment ability), as an
/// approximate stat equivalent. One row of <c>gear-effects.json</c>, see <see cref="GearEffectTable"/>.
/// </summary>
public sealed record GearEffect
{
    public const string KindSet = "set";
    public const string KindPet = "pet";
    public const string KindEquipment = "equipment";
    public const string KindEnchant = "enchant";
    public const string Add = "add";
    public const string Multiply = "multiply";

    public string Id { get; init; } = "";
    /// <summary>set, pet, equipment or enchant.</summary>
    public string Kind { get; init; } = "";
    /// <summary>add (the value is added to each stat) or multiply (the value is the fraction added: 0.2 = x1.2).</summary>
    public string Mode { get; init; } = Add;
    public List<string> Stats { get; init; } = new();
    /// <summary>Pets: the value at <see cref="AtLevel"/>, linear in the level; equipment: flat; enchants: per enchant level.</summary>
    public double Value { get; init; }
    /// <summary>Sets: the value per worn piece count (index 0 = 1 piece, the last one also for more pieces).</summary>
    public List<double>? Tiers { get; init; }
    /// <summary>Sets: the member tags; equipment: the tag(s) that have the effect.</summary>
    public List<string>? Tags { get; init; }
    /// <summary>Multipliers only: scale the stats of the worn pieces with these tags instead of the build totals.</summary>
    public List<string>? PieceTags { get; init; }
    public string? PetType { get; init; }
    public string? MinRarity { get; init; }
    /// <summary>The pet level <see cref="Value"/> belongs to: 100, 200 for the dragon pets.</summary>
    public int AtLevel { get; init; } = 100;
    public string? Enchant { get; init; }
    /// <summary>Only for tasks of this gear category (null = every category).</summary>
    public string? Category { get; init; }
    /// <summary>Only for tasks located in these islands or zones (<see cref="SkyblockZones"/> names), see <see cref="GearEffectTable.InZones"/>.</summary>
    public List<string>? Zones { get; init; }
    /// <summary>Only when the build's tool has this lore item type (e.g. DRILL).</summary>
    public string? ToolType { get; init; }
    public string Confidence { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Note { get; init; }

    public bool IsMultiplier => Mode == Multiply;
}

/// <summary>The task an effect's conditions are checked against.</summary>
public sealed record GearTaskContext(string Task, string Category, IReadOnlyCollection<string> Locations)
{
    public static GearTaskContext For(string task, string category) => new(task, category, GearCategories.LocationsOf(task));
}

/// <summary>
/// The special effects table (<c>Services/Gear/gear-effects.json</c>, embedded), produced from the effects research
/// dossier; every row needs a source URL, a row without one fails the load. Unknown effects count 0. The values are
/// approximations and TUNABLE in the data review.
/// </summary>
public sealed class GearEffectTable
{
    public const string ResourceName = "Coflnet.Sky.PlayerState.Services.Gear.gear-effects.json";
    private static readonly string[] Kinds = [GearEffect.KindSet, GearEffect.KindPet, GearEffect.KindEquipment, GearEffect.KindEnchant];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<GearEffectTable> embedded = new(LoadEmbedded);

    public static readonly GearEffectTable Empty = new([]);
    public static GearEffectTable Embedded => embedded.Value;

    public IReadOnlyList<GearEffect> Effects { get; }

    public GearEffectTable(IReadOnlyList<GearEffect> effects) => Effects = effects;

    private sealed class EffectFile
    {
        public List<GearEffect> Effects { get; set; } = new();
    }

    /// <summary>Parses and validates the table; throws <see cref="InvalidDataException"/> for an invalid row.</summary>
    public static GearEffectTable Load(string json)
    {
        var file = JsonSerializer.Deserialize<EffectFile>(json, Json) ?? new EffectFile();
        foreach (var effect in file.Effects)
            Validate(effect);
        var duplicate = file.Effects.GroupBy(e => e.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidDataException($"gear effect id {duplicate.Key} is used twice");
        return new GearEffectTable(file.Effects);
    }

    /// <summary>The effects whose category and zone conditions the task meets.</summary>
    public IReadOnlyList<GearEffect> For(GearTaskContext context)
        => Effects.Where(e => e.Category == null || e.Category == context.Category)
            .Where(e => e.Zones == null || InZones(e.Zones, context.Locations))
            .ToList();

    /// <summary>
    /// True when at least one task location is in the zones (directly, by its island or the island's group, see
    /// <see cref="SkyblockZones.Matches"/>) and every other location is either in the zones too or resolves to no known
    /// island (such a location is neutral). A location on another known island rules the effect out.
    /// </summary>
    public static bool InZones(IReadOnlyCollection<string> zones, IReadOnlyCollection<string> locations)
    {
        var matching = locations.Where(l => SkyblockZones.Matches(zones, l)).ToHashSet(StringComparer.Ordinal);
        if (matching.Count == 0)
            return false;
        return locations.All(l => matching.Contains(l) || SkyblockZones.IslandOf(l) == null);
    }

    private static void Validate(GearEffect effect)
    {
        var problem = Problem(effect);
        if (problem != null)
            throw new InvalidDataException($"gear effect {effect.Id}: {problem}");
    }

    private static string? Problem(GearEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.Id))
            return "no id";
        if (!effect.Source.StartsWith("https://", StringComparison.Ordinal))
            return "no source url";
        if (!Kinds.Contains(effect.Kind))
            return $"unknown kind {effect.Kind}";
        if (effect.Mode is not (GearEffect.Add or GearEffect.Multiply))
            return $"unknown mode {effect.Mode}";
        if (effect.Stats.Count == 0)
            return "no stats";
        return KindProblem(effect);
    }

    private static string? KindProblem(GearEffect effect) => effect.Kind switch
    {
        GearEffect.KindSet when effect.Tags is not { Count: > 0 } || effect.Tiers is not { Count: > 0 } => "a set needs tags and tiers",
        GearEffect.KindPet when string.IsNullOrEmpty(effect.PetType) || GearRarities.Rank(effect.MinRarity) < 0 => "a pet needs a type and a known minRarity",
        GearEffect.KindPet when effect.AtLevel <= 0 => "atLevel must be positive",
        GearEffect.KindEquipment when effect.Tags is not { Count: > 0 } => "equipment needs tags",
        GearEffect.KindEnchant when string.IsNullOrEmpty(effect.Enchant) => "an enchant effect needs the enchant name",
        _ => null
    };

    private static GearEffectTable LoadEmbedded()
    {
        using var stream = typeof(GearEffectTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }
}

/// <summary>Rarity order of items and pets.</summary>
public static class GearRarities
{
    private static readonly string[] Order = ["COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC", "DIVINE"];

    /// <summary>0 for COMMON .. 6 for DIVINE, -1 for null or unknown.</summary>
    public static int Rank(string? rarity) => rarity == null ? -1 : Array.IndexOf(Order, rarity.ToUpperInvariant());
}
