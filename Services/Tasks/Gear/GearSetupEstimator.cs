using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

public enum GearConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>Values of <see cref="GearSetupEstimate.Status"/>.</summary>
public static class GearSetupStatus
{
    /// <summary>The setup everything is compared against.</summary>
    public const string Baseline = "baseline";
    /// <summary>Gear that should earn more than the baseline on this task.</summary>
    public const string Upgrade = "upgrade";
    /// <summary>tierUnlock only: gear typically used for this tier or a lower one, so it is no upgrade here (no Δ).</summary>
    public const string Reached = "reached";
}

/// <summary>Everything <see cref="GearSetupEstimator.Estimate"/> needs, gathered by <see cref="GearSetupService"/>.</summary>
/// <param name="TaskName">Canonical task name (<see cref="TaskEstimator.GetTaskName"/>), echoed in the response.</param>
/// <param name="Profile">The profile with the task's binding applied.</param>
/// <param name="Community">The task's community estimate, null when there is none.</param>
/// <param name="Shares">Net revenue split of the task, see <see cref="RevenueShares.FromAggregate"/>.</param>
/// <param name="EstimateOf">Community estimate of another task by name (tierUnlock tiers), null when unknown.</param>
/// <param name="Prices">Clean prices by tag for the item price hints.</param>
public record GearEstimateInput(string TaskName, ResolvedGearProfile Profile, TaskEstimate? Community, RevenueShares Shares,
    Func<string, TaskEstimate?> EstimateOf, IReadOnlyDictionary<string, double>? Prices);

/// <summary>
/// Pure gear setup math. Scaled models: with C the community coins/hour, assumed to be earned with the anchor
/// setup a, M_s = share_bulk·Bulk_s/Bulk_a + share_rare·Rare_s/Rare_a + share_thr·Thr_s/Thr_a over net shares
/// (costs included, see <see cref="RevenueShares"/>), clamped to [<see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>];
/// rate_s = C·M_s. tierUnlock: the baseline earns the bound task's rate, a setup the rate of its unlocksTask when that
/// tier is above the bound one, else it is <see cref="GearSetupStatus.Reached"/>. Δ_s = rate_s − rate_baseline.
/// </summary>
public static class GearSetupEstimator
{
    public const double MinMultiplier = 0.2;
    public const double MaxMultiplier = 5;
    /// <summary>Highest <see cref="GearProfile.MaxMultiplier"/> a profile may set.</summary>
    public const double MaxProfileMultiplier = 20;
    internal const int MinContributorsForHigh = 3;

    /// <summary>One setup's numbers before they are put into the response shape.</summary>
    private sealed record SetupRate(double? Rate, double? Multiplier, GearConfidence Confidence, List<string> Assumptions, string Status);

    private sealed record ScaledContext(GearEstimateInput Input, IGearRateModel Model, GearSetup Anchor,
        RateFactors AnchorFactors, double CommunityRate);

    public static TaskGearSetups Estimate(GearEstimateInput input)
    {
        var profile = input.Profile;
        var anchor = profile.Anchor ?? profile.Baseline;
        Func<GearSetup, bool, SetupRate> rateOf = GearModelKinds.Normalize(profile.Model.Kind) == GearModelKinds.TierUnlock
            ? (setup, isBaseline) => TierRate(input, setup, isBaseline)
            : ScaledRates(input, anchor);

        var baselineRate = rateOf(profile.Baseline, true);
        var baseline = ToEstimate(input, TierBaseline(input, profile), baselineRate, baselineRate.Rate);
        var setups = profile.Setups.OrderBy(s => GearTiers.Rank(s.Tier))
            .Select(s => ToEstimate(input, s, rateOf(s, false), baselineRate.Rate))
            .ToList();
        return new TaskGearSetups
        {
            TaskName = input.TaskName,
            ProfileId = profile.Profile.Id,
            ProfileName = profile.Profile.Name,
            Anchor = anchor.Id,
            ResearchedAt = profile.Profile.ResearchedAt,
            CommunityCoinsPerHour = Finite(input.Community?.CoinsPerHour) ?? 0,
            Source = input.Community?.Source ?? "none",
            Contributors = input.Community?.Contributors ?? 0,
            BaselineCoinsPerHour = baseline.CoinsPerHour,
            Baseline = baseline,
            Setups = setups
        };
    }

    internal const string UnknownGearBaselineName = "Community rate (gear varies)";

