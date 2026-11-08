using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>A profile with one binding's overrides applied: what the estimator actually computes on.</summary>
public record ResolvedGearProfile(GearProfile Profile, GearBinding Binding, RateModelSpec Model,
    GearSetup Baseline, IReadOnlyList<GearSetup> Setups)
{
    public IEnumerable<GearSetup> AllSetups() => new[] { Baseline }.Concat(Setups);

    /// <summary>The setup the community rate is assumed to be earned with, null when the anchor id is unknown.</summary>
    public GearSetup? Anchor => AllSetups().FirstOrDefault(s => s.Id == Profile.Anchor);
}

/// <summary>Applies a <see cref="GearBinding"/>'s item, stat and model overrides to its profile.</summary>
public static class GearBindingResolver
{
    public static ResolvedGearProfile Resolve(GearProfile profile, GearBinding binding)
    {
        var model = (profile.Model ?? new()).WithOverrides(binding.ModelOverrides);
        var baseline = ResolveSetup(profile.Baseline, binding);
        var setups = (profile.Setups ?? []).Select(s => ResolveSetup(s, binding)).ToList();
        return new ResolvedGearProfile(profile, binding, model, baseline, setups);
    }

    private static GearSetup ResolveSetup(GearSetup setup, GearBinding binding) => setup with
    {
        Items = ApplyItemOverrides(setup.Id, setup.Items ?? [], binding.ItemOverrides),
        Stats = MergeStats(setup.Stats, binding.StatOverrides?.GetValueOrDefault(setup.Id))
    };

    internal static List<GearItem> ApplyItemOverrides(string setupId, List<GearItem> items,
        IReadOnlyDictionary<string, GearItem>? overrides)
    {
        if (overrides == null || overrides.Count == 0)
            return items;
        var result = items.ToList();
        foreach (var (slot, item) in SlotOverridesFor(setupId, overrides))
        {
            var index = result.FindIndex(i => string.Equals(i?.Slot, slot, StringComparison.OrdinalIgnoreCase));
            var replacement = item with { Slot = slot };
            if (index >= 0)
                result[index] = replacement;
            else
                result.Add(replacement);
        }
        return result;
    }

    /// <summary>Slot to item for one setup: "setupId:slot" keys win over plain "slot" keys.</summary>
    private static Dictionary<string, GearItem> SlotOverridesFor(string setupId, IReadOnlyDictionary<string, GearItem> overrides)
    {
        var result = new Dictionary<string, GearItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, item) in overrides.Where(o => o.Value != null && !o.Key.Contains(':')))
            result[key] = item;
        foreach (var (key, item) in overrides.Where(o => o.Value != null && o.Key.Contains(':')))
        {
            var (targetSetup, slot) = SplitScopedKey(key);
            if (string.Equals(targetSetup, setupId, StringComparison.OrdinalIgnoreCase))
                result[slot] = item;
        }
        return result;
    }

    internal static (string SetupId, string Slot) SplitScopedKey(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? ("", key) : (key[..separator], key[(separator + 1)..]);
    }

    private static Dictionary<string, double> MergeStats(Dictionary<string, double>? stats, Dictionary<string, double>? overrides)
    {
        var result = new Dictionary<string, double>(stats ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides ?? new())
            result[key] = value;
        return result;
    }
}
