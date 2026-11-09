using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>An effect that applies to a build, with its amount (added value, or the fraction for a multiplier).</summary>
public sealed record GearActiveEffect(GearEffect Effect, double Amount);

/// <summary>
/// The gear score of one task: the task's <see cref="GearScoreFormula"/> over the GEAR-ONLY stat totals. Totals are the
/// lore stats of every worn piece and the tool plus the pet's lore stats, then the additive special effects
/// (<see cref="GearEffectTable"/>: set tiers by worn piece count, pet perks by type, rarity and level, equipment and
/// enchant effects), then the multipliers. Missing slots contribute 0; base and menu stats never count.
/// </summary>
public sealed class GearScorer
{
    public GearScoreFormula Formula { get; }
    public IReadOnlyList<GearEffect> Effects { get; }

    public GearScorer(GearScoreFormula formula, IReadOnlyList<GearEffect> effects)
    {
        Formula = formula;
        Effects = effects;
    }

    /// <summary>The task's scorer, null when the task has no score formula (see <see cref="GearScoreFormulas.For"/>).</summary>
    public static GearScorer? For(string task, string category, GearEffectTable? table = null)
    {
        var formula = GearScoreFormulas.For(category, task);
        return formula == null ? null : new(formula, (table ?? GearEffectTable.Embedded).For(GearTaskContext.For(task, category)));
    }

    public double Score(ObservedGear gear) => Formula.Score(Totals(gear));

    /// <summary>The score of the piece worn alone minus the empty build's (the piece's own share, set tier 1 included).</summary>
    public double PieceContribution(string slot, GearPiece piece) => Contribution(Totals(Single(slot, piece)));

    /// <summary>
    /// The piece's contribution to one branch of the formula (<see cref="GearScoreFormula.Branches"/>), only to order a
    /// slot's pieces for that branch: the mage branch's empty build is ln 0 (floored), so its values are not comparable to
    /// the formula's.
    /// </summary>
    public double PieceContribution(string slot, GearPiece piece, GearScoreFormula branch)
        => branch.Score(Totals(Single(slot, piece))) - branch.Empty;

    /// <summary>The pet's lore stats plus its perks, as a score above the empty build.</summary>
    public double PetContribution(GearPet pet) => Contribution(Totals(new ObservedGear { Pet = pet }));

    /// <summary>Stat totals as a score above the empty build.</summary>
    public double Contribution(IReadOnlyDictionary<string, double> totals) => Formula.Score(totals) - Formula.Empty;

    /// <summary>
    /// The piece's share of the full set bonuses it belongs to (the full tier split evenly over the tiers, minus what
    /// the piece alone already gets), as score.
    /// </summary>
    public double SetShare(string slot, GearPiece piece)
    {
        var gear = Single(slot, piece);
        var totals = Totals(gear);
        var before = new Dictionary<string, double>(totals, StringComparer.Ordinal);
        foreach (var effect in SetsOf(piece.Tag))
            Apply(totals, effect, SetShareAmount(effect));
        return Formula.Score(totals) - Formula.Score(before);
    }

    public IEnumerable<GearEffect> SetsOf(string tag)
        => Effects.Where(e => e.Kind == GearEffect.KindSet && e.Tags!.Contains(tag, StringComparer.Ordinal));

    /// <summary>Per piece share of the full set bonus beyond the 1 piece tier.</summary>
    public static double SetShareAmount(GearEffect set) => set.Tiers![^1] / set.Tiers.Count - set.Tiers[0];

    public Dictionary<string, double> Totals(ObservedGear gear)
    {
        var totals = LoreTotals(gear);
        var active = Active(gear);
        foreach (var effect in active.Where(a => !a.Effect.IsMultiplier))
            Apply(totals, effect.Effect, effect.Amount);
        foreach (var effect in active.Where(a => a.Effect.IsMultiplier && a.Effect.PieceTags != null))
            AddPieceMultiplier(totals, gear, effect);
        foreach (var effect in active.Where(a => a.Effect.IsMultiplier && a.Effect.PieceTags == null))
            Apply(totals, effect.Effect, effect.Amount);
        return totals;
    }

