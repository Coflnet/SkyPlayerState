using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>An effect a catalog entry triggers alone or, for <see cref="IsSetShare"/>, its share of a full set bonus.</summary>
public sealed record GearEntryEffect(GearEffect Effect, double Amount, bool IsSetShare);

/// <summary>One published piece (or pet) of the catalog, unrounded.</summary>
public sealed record GearCatalogEntry(string Id, string Slot, GearPiece? Piece, GearPet? Pet, int Players,
    double OwnContribution, double SetShare, double? UsedAtPercentile, IReadOnlyList<GearEntryEffect> Effects)
{
    /// <summary>The piece's score alone plus its share of the full set bonuses it belongs to.</summary>
    public double Contribution => OwnContribution + SetShare;
    public string Tag => Piece?.Tag ?? Pet?.Type ?? "";
}

/// <summary>
/// The pieces builds of a task are composed from (pure). Per slot (tool, armor, equipment, pet) the distinct pieces of
/// the task's player-builds, keyed by tag and the modifiers the category keeps (the stored gear is already narrowed):
/// <list type="bullet">
/// <item>a tag used by fewer than k distinct players is never published;</item>
/// <item>each variant (same tag and modifiers) of at least k players is published as it is;</item>
/// <item>the sightings of the remaining variants, when they hold at least k players, are published as one piece with
/// only the modifiers k of them share (<see cref="GearSharedItems"/>); groups that end up with the same published piece
/// are merged into one entry;</item>
/// <item>lore stats are the per-stat minimum over the k best players' most used instances
/// (<see cref="GearSharedItems.InstanceStats{T}"/>), the contribution is scored from them;</item>
/// <item>a pet of unknown tier gets the tier its stats fit, its player or the type's known players show
/// (<see cref="GearSharedItems.TierFiller"/>); a pet without lore stats (tab list) gets its player's stats
/// (<see cref="GearPetFill"/>), a published pet without any those of similar pets of k players, else it is not published;</item>
/// <item>a pet's contribution is scored on top of the task's most common published tool and armor (the entry of each
/// slot with the most players), so perks that depend on them (a drill, a set's stats) count.</item>
/// </list>
/// </summary>
public static class GearPieceCatalog
{
    public const string PetSlot = "pet";
    public static readonly string[] Slots = [GearSnapshot.Tool, .. GearSnapshot.ArmorSlots, .. GearSnapshot.EquipmentSlots];
    private static readonly string[] BaseSlots = [GearSnapshot.Tool, .. GearSnapshot.ArmorSlots];

    private sealed record Sighting<T>(T Item, GearPlayerBuild Build)
    {
        public GearSeen<T> Seen => new(Item, Build.Hours, Build.Player);
    }

    private sealed record Context(GearScorer Scorer, GearScoreDistribution? Distribution, int MinPlayers);

    public static List<GearCatalogEntry> Build(IReadOnlyList<GearPlayerBuild> builds, GearScorer scorer,
        GearScoreDistribution? distribution, int minPlayers)
    {
        var context = new Context(scorer, distribution, minPlayers);
        var entries = Slots.SelectMany(slot => SlotEntries(slot, builds, context)).ToList();
        entries.AddRange(PetEntries(builds, BaseGear(entries), context));
        return entries;
    }

    private static IEnumerable<GearCatalogEntry> SlotEntries(string slot, IReadOnlyList<GearPlayerBuild> builds, Context context)
    {
        var sightings = builds.Where(b => b.Gear.Slots.ContainsKey(slot))
            .Select(b => new Sighting<GearPiece>(b.Gear.Slots[slot], b));
        var groups = sightings.GroupBy(s => s.Item.Tag, StringComparer.Ordinal)
            .SelectMany(tag => Variants(tag.ToList(), s => GearFingerprint.PieceKey(s.Item), context.MinPlayers));
        return Merged(groups, group => PieceEntry(slot, group, context));
    }

