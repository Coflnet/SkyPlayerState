using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// Relative rate components of one setup. Only ratios between setups matter: <see cref="GearSetupEstimator"/>
/// divides every component by the anchor setup's. Throughput scales the residual (unitemized) revenue,
/// Bulk the common drops and Rare the magic-find affected drops.
/// </summary>
public readonly record struct RateFactors(double Throughput, double Bulk, double Rare)
{
    public static RateFactors Neutral => new(1, 1, 1);
}

public interface IGearRateModel
{
    string Kind { get; }
    RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup);
}

public static class GearModelKinds
{
    public const string Fortune = "fortune";
    public const string Mining = "mining";
    public const string Fishing = "fishing";
    public const string RunTime = "runTime";
    public const string TierUnlock = "tierUnlock";
    public const string Fixed = "fixed";

    public static readonly IReadOnlyList<string> All = [Fortune, Mining, Fishing, RunTime, TierUnlock, Fixed];

    /// <summary>The canonical spelling of <paramref name="kind"/>, null when unknown.</summary>
    public static string? Normalize(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Builds and validates the rate model of a profile. Every game constant is a JSON parameter; the
/// numbers here are only defaults a profile can override. Parameter names per kind:
/// <list type="bullet">
/// <item>fortune: fortuneStat (required); fortuneDivisor=100, rareStat, rareDivisor=100, formulaSource</item>
/// <item>mining: speedStat, fortuneStats (list, or fortuneStat), blockStrength (required); breakTickFactor=30, minTicks=4,
/// instamineFactor (none), overheadTicks=0, ticksPerHour=72000, fortuneDivisor=100, spreadStat, spreadDivisor=100,
/// pristineStat, pristineFactor=0.79, chanceStat, chanceDivisor=100, rareChanceStat, rareChanceDivisor=100, rareStat,
/// rareDivisor=100, formulaSource; fortuneStats: [] means no fortune applies</item>
/// <item>fishing: fishingSpeedStat, seaCreatureChanceStat, baseWaitSeconds (required); reelSeconds=0, fishingSpeedCap (none),
/// sccCap=100, killSecondsPerCreature=0, fishingSpeedDivisor=100, seaCreatureChanceDivisor=100, bulkFrom=creatures|catches,
/// rareStat, rareDivisor=100, formulaSource</item>
/// <item>runTime: every setup needs runSeconds; overheadSeconds=0, mfAffected=false, rareStat (required when mfAffected), rareDivisor=100</item>
/// <item>tierUnlock: no parameters; every upgrade setup names its tier with unlocksTask (see GearProfile.Tiers)</item>
/// <item>fixed: multipliers {setupId: number} for the baseline and every setup</item>
/// </list>
/// Parameters a kind does not know are rejected, so a typo or a leftover can't be silently ignored.
/// </summary>
public static class GearRateModels
{
    public static IGearRateModel Create(RateModelSpec spec) => GearModelKinds.Normalize(spec.Kind) switch
    {
        GearModelKinds.Fortune => new FortuneRateModel(spec),
        GearModelKinds.Mining => new MiningRateModel(spec),
        GearModelKinds.Fishing => new FishingRateModel(spec),
        GearModelKinds.RunTime => new RunTimeRateModel(spec),
        GearModelKinds.TierUnlock => new TierUnlockRateModel(),
        GearModelKinds.Fixed => new FixedRateModel(spec),
        _ => throw new ArgumentException($"unknown gear rate model kind '{spec.Kind}'")
    };

    /// <summary>Parameter and per setup errors of <paramref name="spec"/> applied to <paramref name="setups"/>.</summary>
    public static List<string> Validate(RateModelSpec spec, IReadOnlyList<GearSetup> setups)
    {
        var errors = new List<string>();
        var kind = GearModelKinds.Normalize(spec?.Kind);
        if (spec == null || kind == null)
        {
            errors.Add($"model.kind '{spec?.Kind}' must be one of {string.Join(", ", GearModelKinds.All)}");
            return errors;
        }
        var check = new ModelCheck(spec, setups, errors);
        switch (kind)
        {
            case GearModelKinds.Fortune: FortuneRateModel.Validate(check); break;
            case GearModelKinds.Mining: MiningRateModel.Validate(check); break;
            case GearModelKinds.Fishing: FishingRateModel.Validate(check); break;
            case GearModelKinds.RunTime: RunTimeRateModel.Validate(check); break;
            case GearModelKinds.TierUnlock: TierUnlockRateModel.Validate(check); break;
            case GearModelKinds.Fixed: FixedRateModel.Validate(check); break;
        }
        check.ReportUnknownParams();
        return errors;
    }

    /// <summary>1 + stat/divisor, the usual "percent bonus" shape, 1 when no stat is configured.</summary>
    internal static double PercentBonus(IReadOnlyDictionary<string, double> stats, string? stat, double divisor) =>
        stat == null ? 1 : 1 + stats.GetValueOrDefault(stat) / divisor;

    internal static double Stat(IReadOnlyDictionary<string, double> stats, string? stat) =>
        stat == null ? 0 : stats.GetValueOrDefault(stat);
}

/// <summary>Collects validation errors for one model spec against the setups it is applied to.</summary>
internal sealed class ModelCheck(RateModelSpec spec, IReadOnlyList<GearSetup> setups, List<string> errors)
{
    private readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);

    public RateModelSpec Spec => spec;
    public IReadOnlyList<GearSetup> Setups => setups;

    public void Error(string message) => errors.Add(message);

    /// <summary>Marks parameters as belonging to the kind without checking them (the checks below do this themselves).</summary>
    public void Known(params string[] names) => known.UnionWith(names);

    public void ReportUnknownParams()
    {
        foreach (var name in (spec.Params ?? new()).Keys.Where(k => !known.Contains(k)))
            Error($"model.{name} is not a parameter of kind {spec.Kind}");
    }

    /// <summary>A required stat name parameter whose stat every setup must list.</summary>
    public void RequireStat(string param)
    {
        Known(param);
        var stat = spec.GetString(param);
        if (string.IsNullOrWhiteSpace(stat))
        {
            Error($"model.{param} (stat name) is required for kind {spec.Kind}");
            return;
        }
        StatInEverySetup(param, stat);
    }

    /// <summary>An optional stat name parameter; when set, every setup must list the stat (0 is fine).</summary>
    public void OptionalStat(string param)
    {
        Known(param);
        if (!spec.Has(param))
            return;
        var stat = spec.GetString(param);
        if (string.IsNullOrWhiteSpace(stat))
        {
            Error($"model.{param} must be a stat name");
            return;
        }
        StatInEverySetup(param, stat);
    }

    public void RequirePositive(string param)
    {
        Known(param);
        if (!spec.Has(param))
            Error($"model.{param} (number > 0) is required for kind {spec.Kind}");
        else
            OptionalPositive(param);
    }

    public void OptionalPositive(string param) => OptionalNumber(param, v => v > 0, "> 0");

    public void OptionalNonNegative(string param) => OptionalNumber(param, v => v >= 0, ">= 0");

    public void OptionalAtLeast(string param, double min) => OptionalNumber(param, v => v >= min, $">= {min}");

    public void OptionalString(string param, params string[] allowed)
    {
        Known(param);
        if (!spec.Has(param))
            return;
        var value = spec.GetString(param);
        if (value == null || allowed.Length > 0 && !allowed.Contains(value))
            Error($"model.{param} must be {(allowed.Length > 0 ? string.Join(" or ", allowed) : "a string")}");
    }

    public void OptionalHttps(string param)
    {
        Known(param);
        if (!spec.Has(param))
            return;
        if (!GearProfileValidator.IsHttpsUrl(spec.GetString(param)))
            Error($"model.{param} must be an https url");
    }

    private void OptionalNumber(string param, Func<double, bool> valid, string rule)
    {
        Known(param);
        if (!spec.Has(param))
            return;
        var value = spec.GetDouble(param);
        if (value == null || !double.IsFinite(value.Value) || !valid(value.Value))
            Error($"model.{param} must be a number {rule}");
    }

    public void StatInEverySetup(string param, string stat)
    {
        foreach (var setup in setups.Where(s => s.Stats == null || !s.Stats.ContainsKey(stat)))
            Error($"setup '{setup.Id}' must list stat '{stat}' (model.{param})");
    }
}

/// <summary>Shared magic find handling: rare = 1 + MF/rareDivisor when rareStat is set.</summary>
internal static class RareParams
{
    public static void Validate(ModelCheck check)
    {
        check.OptionalStat("rareStat");
        check.OptionalPositive("rareDivisor");
        check.OptionalHttps("formulaSource");
    }

