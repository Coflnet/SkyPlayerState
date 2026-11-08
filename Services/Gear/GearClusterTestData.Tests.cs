using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Observations and gear for the cluster tests (tags as in the shipped farming profile).</summary>
internal static class GearClusterTestData
{
    public static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    public const string Wheat = "Wheat Farming";

    public static string Player(int n) => new Guid(n, 0, 0, new byte[8]).ToString("N");

    public static ObservedGear TestGear(int harvesting = 6, string? pet = "ELEPHANT", string? petTier = "LEGENDARY", int petLevel = 100,
        string tool = "THEORETICAL_HOE_WHEAT_3", string armor = "FERMENTO", bool equipment = true)
    {
        var slots = new Dictionary<string, GearPiece>
        {
            [GearSnapshot.Tool] = new()
            {
                Tag = tool, ItemType = "FARMING TOOL", Reforge = "blessed",
                Enchants = new() { ["harvesting"] = harvesting },
                Counters = new() { ["farmed_cultivating"] = 1_000_000 },
                Stats = new() { ["Farming Fortune"] = 100 + harvesting * 12.5 }
            },
            [GearSnapshot.Helmet] = new() { Tag = armor + "_HELMET", ItemType = "HELMET" },
            [GearSnapshot.Chestplate] = new() { Tag = armor + "_CHESTPLATE", ItemType = "CHESTPLATE" },
            [GearSnapshot.Leggings] = new() { Tag = armor + "_LEGGINGS", ItemType = "LEGGINGS" },
            [GearSnapshot.Boots] = new() { Tag = armor + "_BOOTS", ItemType = "BOOTS" }
        };
        if (equipment)
            slots[GearSnapshot.Necklace] = new() { Tag = "LOTUS_NECKLACE", ItemType = "NECKLACE" };
        return new ObservedGear
        {
            Slots = slots,
            Pet = pet == null ? null : new GearPet { Type = pet, Tier = petTier, Level = petLevel }
        };
    }

    /// <summary>One observation of <paramref name="hours"/> at <paramref name="commonPerHour"/>.</summary>
    public static GearClusterInput Obs(int player, ObservedGear gear, double hours, double commonPerHour, double rarePerHour = 0,
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
        return new GearClusterInput(row, gear);
    }

    public static string Fingerprint(ObservedGear gear) => GearFingerprint.Of(new GearSnapshot { Slots = gear.Slots, Pet = gear.Pet });
}

/// <summary>In-memory <see cref="IGearObservationReader"/>: rows by (task, week), gear by fingerprint.</summary>
internal class InMemoryGearReader : IGearObservationReader
{
    public List<(string Task, GearObservationRow Row)> Rows { get; } = new();
    public Dictionary<string, ObservedGear> GearRows { get; } = new();
    public List<DateTime> WeeksRead { get; } = new();
    public int GearReads { get; private set; }

    public void Add(params GearClusterInput[] inputs)
    {
        foreach (var input in inputs)
        {
            Rows.Add((GearClusterTestData.Wheat, input.Observation));
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