    /// <summary>
    /// Pets grouped by type and variant after their unknown tiers (<see cref="GearSharedItems.TierFiller"/>), then their
    /// players' stats (<see cref="GearPetFill.StatFiller"/>) are filled in.
    /// </summary>
    private static IEnumerable<GearCatalogEntry> PetEntries(IReadOnlyList<GearPlayerBuild> builds, ObservedGear baseGear, Context context)
    {
        var seen = builds.Where(b => b.Gear.Pet != null).Select(b => new Sighting<GearPet>(b.Gear.Pet!, b)).ToList();
        var fillTier = GearSharedItems.TierFiller(seen.Select(s => s.Seen).ToList(), context.MinPlayers);
        // a variant's player count (and the published tier) may include players whose tier is assumed, see TierFiller
        var tiered = seen.Select(s => s with { Item = fillTier(s.Item, s.Build.Player) }).ToList();
        var fillStats = GearPetFill.StatFiller(tiered.Select(s => s.Seen).ToList());
        var sightings = seen.Zip(tiered, (before, after) => after with
        {
            Item = fillStats(after.Item, after.Build.Player, GearPetFill.HeldItemKnown(before.Item))
        }).ToList();
        var all = sightings.Select(s => s.Seen).ToList();
        var groups = sightings.GroupBy(s => s.Item.Type, StringComparer.Ordinal)
            .SelectMany(type => Variants(type.ToList(), s => PetKey(s.Item), context.MinPlayers));
        return Merged(groups, group => PetEntry(group, all, baseGear, context));
    }

