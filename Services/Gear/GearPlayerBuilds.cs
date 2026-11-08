using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// One player's sessions with one gear fingerprint, collapsed: hours, common and rare coins summed (so a player with many
/// sessions of a build counts once), the gear score and which parts of the gear are known.
/// </summary>
public sealed record GearPlayerBuild(string Player, ObservedGear Gear, IReadOnlyList<GearObservationInput> Members,
    double Hours, double Common, double Rare, double Score)
{
    public double CommonPerHour => Hours > 0 ? Common / Hours : 0;
    public double TotalPerHour => Hours > 0 ? (Common + Rare) / Hours : 0;
    /// <summary>Armor pieces a build needs known (of 4) to be <see cref="Eligible"/>.</summary>
    public const int MinArmorPieces = 3;

    /// <summary>Tool and at least 3 armor pieces known: the score distribution and the curve only use these.</summary>
    public bool Eligible => HasTool(Gear) && GearSnapshot.ArmorSlots.Count(Gear.Slots.ContainsKey) >= MinArmorPieces;
    /// <summary>At least one equipment piece known (equipment is often unknown: it is read from a menu about weekly).</summary>
    public bool EquipmentKnown => GearSnapshot.EquipmentSlots.Any(Gear.Slots.ContainsKey);

    private static bool HasTool(ObservedGear gear) => gear.Slots.ContainsKey(GearSnapshot.Tool);
}

/// <summary>Groups observations into player-builds (pure).</summary>
public static class GearPlayerBuilds
{
    /// <summary>
    /// One build per (player, gear fingerprint). Stale or ambiguous parts were already dropped from the stored gear (and
    /// so from the fingerprint), so the gear alone says which parts are known. Scores are computed once per fingerprint.
    /// </summary>
    public static List<GearPlayerBuild> From(IEnumerable<GearObservationInput> inputs, GearScorer scorer)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        return inputs.Where(i => i.Hours > 0)
            .GroupBy(i => (i.Player, i.Observation.GearFingerprint))
            .Select(g => Build(g.ToList(), scorer, scores))
            .ToList();
    }

    private static GearPlayerBuild Build(List<GearObservationInput> members, GearScorer scorer, Dictionary<string, double> scores)
    {
        var first = members[0];
        var fingerprint = first.Observation.GearFingerprint;
        if (!scores.TryGetValue(fingerprint, out var score))
            scores[fingerprint] = score = scorer.Score(first.Gear);
        return new GearPlayerBuild(first.Player, first.Gear, members, members.Sum(m => m.Hours),
            members.Sum(m => m.Observation.CommonCoins), members.Sum(m => m.Observation.RareCoins), score);
    }

    public static int Players(IEnumerable<GearPlayerBuild> builds) => builds.Select(b => b.Player).Distinct(StringComparer.Ordinal).Count();
}
