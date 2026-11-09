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
/// highest own score contribution, then, for every set bonus whose members the catalog has, the same build with the
/// set's best members in their slots. Each candidate gets the pet that scores best with exactly its items (pet perks can
/// depend on the tool and armor), and the candidate with the highest exact score (the scorer over the combined pieces,
/// effects included) wins, so a full set replaces a mix only when its bonus makes it score higher. Every slot the
/// catalog has pieces for is filled: where no piece adds score, with the slot's most used piece (most players, then the
/// higher contribution), so the build has boots and a pet even when they add nothing measurable.
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
    /// The build's rate: the late game cohort's published rate whenever there is a cohort (basis "cohort": the measured
    /// rate of the top builds, never below or above it); otherwise the curve at the score, read at most
    /// <see cref="GearModelOptions.MaxExtrapolation"/> beyond the observed range and flagged extrapolated beyond it
    /// (basis "curve"), capped like every curve prediction (<see cref="GearCurveModel.CommonCap"/>, flagged capped); null
    /// without either.
    /// </summary>
    public static GearRecommendedRate? Rate(double score, GearCurveModel? curve, double rareCoinsPerHour,
        double? cohortCoinsPerHour, GearScoreFormula formula, GearModelOptions options)
    {
        if (cohortCoinsPerHour is { } cohortRate)
            return new GearRecommendedRate(cohortRate, false, false, BasisCohort);
        if (curve == null)
            return null;
        var readAt = Math.Min(score, formula.Extend(curve.ScoreMax, options.MaxExtrapolation));
        var rate = curve.PredictCommon(readAt) + rareCoinsPerHour;
        return new GearRecommendedRate(Math.Round(rate), score > curve.ScoreMax, curve.IsCappedAt(readAt), BasisCurve);
    }

    private static Dictionary<string, GearCatalogEntry> BestMix(IReadOnlyList<GearCatalogEntry> items)
    {
        var result = new Dictionary<string, GearCatalogEntry>(StringComparer.Ordinal);
        foreach (var slot in items.GroupBy(e => e.Slot, StringComparer.Ordinal))
        {
            var best = Best(slot);
            result[slot.Key] = best.OwnContribution > 0 ? best : MostUsed(slot);
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

    /// <summary>The candidate with the pet that adds the most score to it; the most used pet when none adds anything.</summary>
    private static (IReadOnlyDictionary<string, GearCatalogEntry> Entries, double Score) WithBestPet(
        Dictionary<string, GearCatalogEntry> items, IReadOnlyList<GearCatalogEntry> pets, GearScorer scorer)
    {
        if (pets.Count == 0)
            return (items, scorer.Score(Gear(items)));
        var withoutPet = scorer.Score(Gear(items));
        var scored = pets.Select(pet => (Entries: With(items, pet), Pet: pet)).Select(c => (c.Entries, c.Pet, Score: scorer.Score(Gear(c.Entries)))).ToList();
        var best = scored.OrderByDescending(c => c.Score).ThenByDescending(c => c.Pet.Players).ThenBy(c => c.Pet.Id, StringComparer.Ordinal).First();
        if (best.Score > withoutPet + 1e-9)
            return (best.Entries, best.Score);
        var mostUsedId = MostUsed(pets).Id;
        var mostUsed = scored.Single(c => c.Pet.Id == mostUsedId);
        return (mostUsed.Entries, mostUsed.Score);
    }

    private static Dictionary<string, GearCatalogEntry> With(Dictionary<string, GearCatalogEntry> items, GearCatalogEntry pet)
        => new(items, StringComparer.Ordinal) { [GearPieceCatalog.PetSlot] = pet };

    /// <summary>The entry the most players use; ties: the higher contribution, then the id.</summary>
    private static GearCatalogEntry MostUsed(IEnumerable<GearCatalogEntry> entries)
        => entries.OrderByDescending(e => e.Players).ThenByDescending(e => e.Contribution).ThenBy(e => e.Id, StringComparer.Ordinal).First();

    private static GearCatalogEntry Best(IEnumerable<GearCatalogEntry> entries)
        => entries.OrderByDescending(e => e.OwnContribution).ThenByDescending(e => e.Players).ThenBy(e => e.Id, StringComparer.Ordinal).First();

    public static ObservedGear Gear(IReadOnlyDictionary<string, GearCatalogEntry> entries) => new()
    {
        Slots = entries.Values.Where(e => e.Piece != null).ToDictionary(e => e.Slot, e => e.Piece!),
        Pet = entries.Values.FirstOrDefault(e => e.Pet != null)?.Pet
    };
}