    public static double Factor(RateModelSpec spec, IReadOnlyDictionary<string, double> stats) =>
        GearRateModels.PercentBonus(stats, spec.GetString("rareStat"), spec.GetDouble("rareDivisor", 100));
}

/// <summary>Crop farming / foraging: thr = 1, bulk = 1 + F/100, rare = 1 + MF/100.</summary>
public sealed class FortuneRateModel(RateModelSpec spec) : IGearRateModel
{
    public string Kind => GearModelKinds.Fortune;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup)
    {
        var bulk = GearRateModels.PercentBonus(stats, spec.GetString("fortuneStat"), spec.GetDouble("fortuneDivisor", 100));
        return new RateFactors(1, bulk, RareParams.Factor(spec, stats));
    }

    internal static void Validate(ModelCheck check)
    {
        check.RequireStat("fortuneStat");
        check.OptionalPositive("fortuneDivisor");
        RareParams.Validate(check);
    }
}

/// <summary>
/// Block mining: ticks = max(minTicks, round(breakTickFactor·strength/speed)), or 1 when speed exceeds
/// instamineFactor·strength; BPH = ticksPerHour/(ticks + overheadTicks).
/// bulk = BPH·(1+ΣfortuneStats/100)·(1+spread/100)·pristine·chance, rare = BPH·rareChance·(1+MF/100), throughput = BPH.
/// pristine (gemstones, optional) = 1 + pristineFactor·Pristine (Pristine in stat points);
/// chance (optional, scales bulk) = chanceStat/chanceDivisor;
/// rareChance (optional, e.g. treasure chests: their loot is the rare component) = rareChanceStat/rareChanceDivisor.
/// </summary>
public sealed class MiningRateModel(RateModelSpec spec) : IGearRateModel
{
    public string Kind => GearModelKinds.Mining;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup)
    {
        var blocksPerHour = BlocksPerHour(GearRateModels.Stat(stats, spec.GetString("speedStat")));
        var fortune = 1 + FortuneStats(spec).Sum(s => stats.GetValueOrDefault(s)) / spec.GetDouble("fortuneDivisor", 100);
        var spread = GearRateModels.PercentBonus(stats, spec.GetString("spreadStat"), spec.GetDouble("spreadDivisor", 100));
        var bulk = blocksPerHour * fortune * spread * PristineFactor(stats) * ChanceFactor(stats);
        var rare = blocksPerHour * RareChanceFactor(stats) * RareParams.Factor(spec, stats);
        return new RateFactors(blocksPerHour, bulk, rare);
    }

    private double RareChanceFactor(IReadOnlyDictionary<string, double> stats)
    {
        var stat = spec.GetString("rareChanceStat");
        return stat == null ? 1 : stats.GetValueOrDefault(stat) / spec.GetDouble("rareChanceDivisor", 100);
    }

    /// <summary>fortuneStats (list, summed) plus the single fortuneStat, whichever are set.</summary>
    internal static List<string> FortuneStats(RateModelSpec spec)
    {
        var stats = spec.GetStringList("fortuneStats") ?? [];
        if (spec.GetString("fortuneStat") is { Length: > 0 } single && !stats.Contains(single))
            stats.Add(single);
        return stats;
    }

    /// <summary>Ticks to break one block: 1 when instamining, never below minTicks otherwise.</summary>
    public int TicksPerBlock(double miningSpeed)
    {
        var strength = spec.GetDouble("blockStrength", 0);
        if (spec.GetDouble("instamineFactor") is { } instamine && miningSpeed > instamine * strength)
            return 1;
        var raw = miningSpeed > 0
            ? Math.Round(spec.GetDouble("breakTickFactor", 30) * strength / miningSpeed, MidpointRounding.AwayFromZero)
            : double.MaxValue / 2;
        return (int)Math.Min(int.MaxValue / 2, Math.Max(spec.GetDouble("minTicks", 4), raw));
    }

    /// <summary>Validation keeps minTicks >= 1 and overheadTicks >= 0; the floor of one tick guards the division anyway.</summary>
    public double BlocksPerHour(double miningSpeed) =>
        spec.GetDouble("ticksPerHour", 72000) / Math.Max(1, TicksPerBlock(miningSpeed) + spec.GetDouble("overheadTicks", 0));

    private double PristineFactor(IReadOnlyDictionary<string, double> stats)
    {
        var stat = spec.GetString("pristineStat");
        if (stat == null)
            return 1;
        return 1 + spec.GetDouble("pristineFactor", 0.79) * stats.GetValueOrDefault(stat);
    }

    private double ChanceFactor(IReadOnlyDictionary<string, double> stats)
    {
        var stat = spec.GetString("chanceStat");
        return stat == null ? 1 : stats.GetValueOrDefault(stat) / spec.GetDouble("chanceDivisor", 100);
    }

    internal static void Validate(ModelCheck check)
    {
        check.RequireStat("speedStat");
        ValidateFortuneStats(check);
        check.RequirePositive("blockStrength");
        foreach (var positive in new[] { "breakTickFactor", "ticksPerHour", "fortuneDivisor", "spreadDivisor", "chanceDivisor", "rareChanceDivisor", "instamineFactor" })
            check.OptionalPositive(positive);
        check.OptionalAtLeast("minTicks", 1);
        check.OptionalNonNegative("overheadTicks");
        check.OptionalNonNegative("pristineFactor");
        check.OptionalStat("spreadStat");
        check.OptionalStat("pristineStat");
        check.OptionalStat("chanceStat");
        check.OptionalStat("rareChanceStat");
        RareParams.Validate(check);
    }

    private static void ValidateFortuneStats(ModelCheck check)
    {
        check.Known("fortuneStats", "fortuneStat");
        // an explicit empty list says no fortune stat applies (e.g. Hard Stone); a missing one is an error
        if (check.Spec.Has("fortuneStats") && check.Spec.GetStringList("fortuneStats") == null)
            check.Error("model.fortuneStats must be a list of stat names");
        var stats = FortuneStats(check.Spec);
        foreach (var duplicate in (check.Spec.GetStringList("fortuneStats") ?? []).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            check.Error($"model.fortuneStats lists '{duplicate.Key}' more than once; it would be counted twice");
        if (stats.Count == 0 && !check.Spec.Has("fortuneStats"))
            check.Error("model.fortuneStats (or fortuneStat) is required for kind mining; use [] when no fortune applies");
        foreach (var stat in stats)
            check.StatInEverySetup("fortuneStats", stat);
    }
}

