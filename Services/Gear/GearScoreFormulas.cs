using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// How a category turns gear-only stat totals into one score, see <see cref="GearScoreFormulas"/>.
/// <see cref="WeightedSum"/>: Σ weight × stat total. <see cref="DamageProduct"/> (combat): ln((5 + Damage) × (1 + Strength / 100)
/// × (1 + Crit Damage / 100)), a log scale, so the score distribution and the curve are not dominated by the few huge builds.
/// <see cref="MageProduct"/>: ln(<see cref="MageBase"/> × <see cref="MageIntelligenceScaling"/> × Intelligence / 100
/// × (1 + Ability Damage / 100)), the Intelligence part of the ability damage formula (wiki Damage_Calculation, Wither
/// Impact scaling) without its "1 +": continuous from 0, so a little Intelligence from reforges scores far below any
/// melee build (Intelligence 50 gives ln 150 = 5.0) instead of a floor. <see cref="DamageOrMage"/> (dungeon, kuudra): the
/// higher of the two, so mage builds (Intelligence and Ability Damage only, e.g. Wise Wither armor and Wither Goggles) no
/// longer score 0; the mage branch only applies with Intelligence above 0 (else it is ln 0), the empty build scores the
/// melee value.
/// </summary>
public sealed record GearScoreFormula(string Kind, IReadOnlyDictionary<string, double> Weights)
{
    public const string WeightedSum = "weighted-sum";
    public const string DamageProduct = "log-damage-product";
    public const string MageProduct = "log-mage-product";
    public const string DamageOrMage = "log-max-damage-mage";
    /// <summary><see cref="Models.GearScoreFormulaInfo.Kind"/> of a task without a score model.</summary>
    public const string None = "none";
    /// <summary>
    /// The ability's base damage stand-in, TUNABLE: chosen so typical endgame mage and melee gear land in the same range
    /// (melee: Damage 330, Strength 600, Crit Damage 400 give ln 11 700 = 9.37; mage: Intelligence 2 100, Ability Damage 50
    /// give ln(1000 × 6.3 × 1.5) = ln 9450 = 9.15, 20 % below, close enough on this log scale). Weak builds keep their
    /// gradient: Intelligence 50 from reforges gives ln 150 = 5.0, below a weak melee build (Damage 150, Strength 100, Crit
    /// Damage 50: 6.14).
    /// </summary>
    public const double MageBase = 1000;
    /// <summary>Intelligence scaling of Wither Impact (Hyperion family), the most used dungeon mage ability.</summary>
    public const double MageIntelligenceScaling = 0.3;

    private static readonly GearScoreFormula Melee = new(DamageProduct,
        new Dictionary<string, double> { ["Damage"] = 1, ["Strength"] = 1, ["Crit Damage"] = 1 });
    private static readonly GearScoreFormula Mage = new(MageProduct,
        new Dictionary<string, double> { ["Intelligence"] = 1, ["Ability Damage"] = 1 });

    public bool IsLog => Kind is DamageProduct or MageProduct or DamageOrMage;

    /// <summary>The formulas the score is the highest of: melee and mage for <see cref="DamageOrMage"/>, else itself.</summary>
    public IReadOnlyList<GearScoreFormula> Branches => Kind == DamageOrMage ? [Melee, Mage] : [this];

    public double Score(IReadOnlyDictionary<string, double> totals) => Kind switch
    {
        DamageProduct => LogDamage(totals),
        MageProduct => LogMage(totals),
        DamageOrMage => totals.GetValueOrDefault("Intelligence") > 0 ? Math.Max(LogDamage(totals), LogMage(totals)) : LogDamage(totals),
        _ => Weights.Sum(w => w.Value * totals.GetValueOrDefault(w.Key))
    };

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

    /// <summary>ln 0 without Intelligence, floored at ln 1e-9 so it stays finite.</summary>
    private static double LogMage(IReadOnlyDictionary<string, double> totals)
    {
        var intelligence = Math.Max(0, MageIntelligenceScaling * totals.GetValueOrDefault("Intelligence") / 100);
        var abilityDamage = Math.Max(0, 1 + totals.GetValueOrDefault("Ability Damage") / 100);
        return Math.Log(Math.Max(1e-9, MageBase * intelligence * abilityDamage));
    }
}

/// <summary>
/// The score formula per gear category (the starting weights of the 2026-11-08 design, main stat weight 1; TUNABLE after
/// the data review, this is the one place to change them; special effects come from <see cref="GearEffectTable"/>). Stat names as the lore shows them (see
/// <see cref="GearModifierTable"/>):
/// <list type="bullet">
/// <item>farming: Farming Fortune 1 + the task crop's fortune 1 (<see cref="FarmingCrops"/>);</item>
/// <item>mining-ore: Mining Fortune 1 + Mining Speed 0.1 + Mining Spread 0.5 + the task block's fortune 1 (Ore, Dwarven Metal or
/// Block Fortune, <see cref="MiningBlocks"/>; they add to Mining Fortune only on their blocks) (Breaking Power is a gate, ignored);</item>
/// <item>mining-gem: Gemstone Fortune 1 + Mining Speed 0.1 + Pristine 20 + Mining Spread 0.5;</item>
/// <item>fishing: Sea Creature Chance 10 + Fishing Speed 1 + Double Hook Chance 5, plus Trophy Chance 1 for the trophy
/// fish tasks (<see cref="TrophyTasks"/>);</item>
/// <item>foraging: Foraging Fortune 1 + Sweep 5;</item>
/// <item>combat (unbound tasks that are fought, <see cref="GearCategories.IsCombatTask"/>): the
/// <see cref="OptimalTool"/> damage proxy extended to the whole build, on a log scale (<see cref="GearScoreFormula.DamageProduct"/>);</item>
/// <item>dungeon and kuudra: the higher of that melee score and the mage score (<see cref="GearScoreFormula.DamageOrMage"/>).</item>
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
    private static readonly GearScoreFormula MeleeOrMage = new(GearScoreFormula.DamageOrMage,
        Weights(("Damage", 1), ("Strength", 1), ("Crit Damage", 1), ("Intelligence", 1), ("Ability Damage", 1)));

    public static GearScoreFormula? For(string? category, string? task) => category switch
    {
        GearModifierTable.Farming => Farming(task),
        GearModifierTable.MiningOre => MiningOre(task),
        GearModifierTable.MiningGem => Sum(("Gemstone Fortune", 1), ("Mining Speed", 0.1), ("Pristine", 20), ("Mining Spread", 0.5)),
        GearModifierTable.FishingWater => Fishing(false),
        GearModifierTable.FishingLava => Fishing(task != null && TrophyTasks.Contains(task)),
        GearModifierTable.Foraging => Sum(("Foraging Fortune", 1), ("Sweep", 5)),
        GearModifierTable.Dungeon or GearModifierTable.Kuudra => MeleeOrMage,
        _ => GearCategories.IsCombatTask(task) ? Combat : null
    };

    private static GearScoreFormula Farming(string? task)
    {
        var crop = FarmingCrops.ForTask(task);
        return crop == null ? Sum(("Farming Fortune", 1)) : Sum(("Farming Fortune", 1), (crop.FortuneStat, 1));
    }

    private static GearScoreFormula MiningOre(string? task)
    {
        var block = MiningBlocks.ForTask(task);
        return block == null
            ? Sum(("Mining Fortune", 1), ("Mining Speed", 0.1), ("Mining Spread", 0.5))
            : Sum(("Mining Fortune", 1), (block.FortuneStat, 1), ("Mining Speed", 0.1), ("Mining Spread", 0.5));
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
