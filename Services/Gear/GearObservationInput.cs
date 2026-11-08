using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The columns of a <c>task_gear_observations</c> row the derivation needs (a slim read model of
/// <see cref="GearObservation"/>, so a task's 13 weeks of rows stay small in memory).
/// </summary>
public sealed record GearObservationRow
{
    public static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    public static readonly IReadOnlyDictionary<string, double> NoStats = new Dictionary<string, double>();

    public string PlayerKey { get; init; } = string.Empty;
    public DateTime FoldedAt { get; init; }
    public double Seconds { get; init; }
    public double CommonCoins { get; init; }
    public double RareCoins { get; init; }
    public string Category { get; init; } = string.Empty;
    public string GearFingerprint { get; init; } = string.Empty;
    public IReadOnlySet<string> Flags { get; init; } = NoFlags;
    public string StatsSource { get; init; } = GearObservationBuilder.StatsNone;
    public IReadOnlyDictionary<string, double> Stats { get; init; } = NoStats;

    public static GearObservationRow From(GearObservation o) => new()
    {
        PlayerKey = o.PlayerKey,
        FoldedAt = o.FoldedAt,
        Seconds = o.Seconds,
        CommonCoins = o.CommonCoins,
        RareCoins = o.RareCoins,
        Category = o.Category,
        GearFingerprint = o.GearFingerprint,
        Flags = o.Flags ?? NoFlags,
        StatsSource = o.StatsSource,
        Stats = o.Stats ?? new()
    };
}

/// <summary>One observation with its stored gear and its change key (computed once per gear, see <see cref="GearChangeKeyCache"/>).</summary>
public sealed class GearObservationInput
{
    public GearObservationRow Observation { get; }
    public ObservedGear Gear { get; }
    /// <summary>The key the modifier effects compare, see <see cref="GearChangeKeys.For"/>; null when the gear cannot be keyed.</summary>
    public GearChangeKey? ChangeKey { get; }

    public GearObservationInput(GearObservationRow observation, ObservedGear gear, GearChangeKey? changeKey)
    {
        Observation = observation;
        Gear = gear;
        ChangeKey = changeKey;
    }

    public GearObservationInput(GearObservationRow observation, ObservedGear gear)
        : this(observation, gear, GearChangeKeys.For(observation, gear))
    {
    }

    public double Hours => Observation.Seconds / 3600;
    public string Player => Observation.PlayerKey;
}