/// <summary>
/// Fishing (FS clamped to fishingSpeedCap when set, SCC to sccCap=100): seconds per catch = baseWaitSeconds/(1+FS/100) + reelSeconds + SCC/100·killSecondsPerCreature,
/// catches/h = 3600/that, creatures = catches·SCC/100, bulk = creatures (or catches with bulkFrom=catches),
/// rare = creatures·(1+MF/100), throughput = catches.
/// </summary>
public sealed class FishingRateModel(RateModelSpec spec) : IGearRateModel
{
    public string Kind => GearModelKinds.Fishing;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup)
    {
        var creatureShare = SeaCreatureShare(stats);
        var catches = CatchesPerHour(stats, creatureShare);
        var creatures = catches * creatureShare;
        var bulk = spec.GetString("bulkFrom") == "catches" ? catches : creatures;
        return new RateFactors(catches, bulk, creatures * RareParams.Factor(spec, stats));
    }

    private double SeaCreatureShare(IReadOnlyDictionary<string, double> stats)
    {
        var chance = Math.Min(GearRateModels.Stat(stats, spec.GetString("seaCreatureChanceStat")), spec.GetDouble("sccCap", 100));
        return chance / spec.GetDouble("seaCreatureChanceDivisor", 100);
    }

    internal double CatchesPerHour(IReadOnlyDictionary<string, double> stats, double creatureShare)
    {
        var fishingSpeed = GearRateModels.Stat(stats, spec.GetString("fishingSpeedStat"));
        if (spec.GetDouble("fishingSpeedCap") is { } cap)
            fishingSpeed = Math.Min(fishingSpeed, cap);
        var speed = 1 + fishingSpeed / spec.GetDouble("fishingSpeedDivisor", 100);
        var wait = spec.GetDouble("baseWaitSeconds", 0) / Math.Max(speed, 1e-9);
        var seconds = wait + spec.GetDouble("reelSeconds", 0) + creatureShare * spec.GetDouble("killSecondsPerCreature", 0);
        return seconds > 0 ? 3600 / seconds : 0;
    }

    internal static void Validate(ModelCheck check)
    {
        check.RequireStat("fishingSpeedStat");
        check.RequireStat("seaCreatureChanceStat");
        check.RequirePositive("baseWaitSeconds");
        check.OptionalNonNegative("reelSeconds");
        check.OptionalNonNegative("killSecondsPerCreature");
        check.OptionalPositive("fishingSpeedDivisor");
        check.OptionalPositive("seaCreatureChanceDivisor");
        check.OptionalPositive("fishingSpeedCap");
        check.OptionalPositive("sccCap");
        check.OptionalString("bulkFrom", "creatures", "catches");
        RareParams.Validate(check);
    }
}

