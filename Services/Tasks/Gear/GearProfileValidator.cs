using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// Structural checks of a <see cref="GearProfile"/> that need no task registry: tiers, ids, slots,
/// sources, tag shape and the rate model parameters (per binding, after overrides). Task existence is
/// checked separately by <see cref="RegistryErrors"/>.
/// </summary>
public static partial class GearProfileValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ProfileIdPattern();

    /// <summary>Hypixel item tags: upper case letters, digits, underscore, plus ':' / ';' / '-' variants.</summary>
    [GeneratedRegex("^[A-Z0-9_][A-Z0-9_:;\\-]*$")]
    private static partial Regex ItemTagPattern();

    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public static bool IsItemTag(string? value) => value != null && ItemTagPattern().IsMatch(value);

    public static List<string> Validate(GearProfile profile)
    {
        var errors = new List<string>();
        if (profile == null)
        {
            errors.Add("profile is empty");
            return errors;
        }
        CheckHeader(profile, errors);
        CheckSetups(profile, errors);
        CheckTierLadder(profile, errors);
        var keyErrors = CaseDuplicateKeys(profile);
        errors.AddRange(keyErrors);
        // resolving merges dictionaries case-insensitively, which throws on such keys
        CheckBindings(profile, errors, resolve: keyErrors.Count == 0);
        return errors.Select(e => $"profile '{profile.Id}': {e}").ToList();
    }

    /// <summary>Bindings and unlocksTask targets that do not name a public (non hidden) method task.</summary>
    public static List<string> RegistryErrors(GearProfileCatalog catalog, TaskRegistry registry)
    {
        var errors = new List<string>();
        foreach (var profile in catalog.Profiles)
        {
            foreach (var binding in profile.Bindings.Where(b => !IsPublicTask(registry, b.Task)))
                errors.Add($"profile '{profile.Id}': binding task '{binding.Task}' is not a registered public method task");
            foreach (var setup in profile.AllSetups().Where(s => s.UnlocksTask != null && !IsPublicTask(registry, s.UnlocksTask)))
                errors.Add($"profile '{profile.Id}': setup '{setup.Id}' unlocksTask '{setup.UnlocksTask}' is not a registered public method task");
            foreach (var tier in (profile.Tiers ?? []).Where(t => !IsPublicTask(registry, t)))
                errors.Add($"profile '{profile.Id}': tier '{tier}' is not a registered public method task");
        }
        return errors;
    }

    internal static bool IsPublicTask(TaskRegistry registry, string? name) =>
        name != null && registry.GetByName(name) is MethodTask { IsHidden: false };

    private static void CheckHeader(GearProfile profile, List<string> errors)
    {
        if (profile.Id == null || !ProfileIdPattern().IsMatch(profile.Id))
            errors.Add("id must be lower case letters, digits and dashes");
        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add("name is required");
        if (profile.Sources == null || profile.Sources.Count == 0)
            errors.Add("sources must list at least one https url");
        CheckSources("sources", profile.Sources, errors);
        if (profile.Model == null || GearModelKinds.Normalize(profile.Model.Kind) == null)
            errors.Add($"model.kind must be one of {string.Join(", ", GearModelKinds.All)}");
        if (profile.ModelConfidence != null && GearSetupEstimator.ParseConfidence(profile.ModelConfidence) == null)
            errors.Add("modelConfidence must be high, medium or low");
        if (profile.StatCaps?.Values.Any(v => !double.IsFinite(v) || v < 0) == true)
            errors.Add("statCaps values must be numbers >= 0");
        if (profile.MaxMultiplier is { } max && !(max >= GearSetupEstimator.MaxMultiplier && max <= GearSetupEstimator.MaxProfileMultiplier))
            errors.Add($"maxMultiplier must be between {GearSetupEstimator.MaxMultiplier} and {GearSetupEstimator.MaxProfileMultiplier}");
        if (profile.MaxMultiplier != null && GearModelKinds.Normalize(profile.Model?.Kind) == GearModelKinds.TierUnlock)
            errors.Add("maxMultiplier is not used by kind tierUnlock");
    }

    /// <summary>tierUnlock: an ordered task ladder containing every bound and unlocked task, upgrades climbing it.</summary>
    private static void CheckTierLadder(GearProfile profile, List<string> errors)
    {
        var tiers = profile.Tiers;
        if (GearModelKinds.Normalize(profile.Model?.Kind) != GearModelKinds.TierUnlock)
        {
            if (tiers != null)
                errors.Add("tiers is only used by model kind tierUnlock");
            return;
        }
        if (tiers == null || tiers.Count < 2 || tiers.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("tiers (at least two task names, lowest tier first) is required for kind tierUnlock");
            return;
        }
        foreach (var duplicate in tiers.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"tier '{duplicate.Key}' is listed more than once");
        if (profile.Baseline?.UnlocksTask != null)
            errors.Add("the baseline must not have unlocksTask: it is always the bound task's own tier");
        CheckUpgradeTiers(profile, tiers, errors);
        foreach (var binding in (profile.Bindings ?? []).Where(b => b != null && GearSetupEstimator.TierIndex(tiers, b.Task) < 0))
            errors.Add($"binding task '{binding.Task}' is not in tiers");
    }

    private static void CheckUpgradeTiers(GearProfile profile, List<string> tiers, List<string> errors)
    {
        var upgrades = (profile.Setups ?? []).Where(s => s != null).OrderBy(s => GearTiers.Rank(s.Tier)).ToList();
        var previous = -1;
        foreach (var setup in upgrades)
        {
            var index = GearSetupEstimator.TierIndex(tiers, setup.UnlocksTask);
            if (setup.UnlocksTask == null)
                errors.Add($"setup '{setup.Id}' needs unlocksTask (the tier it is gear for) for kind tierUnlock");
            else if (index < 0)
                errors.Add($"setup '{setup.Id}' unlocksTask '{setup.UnlocksTask}' is not in tiers");
            else if (index <= previous)
                errors.Add($"setup '{setup.Id}' must unlock a higher tier than the cheaper setups");
            previous = Math.Max(previous, index);
        }
    }

    /// <summary>Keys differing only in case would throw when merged case-insensitively (stats, params, overrides).</summary>
    private static List<string> CaseDuplicateKeys(GearProfile profile)
    {
        var errors = new List<string>();
        Duplicates("model", profile.Model?.Params?.Keys, errors);
        Duplicates("statCaps", profile.StatCaps?.Keys, errors);
        foreach (var setup in profile.AllSetups())
        {
            Duplicates($"setup '{setup.Id}' stats", setup.Stats?.Keys, errors);
            foreach (var item in (setup.Items ?? []).Where(i => i != null))
                Duplicates($"setup '{setup.Id}' item '{item.ItemTag}' priceFilters", item.PriceFilters?.Keys, errors);
        }
        foreach (var binding in (profile.Bindings ?? []).Where(b => b != null))
            BindingDuplicates($"binding '{binding.Task}'", binding, errors);
        return errors;
    }

    private static void BindingDuplicates(string where, GearBinding binding, List<string> errors)
    {
        Duplicates($"{where} modelOverrides", binding.ModelOverrides?.Keys, errors);
        Duplicates($"{where} statOverrides", binding.StatOverrides?.Keys, errors);
        foreach (var (setupId, stats) in binding.StatOverrides ?? new())
            Duplicates($"{where} statOverrides '{setupId}'", stats?.Keys, errors);
        Duplicates($"{where} itemOverrides", binding.ItemOverrides?.Keys, errors);
        foreach (var (key, item) in binding.ItemOverrides ?? new())
            Duplicates($"{where} itemOverrides '{key}' priceFilters", item?.PriceFilters?.Keys, errors);
    }

    private static void Duplicates(string where, IEnumerable<string>? keys, List<string> errors)
    {
        foreach (var group in (keys ?? []).GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"{where} has keys that differ only in case: {string.Join(", ", group)}");
    }

    private static void CheckSetups(GearProfile profile, List<string> errors)
    {
        if (profile.Baseline == null)
            errors.Add("baseline is required");
        else if (profile.Baseline.Id != GearTiers.Baseline || profile.Baseline.Tier is not (null or "" or GearTiers.Baseline))
            errors.Add("baseline must have id 'baseline' (and tier 'baseline' or none)");
        CheckTiers(profile.Setups, errors);
        var all = profile.AllSetups().ToList();
        foreach (var duplicate in all.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            errors.Add($"setup id '{duplicate.Key}' is used more than once");
        if (!all.Any(s => s.Id == profile.Anchor))
            errors.Add($"anchor '{profile.Anchor}' is not a setup id");
        var unlockAllowed = GearModelKinds.Normalize(profile.Model?.Kind) == GearModelKinds.TierUnlock;
        foreach (var setup in all)
            CheckSetup(setup, unlockAllowed, errors);
    }

    private static void CheckTiers(List<GearSetup>? setups, List<string> errors)
    {
        var tiers = (setups ?? []).Where(s => s != null).Select(s => s.Tier).ToList();
        if (tiers.Count != GearTiers.Upgrades.Count || GearTiers.Upgrades.Any(t => tiers.Count(x => x == t) != 1))
            errors.Add($"setups must be exactly one each of {string.Join(", ", GearTiers.Upgrades)} (got: {string.Join(", ", tiers)})");
    }

    private static void CheckSetup(GearSetup setup, bool unlockAllowed, List<string> errors)
    {
        var where = $"setup '{setup.Id}'";
        if (string.IsNullOrWhiteSpace(setup.Id))
            errors.Add("every setup needs an id");
        if (string.IsNullOrWhiteSpace(setup.Name))
            errors.Add($"{where} needs a name");
        if (setup.Items == null)
            errors.Add($"{where} needs an items list");
        foreach (var item in setup.Items ?? [])
            CheckItem(where, item, errors);
        CheckUniqueSlots(where, setup.Items, errors);
        if (setup.Stats?.Values.Any(v => !double.IsFinite(v)) == true)
            errors.Add($"{where} has a non finite stat value");
        if (setup.RunSeconds is <= 0)
            errors.Add($"{where} runSeconds must be > 0 when set");
        if (setup.UnlocksTask != null && !unlockAllowed)
            errors.Add($"{where} unlocksTask is only allowed for model kind tierUnlock");
        CheckSources($"{where} sources", setup.Sources, errors);
    }

    private static void CheckItem(string where, GearItem? item, List<string> errors)
    {
        if (item == null)
        {
            errors.Add($"{where} has an empty item");
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Slot) || item.Slot.Contains(':'))
            errors.Add($"{where} item '{item.ItemTag}' needs a slot without ':'");
        if (!IsItemTag(item.ItemTag))
            errors.Add($"{where} item tag '{item.ItemTag}' is not an item tag");
        if (string.IsNullOrWhiteSpace(item.Name))
            errors.Add($"{where} item '{item.ItemTag}' needs a name");
        if (string.IsNullOrWhiteSpace(item.Reason))
            errors.Add($"{where} item '{item.ItemTag}' needs a reason");
    }

    private static void CheckUniqueSlots(string where, List<GearItem>? items, List<string> errors)
    {
        var slots = (items ?? []).Where(i => i?.Slot != null).GroupBy(i => i.Slot, StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in slots.Where(g => g.Count() > 1))
            errors.Add($"{where} uses slot '{duplicate.Key}' more than once");
    }

    private static void CheckSources(string where, List<string>? sources, List<string> errors)
    {
        foreach (var source in (sources ?? []).Where(s => !IsHttpsUrl(s)))
            errors.Add($"{where}: '{source}' is not an https url");
    }

    private static void CheckBindings(GearProfile profile, List<string> errors, bool resolve)
    {
        if (profile.Bindings == null || profile.Bindings.Count == 0)
        {
            errors.Add("bindings must bind at least one task");
            return;
        }
        foreach (var duplicate in profile.Bindings.GroupBy(b => b?.Task, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"task '{duplicate.Key}' is bound more than once");
        if (!resolve || profile.Baseline == null || profile.Setups == null || profile.Model == null)
            return; // reported above, resolving would only repeat it (or throw)
        foreach (var binding in profile.Bindings)
            CheckBinding(profile, binding, errors);
    }

    private static void CheckBinding(GearProfile profile, GearBinding? binding, List<string> errors)
    {
        if (binding == null || string.IsNullOrWhiteSpace(binding.Task))
        {
            errors.Add("every binding needs a task name");
            return;
        }
        var where = $"binding '{binding.Task}'";
        var setupIds = profile.AllSetups().Select(s => s.Id).ToHashSet();
        CheckOverrideKeys(where, binding, setupIds, errors);
        foreach (var tag in (binding.RareDropTags ?? []).Where(t => !IsItemTag(t)))
            errors.Add($"{where} rareDropTag '{tag}' is not an item tag");
        var resolved = GearBindingResolver.Resolve(profile, binding);
        foreach (var setup in resolved.AllSetups())
            CheckUniqueSlots($"{where} setup '{setup.Id}'", setup.Items, errors);
        var modelErrors = GearRateModels.Validate(resolved.Model, resolved.AllSetups().ToList());
        foreach (var error in modelErrors)
            errors.Add($"{where}: {error}");
        if (modelErrors.Count == 0)
            CheckAnchorFactors(where, resolved, errors);
    }

    private static bool Positive(double value) => value > 0 && double.IsFinite(value);

    /// <summary>Every setup is divided by the anchor's factors, so a zero there would silently neutralize the model.</summary>
    private static void CheckAnchorFactors(string where, ResolvedGearProfile resolved, List<string> errors)
    {
        if (resolved.Anchor == null)
            return;
        var factors = GearSetupEstimator.FactorsOf(GearRateModels.Create(resolved.Model), resolved.Profile, resolved.Anchor);
        if (!Positive(factors.Throughput) || !Positive(factors.Bulk) || !Positive(factors.Rare))
            errors.Add($"{where}: the anchor setup '{resolved.Anchor.Id}' must give positive rate factors (got {factors})");
    }

    private static void CheckOverrideKeys(string where, GearBinding binding, HashSet<string> setupIds, List<string> errors)
    {
        foreach (var key in (binding.StatOverrides ?? new()).Keys.Where(k => !setupIds.Contains(k)))
            errors.Add($"{where} statOverrides key '{key}' is not a setup id");
        foreach (var (key, item) in binding.ItemOverrides ?? new())
        {
            var (setupId, slot) = GearBindingResolver.SplitScopedKey(key);
            if (key.Contains(':') && !setupIds.Contains(setupId) || string.IsNullOrWhiteSpace(slot))
                errors.Add($"{where} itemOverrides key '{key}' must be 'slot' or 'setupId:slot'");
            CheckItem($"{where} itemOverrides '{key}'", item == null ? null : item with { Slot = slot }, errors);
        }
    }
}
