using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Observations and gear for the gear model tests (tags as in the shipped farming profile).</summary>
internal static class GearModelTestData
{
    public static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    public const string Wheat = "Wheat Farming";

    public static string Player(int n) => new Guid(n, 0, 0, new byte[8]).ToString("N");

    /// <summary>Farming gear; the tool's Farming Fortune is <paramref name="toolFortune"/> (default 100 + 12.5 per harvesting level).</summary>
    public static ObservedGear TestGear(int harvesting = 6, string? pet = "ELEPHANT", string? petTier = "LEGENDARY", int petLevel = 100,
        string tool = "THEORETICAL_HOE_WHEAT_3", string armor = "FERMENTO", bool equipment = true, double? toolFortune = null,
        double armorFortune = 0)
    {
        var slots = new Dictionary<string, GearPiece>
        {
            [GearSnapshot.Tool] = new()
            {
                Tag = tool, ItemType = "FARMING TOOL", Reforge = "blessed",
                Enchants = new() { ["harvesting"] = harvesting },
                Counters = new() { ["farmed_cultivating"] = 1_000_000 },
                Stats = new() { ["Farming Fortune"] = toolFortune ?? 100 + harvesting * 12.5 }
            }
        };
        foreach (var slot in GearSnapshot.ArmorSlots)
            slots[slot] = Armor(armor, slot, armorFortune);
        if (equipment)
            slots[GearSnapshot.Necklace] = new() { Tag = "LOTUS_NECKLACE", ItemType = "NECKLACE" };
        return new ObservedGear
        {
            Slots = slots,
            Pet = pet == null ? null : new GearPet { Type = pet, Tier = petTier, Level = petLevel }
        };
    }

    public static GearPiece Armor(string set, string slot, double fortune = 0)
    {
        var piece = new GearPiece { Tag = $"{set}_{slot.ToUpperInvariant()}", ItemType = slot.ToUpperInvariant() };
        if (fortune != 0)
            piece.Stats["Farming Fortune"] = fortune;
        return piece;
    }

    /// <summary>One observation of <paramref name="hours"/> at <paramref name="commonPerHour"/>.</summary>
    public static GearObservationInput Obs(int player, ObservedGear gear, double hours, double commonPerHour, double rarePerHour = 0,
        DateTime? at = null, string category = GearModifierTable.Farming, Dictionary<string, double>? stats = null, params string[] flags)
    {
        var row = new GearObservationRow
        {
            PlayerKey = Player(player),
            Category = category,
            FoldedAt = at ?? Now.AddHours(-1),
            Seconds = hours * 3600,
            CommonCoins = commonPerHour * hours,
            RareCoins = rarePerHour * hours,
            GearFingerprint = Fingerprint(gear),
            Flags = flags.ToHashSet(),
            Stats = stats ?? new(),
            StatsSource = stats == null ? GearObservationBuilder.StatsNone : GearObservationBuilder.StatsBaseGear
        };
        return new GearObservationInput(row, gear);
    }

    /// <summary>
    /// A build whose only stat is the tool's Farming Fortune = <paramref name="score"/> (no effects: pet and armor without
    /// perks). The score is also written into a gem slot: lore stats are not part of a fingerprint, the gems are.
    /// </summary>
    public static ObservedGear ScoreGear(double score, string tool = "THEORETICAL_HOE_WHEAT_3")
    {
        var gear = TestGear(pet: null, armor: "TEST_PLAIN", equipment: false, toolFortune: score, tool: tool);
        gear.Slots[GearSnapshot.Tool].Gems["TEST_SCORE"] = score.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return gear;
    }

    public static string Fingerprint(ObservedGear gear) => GearFingerprint.Of(new GearSnapshot { Slots = gear.Slots, Pet = gear.Pet });

    /// <summary>The wheat scorer without special effects.</summary>
    public static GearScorer PlainScorer => GearScorer.For(Wheat, GearModifierTable.Farming, GearEffectTable.Empty)!;

    public static List<GearPlayerBuild> Builds(params GearObservationInput[] inputs) => GearPlayerBuilds.From(inputs, PlainScorer);
}

/// <summary>In-memory <see cref="IGearObservationReader"/>: rows by (task, week), gear by fingerprint.</summary>
internal class InMemoryGearReader : IGearObservationReader
{
    public List<(string Task, GearObservationRow Row)> Rows { get; } = new();
    public Dictionary<string, ObservedGear> GearRows { get; } = new();
    public List<DateTime> WeeksRead { get; } = new();
    public int GearReads { get; private set; }

    public void Add(params GearObservationInput[] inputs)
    {
        foreach (var input in inputs)
        {
            Rows.Add((GearModelTestData.Wheat, input.Observation));
            GearRows[input.Observation.GearFingerprint] = input.Gear;
        }
    }

    public Task<List<GearObservationRow>> ReadWeek(string taskName, DateTime weekStart, int limit, CancellationToken cancellationToken)
    {
        WeeksRead.Add(weekStart);
        return Task.FromResult(Rows.Where(r => r.Task == taskName && GearObservationBuilder.WeekStart(r.Row.FoldedAt) == weekStart)
            .Select(r => r.Row).OrderByDescending(o => o.FoldedAt).Take(limit).ToList());
    }

    public Task<Dictionary<string, ObservedGear>> ReadGear(IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken)
    {
        GearReads++;
        return Task.FromResult(fingerprints.Where(GearRows.ContainsKey).ToDictionary(f => f, f => GearRows[f]));
    }
}