/// <summary>Run based content: runs/h = 3600/(runSeconds + overheadSeconds), rare additionally ·(1+MF/100) when mfAffected.</summary>
public sealed class RunTimeRateModel(RateModelSpec spec) : IGearRateModel
{
    public string Kind => GearModelKinds.RunTime;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup)
    {
        var runs = RunsPerHour(spec, setup);
        var rare = spec.GetBool("mfAffected") ? runs * RareParams.Factor(spec, stats) : runs;
        return new RateFactors(runs, runs, rare);
    }

    internal static double RunsPerHour(RateModelSpec spec, GearSetup setup)
    {
        var seconds = (setup.RunSeconds ?? 0) + spec.GetDouble("overheadSeconds", 0);
        return seconds > 0 ? 3600 / seconds : 0;
    }

    internal static void Validate(ModelCheck check)
    {
        check.OptionalNonNegative("overheadSeconds");
        foreach (var setup in check.Setups.Where(s => s.RunSeconds is not > 0))
            check.Error($"setup '{setup.Id}' needs runSeconds > 0 for kind runTime");
        check.Known("mfAffected");
        if (check.Spec.GetBool("mfAffected"))
            check.RequireStat("rareStat");
        else
            check.OptionalStat("rareStat");
        check.OptionalPositive("rareDivisor");
        check.OptionalHttps("formulaSource");
    }
}

