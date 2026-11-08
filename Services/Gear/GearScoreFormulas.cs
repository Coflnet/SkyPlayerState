using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// How a category turns gear-only stat totals into one score, see <see cref="GearScoreFormulas"/>.
/// <see cref="WeightedSum"/>: Σ weight × stat total. <see cref="DamageProduct"/> (combat): ln((5 + Damage) × (1 + Strength / 100)
/// × (1 + Crit Damage / 100)), a log scale, so the score distribution and the curve are not dominated by the few huge builds.
/// </summary>
public sealed record GearScoreFormula(string Kind, IReadOnlyDictionary<string, double> Weights)
{
    public const string WeightedSum = "weighted-sum";
    public const string DamageProduct = "log-damage-product";
    /// <summary><see cref="Models.GearScoreFormulaInfo.Kind"/> of a task without a score model.</summary>
    public const string None = "none";

    public bool IsLog => Kind == DamageProduct;

    public double Score(IReadOnlyDictionary<string, double> totals)
        => IsLog ? LogDamage(totals) : Weights.Sum(w => w.Value * totals.GetValueOrDefault(w.Key));

    /// <summary>The score of a build with all stats 0: 0, ln 5 for combat.</summary>
    public double Empty => Score(new Dictionary<string, double>());

    /// <summary>
    /// The score of a build whose underlying stat value is (1 + <paramref name="fraction"/>) times the build of
    /// <paramref name="score"/>: × (1 + fraction) for weighted sums, + ln(1 + fraction) on the log scale.
    /// </summary>
    public double Extend(double score, double fraction)
    {
        if (IsLog)
            return score + Math.Log(1 + fraction);
        return score > 0 ? score * (1 + fraction) : score;
    }

    private static double LogDamage(IReadOnlyDictionary<string, double> totals)
    {
        var damage = Math.Max(0, 5 + totals.GetValueOrDefault("Damage"));
        var strength = Math.Max(0, 1 + totals.GetValueOrDefault("Strength") / 100);
        var critDamage = Math.Max(0, 1 + totals.GetValueOrDefault("Crit Damage") / 100);
        return Math.Log(Math.Max(1e-9, damage * strength * critDamage));
    }
}

/// <summary>
/// The score formula per gear category (the starting weights of the 2026-11-08 design, main stat weight 1; TUNABLE after
/// the data review, this is the one place to change them; special effects come from <see cref="GearEffectTable"/>). Stat names as the lore shows them (see
/// <see cref="GearModifierTable"/>):
/// <list type="bullet">
/// <item>farming: Farming Fortune 1 + the task crop's fortune 1 (<see cref="FarmingCrops"/>);</item>
/// <item>mining-ore: Mining Fortune 1 + Mining Speed 0.1 + Mining Spread 0.5 (Breaking Power is a gate, ignored);</item>
/// <item>mining-gem: Gemstone Fortune 1 + Mining Speed 0.1 + Pristine 20 + Mining Spread 0.5;</item>
/// <item>fishing: Sea Creature Chance 10 + Fishing Speed 1 + Double Hook Chance 5, plus Trophy Chance 1 for the trophy
/// fish tasks (<see cref="TrophyTasks"/>);</item>
/// <item>foraging: Foraging Fortune 1 + Sweep 5;</item>
/// <item>combat (dungeon, kuudra, and unbound tasks that are fought, <see cref="GearCategories.IsCombatTask"/>): the
/// <see cref="OptimalTool"/> damage proxy extended to the whole build, on a log scale (<see cref="GearScoreFormula.DamageProduct"/>).</item>
/// </list>
/// Unbound tasks that are not fought have no formula (null): their gear has no known rate relevant stat.
/// </summary>
public static class GearScoreFormulas
{
    /// <summary>
    /// Lava fishing tasks whose drops include the trophy fish (the Crimson Fishing evidence set lists every trophy fish
    /// tier, see FishingTasks); only these weigh Trophy Chance.
    /// </summary>
    public static readonly IReadOnlySet<string> TrophyTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Crimson Fishing" };

    private static readonly GearScoreFormula Combat = new(GearScoreFormula.DamageProduct,
        Weights(("Damage", 1), ("Strength", 1), ("Crit Damage", 1)));

    public static GearScoreFormula? For(string? category, string? task) => category switch
    {
        GearModifierTable.Farming => Farming(task),
        GearModifierTable.MiningOre => Sum(("Mining Fortune", 1), ("Mining Speed", 0.1), ("Mining Spread", 0.5)),
        GearModifierTable.MiningGem => Sum(("Gemstone Fortune", 1), ("Mining Speed", 0.1), ("Pristine", 20), ("Mining Spread", 0.5)),
        GearModifierTable.FishingWater => Fishing(false),
        GearModifierTable.FishingLava => Fishing(task != null && TrophyTasks.Contains(task)),
        GearModifierTable.Foraging => Sum(("Foraging Fortune", 1), ("Sweep", 5)),
        GearModifierTable.Dungeon or GearModifierTable.Kuudra => Combat,
        _ => GearCategories.IsCombatTask(task) ? Combat : null
    };

    private static GearScoreFormula Farming(string? task)
    {
        var crop = FarmingCrops.ForTask(task);
        return crop == null ? Sum(("Farming Fortune", 1)) : Sum(("Farming Fortune", 1), (crop.FortuneStat, 1));
    }

    private static GearScoreFormula Fishing(bool trophy)
    {
        var weights = new List<(string, double)> { ("Sea Creature Chance", 10), ("Fishing Speed", 1), ("Double Hook Chance", 5) };
        if (trophy)
            weights.Add(("Trophy Chance", 1));
        return Sum([.. weights]);
    }

    private static GearScoreFormula Sum(params (string Stat, double Weight)[] weights)
        => new(GearScoreFormula.WeightedSum, Weights(weights));

    private static IReadOnlyDictionary<string, double> Weights(params (string Stat, double Weight)[] weights)
        => weights.ToDictionary(w => w.Stat, w => w.Weight, StringComparer.Ordinal);
}
