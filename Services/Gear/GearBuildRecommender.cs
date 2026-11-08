using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>The "maximum achievable" build: catalog entries by slot, its exact score and the effects it triggers.</summary>
public sealed record GearRecommendation(IReadOnlyDictionary<string, GearCatalogEntry> Entries, double Score, IReadOnlyList<GearActiveEffect> Effects);

/// <summary>A rate read for a score, see <see cref="GearBuildRecommender.Rate"/>.</summary>
public sealed record GearRecommendedRate(double CoinsPerHour, bool Extrapolated, bool Capped, string Basis);

/// <summary>
/// Composes the recommended endgame build from the published catalog only (pure): per item slot the entry with the
/// highest own score contribution (the tool always, other slots only with a positive contribution), then, for every set
/// bonus whose members the catalog has, the same build with the set's best members in their slots. Each candidate gets
/// the pet that scores best with exactly its items (pet perks can depend on the tool and armor), and the candidate with
/// the highest exact score (the scorer over the combined pieces, effects included) wins, so a full set replaces a mix
/// only when its bonus makes it score higher.
/// </summary>
public static class GearBuildRecommender
{
    public const string BasisCurve = "curve";
    public const string BasisCohort = "cohort";

    public static GearRecommendation? Recommend(IReadOnlyList<GearCatalogEntry> catalog, GearScorer scorer)
    {
        var items = catalog.Where(e => e.Pet == null).ToList();
        var pets = catalog.Where(e => e.Pet != null).ToList();
        var mix = BestMix(items);
        if (mix.Count == 0 && pets.Count == 0)
            return null;
        var best = Candidates(items, mix, scorer)
            .Select(c => WithBestPet(c, pets, scorer))
            .Aggregate((a, b) => b.Score > a.Score + 1e-9 ? b : a);
        return new GearRecommendation(best.Entries, best.Score, scorer.Active(Gear(best.Entries)));
    }

    /// <summary>
    /// The rate at the score: from the curve when there is one (evaluated at most <see cref="GearModelOptions.MaxExtrapolation"/>
    /// beyond the observed range, flagged extrapolated beyond it, and never above <paramref name="capPerHour"/>, rounded to
    /// 2 significant digits when that cap binds), else the cohort's published rate; null without either. The cap is the
    /// late game cohort's published rate at every cohort size: it is public already, so a capped rate reveals nothing new
    /// (a percentile of the players' rates can land exactly on one player's rate).
    /// </summary>
    public static GearRecommendedRate? Rate(double score, GearCurveModel? curve, double rareCoinsPerHour, double? capPerHour,
        double? cohortCoinsPerHour, GearScoreFormula formula, GearModelOptions options)
    {
        if (curve == null)
            return cohortCoinsPerHour is { } cohortRate ? new GearRecommendedRate(cohortRate, false, false, BasisCohort) : null;
        var limit = formula.Extend(curve.ScoreMax, options.MaxExtrapolation);
        var rate = curve.PredictCommon(Math.Min(score, limit)) + rareCoinsPerHour;
        var extrapolated = score > curve.ScoreMax;
        if (capPerHour is not { } cap || rate <= cap)
            return new GearRecommendedRate(Math.Round(rate), extrapolated, false, BasisCurve);
        return new GearRecommendedRate(GearModelPublisher.RoundSignificant(cap, 2), extrapolated, true, BasisCurve);
    }

    private static Dictionary<string, GearCatalogEntry> BestMix(IReadOnlyList<GearCatalogEntry> items)
    {
        var result = new Dictionary<string, GearCatalogEntry>(StringComparer.Ordinal);
        foreach (var slot in items.GroupBy(e => e.Slot, StringComparer.Ordinal))
        {
            var best = Best(slot);
            if (best.OwnContribution > 0 || slot.Key == GearSnapshot.Tool)
                result[slot.Key] = best;
        }
        return result;
    }

    private static IEnumerable<Dictionary<string, GearCatalogEntry>> Candidates(IReadOnlyList<GearCatalogEntry> items,
        Dictionary<string, GearCatalogEntry> mix, GearScorer scorer)
    {
        yield return mix;
        foreach (var set in scorer.Effects.Where(e => e.Kind == GearEffect.KindSet))
        {
            var members = items.Where(e => set.Tags!.Contains(e.Tag, StringComparer.Ordinal))
                .GroupBy(e => e.Slot, StringComparer.Ordinal).Select(Best).ToList();
            if (members.Count < 2)
                continue;
            var candidate = new Dictionary<string, GearCatalogEntry>(mix, StringComparer.Ordinal);
            foreach (var member in members)
                candidate[member.Slot] = member;
            yield return candidate;
        }
    }

    /// <summary>The candidate with the pet that adds the most score to it (no pet when none adds anything).</summary>
    private static (IReadOnlyDictionary<string, GearCatalogEntry> Entries, double Score) WithBestPet(
        Dictionary<string, GearCatalogEntry> items, IReadOnlyList<GearCatalogEntry> pets, GearScorer scorer)
    {
        (IReadOnlyDictionary<string, GearCatalogEntry> Entries, double Score) best = (items, scorer.Score(Gear(items)));
        foreach (var pet in pets)
        {
            var withPet = new Dictionary<string, GearCatalogEntry>(items, StringComparer.Ordinal) { [GearPieceCatalog.PetSlot] = pet };
            var score = scorer.Score(Gear(withPet));
            if (score > best.Score + 1e-9)
                best = (withPet, score);
        }
        return best;
    }

    private static GearCatalogEntry Best(IEnumerable<GearCatalogEntry> entries)
        => entries.OrderByDescending(e => e.OwnContribution).ThenByDescending(e => e.Players).ThenBy(e => e.Id, StringComparer.Ordinal).First();

    public static ObservedGear Gear(IReadOnlyDictionary<string, GearCatalogEntry> entries) => new()
    {
        Slots = entries.Values.Where(e => e.Piece != null).ToDictionary(e => e.Slot, e => e.Piece!),
        Pet = entries.Values.FirstOrDefault(e => e.Pet != null)?.Pet
    };
}