    /// <summary>
    /// The entries of the groups, best contribution first; groups whose published piece is the same (same id) are merged
    /// and published once with the players of all of them.
    /// </summary>
    private static IEnumerable<GearCatalogEntry> Merged<T>(IEnumerable<List<Sighting<T>>> groups, Func<List<Sighting<T>>, GearCatalogEntry?> entry)
    {
        var built = groups.Select(g => (Group: g, Entry: entry(g))).Where(e => e.Entry != null).ToList();
        return built.GroupBy(e => e.Entry!.Id, StringComparer.Ordinal)
            .Select(same => same.Count() == 1 ? same.First().Entry : entry(same.SelectMany(e => e.Group).ToList()))
            .OfType<GearCatalogEntry>()
            .DistinctBy(e => e.Id)
            .OrderByDescending(e => e.Contribution).ThenByDescending(e => e.Players).ThenBy(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>The variants of at least k players, then the rest of the tag's sightings as one group when they hold k players.</summary>
    private static IEnumerable<List<Sighting<T>>> Variants<T>(List<Sighting<T>> sameTag, Func<Sighting<T>, string> key, int minPlayers)
    {
        if (Players(sameTag) < minPlayers)
            yield break;
        var rest = new List<Sighting<T>>();
        foreach (var variant in sameTag.GroupBy(key, StringComparer.Ordinal).Select(g => g.ToList()))
        {
            if (Players(variant) >= minPlayers)
                yield return variant;
            else
                rest.AddRange(variant);
        }
        if (Players(rest) >= minPlayers)
            yield return rest;
    }

    private static GearCatalogEntry? PieceEntry(string slot, List<Sighting<GearPiece>> group, Context context)
    {
        var piece = GearSharedItems.SharedPiece(group.Select(s => s.Seen).ToList(), context.MinPlayers, context.Scorer.Contribution);
        if (piece == null)
            return null;
        var scorer = context.Scorer;
        var id = $"{slot}:{piece.Tag}:{GearFingerprint.PieceKey(piece)[..8]}";
        return new GearCatalogEntry(id, slot, piece, null, Players(group), scorer.PieceContribution(slot, piece),
            scorer.SetShare(slot, piece), UsedAt(group, context), PieceEffects(slot, piece, scorer));
    }

    /// <summary>
    /// The group's shared pet with its stats (<see cref="FillStats"/>). Not published when its stats are still unseen while
    /// its type has stats (<see cref="GearPetFill.StatsUnseen"/>): it would score 0 and, as the most used pet, sat in
    /// recommended endgames.
    /// </summary>
    private static GearCatalogEntry? PetEntry(List<Sighting<GearPet>> group, IReadOnlyList<GearSeen<GearPet>> all, ObservedGear baseGear, Context context)
    {
        var groupSeen = group.Select(s => s.Seen).ToList();
        var pet = GearSharedItems.SharedPet(groupSeen, context.MinPlayers, context.Scorer.Contribution);
        if (pet == null)
            return null;
        FillStats(pet, groupSeen, all, context);
        if (GearPetFill.StatsUnseen(pet, all))
            return null;
        var scorer = context.Scorer;
        var withPet = new ObservedGear { Slots = baseGear.Slots, Pet = pet };
        var effects = scorer.Active(withPet).Where(a => a.Effect.Kind == GearEffect.KindPet)
            .Select(a => new GearEntryEffect(a.Effect, a.Amount, false)).ToList();
        return new GearCatalogEntry($"{PetSlot}:{PetKey(pet)}", PetSlot, null, pet, Players(group),
            scorer.Score(withPet) - scorer.Score(baseGear), 0, UsedAt(group, context), effects);
    }

    /// <summary>
    /// A shared pet without stats of its own takes those of similar sightings with the same held item
    /// (<see cref="GearPetFill.SimilarStats"/>); one without a held item, when no sighting of its type without one has
    /// stats at all, the group's minimum over all held items (<see cref="GearSharedItems.AnyHeldItemStats"/>). The held
    /// item fallback came first and published a pet without one at a Relic's 150 next to plain pets at 90 (review 2026-11-10).
    /// </summary>
    private static void FillStats(GearPet pet, List<GearSeen<GearPet>> group, IReadOnlyList<GearSeen<GearPet>> all, Context context)
    {
        if (GearPetFill.HasStats(pet))
            return;
        pet.Stats = GearPetFill.SimilarStats(pet, all, context.MinPlayers, context.Scorer.Contribution);
        if (GearPetFill.HasStats(pet) || pet.HeldItem != null || GearPetFill.AnyStatsWithoutHeldItem(pet, all))
            return;
        pet.Stats = GearSharedItems.AnyHeldItemStats(pet, group, context.MinPlayers, context.Scorer.Contribution);
    }

    /// <summary>Per tool and armor slot the published entry with the most players: the build pets are scored with.</summary>
    private static ObservedGear BaseGear(IEnumerable<GearCatalogEntry> entries) => new()
    {
        Slots = entries.Where(e => e.Piece != null && BaseSlots.Contains(e.Slot))
            .GroupBy(e => e.Slot, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Players).ThenBy(e => e.Id, StringComparer.Ordinal).First().Piece!)
    };

    private static List<GearEntryEffect> PieceEffects(string slot, GearPiece piece, GearScorer scorer)
    {
        var alone = scorer.Active(new ObservedGear { Slots = new() { [slot] = piece } })
            .Where(a => a.Effect.Kind != GearEffect.KindSet)
            .Select(a => new GearEntryEffect(a.Effect, a.Amount, false));
        var sets = scorer.SetsOf(piece.Tag).Select(s => new GearEntryEffect(s, GearScorer.SetShareAmount(s) + s.Tiers![0], true));
        return alone.Concat(sets).ToList();
    }

    /// <summary>
    /// Where the builds using the entry lie in the score distribution: each player's mean percentile over their eligible
    /// builds in the group, then the median over at least k players; null otherwise.
    /// </summary>
    private static double? UsedAt<T>(List<Sighting<T>> group, Context context)
    {
        if (context.Distribution is not { } distribution)
            return null;
        var perPlayer = group.Where(s => s.Build.Eligible)
            .GroupBy(s => s.Build.Player, StringComparer.Ordinal)
            .Select(g => g.Average(s => distribution.RankOf(s.Build.Score)))
            .ToList();
        return perPlayer.Count < context.MinPlayers ? null : GearStatistics.Median(perPlayer);
    }

    private static string PetKey(GearPet pet) => $"{pet.Type}:{pet.Tier ?? "-"}:{pet.Level}:{pet.HeldItem ?? "-"}";

    private static int Players<T>(IEnumerable<Sighting<T>> sightings)
        => sightings.Select(s => s.Build.Player).Distinct(StringComparer.Ordinal).Count();
}
