namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// Current currency balances of a player as last seen on the sidebar scoreboard.
/// Lightweight projection of <see cref="ExtractedInfo"/> so callers that only need the
/// balances don't have to pull the full state (inventory, storage, ...).
/// </summary>
public class PlayerCurrencies
{
    /// <summary>
    /// Coins currently in the purse (or piggy bank). 0 when unknown, -1 while outside skyblock.
    /// </summary>
    public long Purse { get; set; }
    /// <summary>
    /// Bits currently available. 0 when unknown.
    /// </summary>
    public long Bits { get; set; }
}