    /// <summary>
    /// tierUnlock only: on a tier at or above the first one a setup unlocks, the starter kit the baseline lists cannot do
    /// the tier, so the baseline is the plain community rate without a gear claim (it was "Adaptive armor + AOTE" at M7).
    /// </summary>
    internal static GearSetup TierBaseline(GearEstimateInput input, ResolvedGearProfile profile)
    {
        if (GearModelKinds.Normalize(profile.Model.Kind) != GearModelKinds.TierUnlock)
            return profile.Baseline;
        var tiers = profile.Profile.Tiers ?? [];
        var firstUnlock = profile.Setups.Select(s => TierIndex(tiers, s.UnlocksTask)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();
        if (TierIndex(tiers, input.TaskName) < firstUnlock)
            return profile.Baseline;
        return profile.Baseline with
        {
            Name = UnknownGearBaselineName,
            Items = [],
            Assumptions = [.. profile.Baseline.Assumptions ?? [], "The gear this tier is done with varies, the listed upgrades are what it is usually unlocked with."]
        };
    }

    /// <summary>The share weighted, anchor relative multiplier before clamping.</summary>
    public static double RawMultiplier(RevenueShares shares, RateFactors setup, RateFactors anchor) =>
        shares.Bulk * Ratio(setup.Bulk, anchor.Bulk)
        + shares.Rare * Ratio(setup.Rare, anchor.Rare)
        + shares.Throughput * Ratio(setup.Throughput, anchor.Throughput);

    private static double Ratio(double value, double anchor) => anchor > 0 ? value / anchor : 1;

    /// <summary>The model's factors for <paramref name="setup"/> with the profile's statCaps applied first.</summary>
    internal static RateFactors FactorsOf(IGearRateModel model, GearProfile profile, GearSetup setup) =>
        model.Factors(CapStats(setup.Stats, profile.StatCaps), setup);

    internal static Dictionary<string, double> CapStats(Dictionary<string, double>? stats, Dictionary<string, double>? caps)
    {
        var result = new Dictionary<string, double>(stats ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (stat, cap) in caps ?? new())
            if (result.TryGetValue(stat, out var value) && value > cap)
                result[stat] = cap;
        return result;
    }

    private static GearSetupEstimate ToEstimate(GearEstimateInput input, GearSetup setup, SetupRate rate, double? baselineRate)
    {
        var isUpgrade = rate.Status == GearSetupStatus.Upgrade;
        return new GearSetupEstimate
        {
            Id = setup.Id,
            Name = setup.Name,
            Tier = string.IsNullOrEmpty(setup.Tier) ? setup.Id : setup.Tier,
            Status = rate.Status,
            Items = setup.Items.Select(i => ToItem(i, input.Prices)).ToList(),
            Stats = (setup.Stats ?? new()).Where(s => double.IsFinite(s.Value)).ToDictionary(s => s.Key, s => s.Value),
            Multiplier = Finite(rate.Multiplier),
            CoinsPerHour = Finite(rate.Rate),
            DeltaCoinsPerHour = rate.Status == GearSetupStatus.Baseline
                ? (Finite(rate.Rate).HasValue ? 0 : null)
                : isUpgrade ? Finite(rate.Rate - baselineRate) : null,
            Confidence = rate.Confidence.ToString().ToLowerInvariant(),
            Assumptions = rate.Assumptions.Distinct().ToList(),
            Sources = (input.Profile.Profile.Sources ?? []).Concat(setup.Sources ?? []).Distinct().ToList(),
            UnlocksTask = setup.UnlocksTask
        };
    }

    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;

    // ── scaled models (fortune, mining, fishing, runTime, fixed) ──

    private static Func<GearSetup, bool, SetupRate> ScaledRates(GearEstimateInput input, GearSetup anchor)
    {
        var model = GearRateModels.Create(input.Profile.Model);
        var context = new ScaledContext(input, model, anchor, FactorsOf(model, input.Profile.Profile, anchor),
            Finite(input.Community?.CoinsPerHour) ?? 0);
        return (setup, isBaseline) => ScaledRate(context, setup, isBaseline ? GearSetupStatus.Baseline : GearSetupStatus.Upgrade);
    }

    private static SetupRate ScaledRate(ScaledContext context, GearSetup setup, string status)
    {
        var assumptions = new List<string>(setup.Assumptions ?? []);
        var raw = RawMultiplier(context.Input.Shares, FactorsOf(context.Model, context.Input.Profile.Profile, setup), context.AnchorFactors);
        var blocker = ScaledBlocker(context, raw);
        if (blocker != null)
        {
            assumptions.Add(blocker);
            return new SetupRate(null, null, GearConfidence.Low, assumptions, status);
        }
        var upper = context.Input.Profile.Profile.MaxMultiplier ?? MaxMultiplier;
        var multiplier = Math.Clamp(raw, MinMultiplier, upper);
        var clamped = Math.Abs(multiplier - raw) > 1e-9;
        assumptions.Add($"The community rate is assumed to be earned with the '{context.Anchor.Name}' setup; the others are scaled from it.");
        AddScaledAssumptions(context, clamped, assumptions);
        var confidence = clamped ? GearConfidence.Low
            : Min(KindConfidence(context.Model.Kind, context.Input.Profile), DataConfidence(context.Input.Community));
        if (upper > MaxMultiplier)
            confidence = Min(confidence, GearConfidence.Medium);
        return new SetupRate(context.CommunityRate * multiplier, multiplier, CapModel(confidence, context.Input.Profile), assumptions, status);
    }

    /// <summary>Why no rate can be given, null when one can.</summary>
    private static string? ScaledBlocker(ScaledContext context, double raw)
    {
        if (context.CommunityRate <= 0)
            return "There is no community coins/hour estimate for this task yet.";
        if (!context.Input.Shares.NetPositive)
            return "This task currently nets no coins on average, so setups can't be compared on it.";
        if (!double.IsFinite(raw))
            return "The rate model gave no usable result for this setup.";
        return null;
    }

    private static void AddScaledAssumptions(ScaledContext context, bool clamped, List<string> assumptions)
    {
        if (!context.Input.Shares.Itemized)
            assumptions.Add("No drop breakdown is tracked for this task yet, so all revenue is assumed to scale with drop amount.");
        if (context.Model.Kind == GearModelKinds.Fixed)
            assumptions.Add("The multiplier is a fixed research estimate, not derived from stats.");
        if (clamped)
            assumptions.Add("The model result was clamped to a plausible range; treat this estimate as rough.");
        if ((context.Input.Profile.Profile.MaxMultiplier ?? MaxMultiplier) > MaxMultiplier)
            assumptions.Add("Endgame gear has a very large effect on this task, so the gaps between setups are especially uncertain.");
    }

    // ── tierUnlock ──

    private static SetupRate TierRate(GearEstimateInput input, GearSetup setup, bool isBaseline)
    {
        var assumptions = new List<string>(setup.Assumptions ?? []);
        if (isBaseline)
            return BoundTierRate(input, assumptions);
        var tiers = input.Profile.Profile.Tiers ?? [];
        var boundIndex = TierIndex(tiers, input.TaskName);
        var unlockIndex = TierIndex(tiers, setup.UnlocksTask);
        if (unlockIndex <= boundIndex)
        {
            assumptions.Add($"This is gear typically used for '{setup.UnlocksTask}', which is not above '{input.TaskName}'.");
            return new SetupRate(null, null, CapModel(GearConfidence.High, input.Profile), assumptions, GearSetupStatus.Reached);
        }
        return UnlockedTierRate(input, setup, assumptions);
    }

    private static SetupRate BoundTierRate(GearEstimateInput input, List<string> assumptions)
    {
        var rate = Finite(input.Community?.CoinsPerHour);
        if (rate is not > 0)
        {
            assumptions.Add("There is no community coins/hour estimate for this task yet.");
            return new SetupRate(null, null, GearConfidence.Low, assumptions, GearSetupStatus.Baseline);
        }
        assumptions.Add($"The baseline earns the community rate of this tier, '{input.TaskName}', whatever gear it takes.");
        var confidence = CapModel(DataConfidence(input.Community), input.Profile);
        return new SetupRate(rate, 1, confidence, assumptions, GearSetupStatus.Baseline);
    }

    private static SetupRate UnlockedTierRate(GearEstimateInput input, GearSetup setup, List<string> assumptions)
    {
        var unlocked = input.EstimateOf(setup.UnlocksTask!);
        if (Finite(unlocked?.CoinsPerHour) is not > 0)
        {
            assumptions.Add($"There is no community estimate for '{setup.UnlocksTask}' yet.");
            return new SetupRate(null, null, GearConfidence.Low, assumptions, GearSetupStatus.Upgrade);
        }
        var bound = Finite(input.Community?.CoinsPerHour);
        var (rate, bestTier) = BestReachableRate(input, setup.UnlocksTask!, unlocked!);
        if (bound > 0 && rate <= bound)
        {
            assumptions.Add($"Every tier this gear unlocks earns no more than '{input.TaskName}' does, so it is no upgrade here.");
            return new SetupRate(null, null, CapModel(GearConfidence.High, input.Profile), assumptions, GearSetupStatus.Reached);
        }
        assumptions.Add(bestTier == setup.UnlocksTask
            ? $"This setup is assumed to make '{setup.UnlocksTask}' doable; its rate is that task's community rate."
            : $"This setup makes '{setup.UnlocksTask}' doable, but '{bestTier}' earns more and is reachable with it, so that rate is used.");
        var confidence = Min(DataConfidence(unlocked), DataConfidence(input.Community));
        return new SetupRate(rate, bound > 0 ? rate / bound : null, CapModel(confidence, input.Profile), assumptions, GearSetupStatus.Upgrade);
    }

    /// <summary>
    /// The best community rate among the tiers above the bound one up to and including <paramref name="unlockTask"/>:
    /// gear that unlocks a floor also does the floors below it, so it never earns less than the best of them.
    /// </summary>
    private static (double Rate, string Tier) BestReachableRate(GearEstimateInput input, string unlockTask, TaskEstimate unlocked)
    {
        var tiers = input.Profile.Profile.Tiers ?? [];
        var best = (Rate: unlocked.CoinsPerHour, Tier: unlockTask);
        var from = TierIndex(tiers, input.TaskName) + 1;
        for (var i = Math.Max(from, 0); i < TierIndex(tiers, unlockTask); i++)
        {
            var rate = Finite(input.EstimateOf(tiers[i])?.CoinsPerHour);
            if (rate > best.Rate)
                best = (rate.Value, tiers[i]);
        }
        return best;
    }

    /// <summary>Position of <paramref name="task"/> in the profile's tier ladder, -1 when absent.</summary>
    internal static int TierIndex(IReadOnlyList<string> tiers, string? task)
    {
        for (var i = 0; i < tiers.Count; i++)
            if (string.Equals(tiers[i], task, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // ── confidence ──

    internal static GearConfidence DataConfidence(TaskEstimate? estimate)
    {
        if (estimate == null || !(estimate.CoinsPerHour > 0) || estimate.Source is null or "formula")
            return GearConfidence.Low;
        if (estimate.Contributors >= MinContributorsForHigh)
            return GearConfidence.High;
        return estimate.Contributors >= 1 ? GearConfidence.Medium : GearConfidence.Low;
    }

    /// <summary>Fortune, mining and fishing with a formulaSource are high, without medium; runTime medium, fixed low.</summary>
    private static GearConfidence KindConfidence(string kind, ResolvedGearProfile profile) => kind switch
    {
        GearModelKinds.Fortune or GearModelKinds.Mining or GearModelKinds.Fishing =>
            profile.Model.Has("formulaSource") ? GearConfidence.High : GearConfidence.Medium,
        GearModelKinds.RunTime => GearConfidence.Medium,
        GearModelKinds.TierUnlock => GearConfidence.High,
        _ => GearConfidence.Low
    };

    /// <summary>Lowers <paramref name="confidence"/> to the profile's modelConfidence when that is set.</summary>
    private static GearConfidence CapModel(GearConfidence confidence, ResolvedGearProfile profile) =>
        ParseConfidence(profile.Profile.ModelConfidence) is { } cap ? Min(confidence, cap) : confidence;

    internal static GearConfidence? ParseConfidence(string? value) =>
        value != null && value.All(char.IsLetter) && Enum.TryParse<GearConfidence>(value, true, out var parsed)
            ? parsed : null;

    private static GearConfidence Min(GearConfidence a, GearConfidence b) => a < b ? a : b;

    private static GearSetupItem ToItem(GearItem item, IReadOnlyDictionary<string, double>? prices)
    {
        var price = prices?.GetValueOrDefault(item.ItemTag) ?? 0;
        var filtered = item.PriceFilters is { Count: > 0 };
        return new GearSetupItem
        {
            Slot = item.Slot,
            ItemTag = item.ItemTag,
            Name = item.Name,
            Reason = item.Reason,
            EstimatedPrice = price > 0 && double.IsFinite(price) && !filtered && item.Tradeable ? price : null,
            Tradeable = item.Tradeable,
            PriceFilters = item.PriceFilters == null ? null : new Dictionary<string, string>(item.PriceFilters)
        };
    }
}
