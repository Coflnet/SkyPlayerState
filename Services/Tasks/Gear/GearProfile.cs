using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

// Input side of the gear setups feature: one researched profile per activity family (farming,
// mining, fishing, ...), loaded from Services/Tasks/Gear/profiles/*.json by GearProfileCatalog and
// bound to tasks by MethodTask name. See profiles/README.md for the exact schema.

/// <summary>The setup tiers every profile must offer exactly once, cheapest first.</summary>
public static class GearTiers
{
    public const string Baseline = "baseline";
    public const string Budget = "budget";
    public const string Mid = "mid";
    public const string Endgame = "endgame";

    public static readonly IReadOnlyList<string> Upgrades = [Budget, Mid, Endgame];

    /// <summary>Sort rank: baseline first, unknown tiers last.</summary>
    public static int Rank(string? tier)
    {
        if (tier == Baseline)
            return 0;
        var index = Upgrades.ToList().IndexOf(tier ?? "");
        return index < 0 ? int.MaxValue : index + 1;
    }
}

/// <summary>One researched activity family with a baseline, three upgrade setups and the tasks it applies to.</summary>
public record GearProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Setup id the community rate is assumed to be earned with, default <see cref="GearTiers.Budget"/>.</summary>
    public string Anchor { get; init; } = GearTiers.Budget;
    /// <summary>ISO date the research was done, shown so stale profiles are recognizable.</summary>
    public string? ResearchedAt { get; init; }
    /// <summary>https links backing the profile (required, at least one).</summary>
    public List<string> Sources { get; init; } = [];
    public RateModelSpec Model { get; init; } = new();
    /// <summary>tierUnlock only (required there): the task ladder, lowest tier first. Bound and unlocked tasks must be in it.</summary>
    public List<string>? Tiers { get; init; }
    /// <summary>Optional cap on the model confidence (high, medium or low), e.g. medium for an unverified formula.</summary>
    public string? ModelConfidence { get; init; }
    /// <summary>
    /// Optional upper clamp of the scaled multiplier, 5 to 20 (default <see cref="GearSetupEstimator.MaxMultiplier"/>),
    /// for activities where top gear really is worth many times the anchor's rate. Caps confidence at medium.
    /// </summary>
    public double? MaxMultiplier { get; init; }
    /// <summary>Optional upper limit per stat name, applied to every setup's stats before the rate model.</summary>
    public Dictionary<string, double>? StatCaps { get; init; }
    public GearSetup Baseline { get; init; } = new();
    public List<GearSetup> Setups { get; init; } = [];
    public List<GearBinding> Bindings { get; init; } = [];

    /// <summary>Baseline first, then the upgrade setups in file order.</summary>
    public IEnumerable<GearSetup> AllSetups() => new[] { Baseline }.Concat(Setups ?? []).Where(s => s != null);
}

/// <summary>One gear setup. The baseline uses id and tier <see cref="GearTiers.Baseline"/>.</summary>
public record GearSetup
{
    public string Id { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Name { get; init; } = "";
    public List<GearItem> Items { get; init; } = [];
    /// <summary>Stat totals of the whole setup, keyed by stat name (e.g. "Farming Fortune"); the rate model reads them.</summary>
    public Dictionary<string, double> Stats { get; init; } = new();
    /// <summary>Seconds per run, required by the runTime model.</summary>
    public double? RunSeconds { get; init; }
    /// <summary>tierUnlock only, required on every upgrade setup: the tier (task) this gear is for, see <see cref="GearProfile.Tiers"/>.</summary>
    public string? UnlocksTask { get; init; }
    public List<string> Sources { get; init; } = [];
    public List<string> Assumptions { get; init; } = [];
}

/// <summary>One item of a setup.</summary>
public record GearItem
{
    /// <summary>Unique within a setup, e.g. helmet, chestplate, tool, pet.</summary>
    public string Slot { get; init; } = "";
    [JsonPropertyName("tag")]
    public string ItemTag { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Why the item is in the setup, e.g. "+30 Farming Fortune".</summary>
    public string Reason { get; init; } = "";
    /// <summary>Optional auction filters (e.g. pet level/rarity) for variant priced items; null for plain tags.</summary>
    public Dictionary<string, string>? PriceFilters { get; init; }
    /// <summary>False for items that cannot be bought (soulbound): listed, but not part of the setup's cost.</summary>
    public bool Tradeable { get; init; } = true;
}

/// <summary>Applies a profile to one task (by MethodTask name), with optional per-task overrides.</summary>
public record GearBinding
{
    public string Task { get; init; } = "";
    /// <summary>
    /// Replaces (or adds) items by slot. Key "slot" applies to every setup including the baseline,
    /// "setupId:slot" to that one setup only and wins over the plain slot key.
    /// </summary>
    public Dictionary<string, GearItem>? ItemOverrides { get; init; }
    /// <summary>Drop tags whose revenue scales with the rare factor (magic find) instead of the bulk factor.</summary>
    public List<string>? RareDropTags { get; init; }
    /// <summary>Model parameters replacing the profile's ones for this task (e.g. another block hardness).</summary>
    public Dictionary<string, JsonElement>? ModelOverrides { get; init; }
    /// <summary>Per setup id, stat values merged over that setup's stats for this task.</summary>
    public Dictionary<string, Dictionary<string, double>>? StatOverrides { get; init; }
}

/// <summary>
/// Rate model kind plus its flat parameters (everything in the "model" object except "kind").
/// See <see cref="GearRateModels"/> for the parameter names per kind.
/// </summary>
public record RateModelSpec
{
    public string Kind { get; init; } = "";
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Params { get; init; } = new();

    public bool Has(string name) => Find(name) is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    public string? GetString(string name) =>
        Find(name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public double? GetDouble(string name) =>
        Find(name) is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : null;

    public double GetDouble(string name, double fallback) => GetDouble(name) ?? fallback;

    public bool GetBool(string name) => Find(name) is { ValueKind: JsonValueKind.True };

    /// <summary>A string array parameter, null when absent or not an array of strings.</summary>
    public List<string>? GetStringList(string name)
    {
        if (Find(name) is not { ValueKind: JsonValueKind.Array } e)
            return null;
        var result = new List<string>();
        foreach (var value in e.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                return null;
            result.Add(value.GetString()!);
        }
        return result;
    }

    /// <summary>A numeric map parameter (e.g. fixed model "multipliers"), null when absent or malformed.</summary>
    public Dictionary<string, double>? GetNumberMap(string name)
    {
        if (Find(name) is not { ValueKind: JsonValueKind.Object } e)
            return null;
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in e.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number)
                return null;
            result[property.Name] = property.Value.GetDouble();
        }
        return result;
    }

    /// <summary>Copy with <paramref name="overrides"/> replacing parameters of the same name.</summary>
    public RateModelSpec WithOverrides(IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        if (overrides == null || overrides.Count == 0)
            return this;
        var merged = new Dictionary<string, JsonElement>(Params ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides)
            merged[key] = value;
        return this with { Params = merged };
    }

    private JsonElement? Find(string name)
    {
        if (Params == null)
            return null;
        foreach (var (key, value) in Params)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }
}