/// <summary>
/// Gear that makes a harder tier doable. Rates are the tiers' community rates (see <see cref="GearSetupEstimator"/>),
/// so the factors are neutral; the tier rules (Tiers, unlocksTask) are checked by <see cref="GearProfileValidator"/>.
/// </summary>
public sealed class TierUnlockRateModel : IGearRateModel
{
    public string Kind => GearModelKinds.TierUnlock;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup) => RateFactors.Neutral;

    internal static void Validate(ModelCheck check)
    {
        foreach (var setup in check.Setups.Where(s => s.RunSeconds != null))
            check.Error($"setup '{setup.Id}': runSeconds is not used by kind tierUnlock");
    }
}

/// <summary>An explicit research multiplier per setup id, applied to every component. Always low confidence.</summary>
public sealed class FixedRateModel(RateModelSpec spec) : IGearRateModel
{
    public string Kind => GearModelKinds.Fixed;

    public RateFactors Factors(IReadOnlyDictionary<string, double> stats, GearSetup setup)
    {
        var multiplier = spec.GetNumberMap("multipliers")?.GetValueOrDefault(setup.Id) ?? 1;
        return new RateFactors(multiplier, multiplier, multiplier);
    }

    internal static void Validate(ModelCheck check)
    {
        check.Known("multipliers");
        var multipliers = check.Spec.GetNumberMap("multipliers");
        if (multipliers == null)
        {
            check.Error("model.multipliers ({setupId: number}) is required for kind fixed");
            return;
        }
        foreach (var setup in check.Setups.Where(s => multipliers.GetValueOrDefault(s.Id) is not > 0))
            check.Error($"model.multipliers needs a number > 0 for setup '{setup.Id}'");
    }
}
