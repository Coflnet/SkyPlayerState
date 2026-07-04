using System.Text.Json.Serialization;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// Authoritative list of achievements a player can unlock. This is the single source of truth:
/// it is exposed on the API and generated into the mod client (SkyModCommands) via openapi-generator,
/// so the two services cannot drift apart unnoticed - removing/renaming a value here breaks the mod build.
///
/// The mod maps these to displayable "emblems"; that presentation is purely a mod concern.
///
/// Serialized as its name (string) on the HTTP API for readable, stable client enums. The explicit
/// numeric values are what MessagePack persists in the player state, so never reuse or reorder them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Achievement
{
    /// <summary>Created the first lowball offer.</summary>
    FirstLowball = 1,
    /// <summary>Closed the first profitable bazaar flip.</summary>
    BazaarFlipProfit = 2,
    /// <summary>Closed a bazaar flip at a loss.</summary>
    BazaarFlipLoss = 3,
    /// <summary>Landed a single bazaar flip worth 100M+ coins of profit.</summary>
    Whale = 4,
    /// <summary>Reserved - not granted yet (mysterious emblem on the mod side).</summary>
    NightOwl = 5,
    /// <summary>Reserved - not granted yet (mysterious emblem on the mod side).</summary>
    DiamondHands = 6,
}
