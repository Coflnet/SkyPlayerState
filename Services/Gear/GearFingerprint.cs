using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The stored form of an observation's gear (row of <c>gear_fingerprints</c>): rate relevant pieces with sorted keys,
/// counters floored to buckets, the pet level floored to tens. No timestamps, evidence or player data.
/// </summary>
public class ObservedGear
{
    [JsonPropertyName("slots")] public Dictionary<string, GearPiece> Slots { get; set; } = new();
    [JsonPropertyName("pet")] public GearPet? Pet { get; set; }
    [JsonPropertyName("bait")] public List<string> Bait { get; set; } = new();
}

/// <summary>Canonical json and short hashes of gear, see <see cref="ObservedGear"/>.</summary>
public static class GearFingerprint
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Floors a counter so the same item keeps its fingerprint while it is used: levelable_lvl to tens, every other
    /// counter to a 1-2-5 log scale (1, 2, 5, 10, 20, 50, ...). The wiki and NEU disagree on the enchant thresholds, so
    /// raw buckets are stored instead of a derived level.
    /// </summary>
    public static long BucketCounter(string key, long value)
    {
        if (value <= 0)
            return 0;
        if (key == "levelable_lvl")
            return value / 10 * 10;
        var power = 1L;
        while (power <= value / 10)
            power *= 10;
        var leading = value / power;
        var step = leading >= 5 ? 5 : leading >= 2 ? 2 : 1;
        return step * power;
    }

    /// <summary>A copy with sorted keys and bucketed counters.</summary>
    public static GearPiece Canonical(GearPiece piece) => new(piece)
    {
        Enchants = Sorted(piece.Enchants),
        Gems = Sorted(piece.Gems),
        Parts = Sorted(piece.Parts),
        Applied = Sorted(piece.Applied),
        Stats = Sorted(piece.Stats),
        Counters = Sorted(piece.Counters).ToDictionary(c => c.Key, c => BucketCounter(c.Key, c.Value))
    };

    public static ObservedGear Observed(GearSnapshot snapshot) => new()
    {
        Slots = (snapshot.Slots ?? new()).OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(s => s.Key, s => Canonical(s.Value)),
        Pet = snapshot.Pet == null ? null : new GearPet(snapshot.Pet) { Level = snapshot.Pet.Level / 10 * 10, Stats = Sorted(snapshot.Pet.Stats) },
        Bait = (snapshot.Bait ?? new()).OrderBy(b => b, StringComparer.Ordinal).ToList()
    };

    public static string ToJson(ObservedGear gear) => JsonSerializer.Serialize(gear, Json);

    /// <summary>First 16 hex chars of the SHA-256 of the canonical json.</summary>
    public static string Of(GearSnapshot snapshot) => Hash(ToJson(WithoutStats(Observed(snapshot))));

    /// <summary>
    /// Lore stats are derived values that grow with tool levels, pet levels and visitor bonuses: they are stored with the
    /// gear but kept out of every identity (fingerprint, piece key), like counters.
    /// </summary>
    private static ObservedGear WithoutStats(ObservedGear gear) => new()
    {
        Slots = gear.Slots.ToDictionary(s => s.Key, s => new GearPiece(s.Value) { Stats = new() }),
        Pet = gear.Pet == null ? null : new GearPet(gear.Pet) { Stats = new() },
        Bait = gear.Bait
    };

    /// <summary>
    /// Identity of a piece ignoring its counters and lore stats (they grow while the item is used): two keys differ only when the
    /// piece was swapped or modified.
    /// </summary>
    public static string PieceKey(GearPiece piece)
        => Hash(JsonSerializer.Serialize(new GearPiece(Canonical(piece)) { Counters = new(), Stats = new() }, Json));

    /// <summary>Key of the snapshot's tool when it is known (not ambiguous), else null.</summary>
    public static string? ToolKey(GearSnapshot? snapshot)
    {
        var tool = snapshot?.ToolPiece;
        if (tool == null || !GearToolEvidence.IsKnown(snapshot!.ToolEvidence))
            return null;
        return PieceKey(tool);
    }

    private static Dictionary<string, T> Sorted<T>(Dictionary<string, T>? values)
        => (values ?? new()).OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value);

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}