    /// <summary>The effects the build triggers; of several pet effects of the same group the highest matching rarity wins.</summary>
    public List<GearActiveEffect> Active(ObservedGear gear)
    {
        var result = new List<GearActiveEffect>();
        foreach (var effect in Effects.Where(e => ToolMatches(e, gear)))
            AddIfActive(result, effect, gear);
        return MostSpecificPets(result);
    }

    private void AddIfActive(List<GearActiveEffect> result, GearEffect effect, ObservedGear gear)
    {
        var amount = effect.Kind switch
        {
            GearEffect.KindSet => SetAmount(effect, gear),
            GearEffect.KindPet => PetAmount(effect, gear.Pet),
            GearEffect.KindEquipment => Worn(gear).Any(p => effect.Tags!.Contains(p.Tag, StringComparer.Ordinal)) ? effect.Value : 0,
            GearEffect.KindEnchant => Worn(gear).Sum(p => p.Enchants?.GetValueOrDefault(effect.Enchant!) ?? 0) * effect.Value,
            _ => 0
        };
        if (amount != 0)
            result.Add(new GearActiveEffect(effect, amount));
    }

    private static double SetAmount(GearEffect set, ObservedGear gear)
    {
        var count = (gear.Slots ?? new()).Values.Count(p => set.Tags!.Contains(p.Tag, StringComparer.Ordinal));
        return count == 0 ? 0 : set.Tiers![Math.Min(count, set.Tiers.Count) - 1];
    }

    private static double PetAmount(GearEffect effect, GearPet? pet)
    {
        if (pet == null || pet.Type != effect.PetType || GearRarities.Rank(pet.Tier) < GearRarities.Rank(effect.MinRarity))
            return 0;
        return effect.Value * Math.Min(pet.Level, effect.AtLevel) / effect.AtLevel;
    }

    private static List<GearActiveEffect> MostSpecificPets(List<GearActiveEffect> active)
    {
        var pets = active.Where(a => a.Effect.Kind == GearEffect.KindPet)
            .GroupBy(a => PetGroup(a.Effect))
            .Select(g => g.OrderByDescending(a => GearRarities.Rank(a.Effect.MinRarity)).First());
        return active.Where(a => a.Effect.Kind != GearEffect.KindPet).Concat(pets).ToList();
    }

    private static string PetGroup(GearEffect effect)
        => string.Join("|", effect.PetType, effect.Mode, string.Join(",", effect.Stats), string.Join(",", effect.PieceTags ?? new()));

    private static bool ToolMatches(GearEffect effect, ObservedGear gear)
        => effect.ToolType == null || gear.Slots?.GetValueOrDefault(GearSnapshot.Tool)?.ItemType == effect.ToolType;

    private static void Apply(Dictionary<string, double> totals, GearEffect effect, double amount)
    {
        foreach (var stat in effect.Stats)
        {
            var current = totals.GetValueOrDefault(stat);
            totals[stat] = effect.IsMultiplier ? current * (1 + amount) : current + amount;
        }
    }

    private static void AddPieceMultiplier(Dictionary<string, double> totals, ObservedGear gear, GearActiveEffect active)
    {
        var pieces = Worn(gear).Where(p => active.Effect.PieceTags!.Contains(p.Tag, StringComparer.Ordinal)).ToList();
        foreach (var stat in active.Effect.Stats)
        {
            var pieceTotal = pieces.Sum(p => p.Stats?.GetValueOrDefault(stat) ?? 0);
            if (pieceTotal != 0)
                totals[stat] = totals.GetValueOrDefault(stat) + pieceTotal * active.Amount;
        }
    }

    private static Dictionary<string, double> LoreTotals(ObservedGear gear)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var stats in Worn(gear).Select(p => p.Stats).Append(gear.Pet?.Stats))
            foreach (var (name, value) in stats ?? new())
                totals[name] = totals.GetValueOrDefault(name) + value;
        return totals;
    }

    private static IEnumerable<GearPiece> Worn(ObservedGear gear) => (gear.Slots ?? new()).Values;

    private static ObservedGear Single(string slot, GearPiece piece) => new() { Slots = new() { [slot] = piece } };
}
