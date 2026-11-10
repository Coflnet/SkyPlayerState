using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The rare drop rate of player-builds, robust to one lucky drop (unrounded). Rare drops are lumpy: the plain pooled rate
/// (all rare coins / all hours) let one drop move a whole task (F7's late game 7.5 -> 32.3M/h, 31.3 of it rare; Voidgloom
/// T4's curve 2 -> 15; live check 2026-11-10). So each player's rare coins count at most <see cref="PlayerCap"/> per hour
/// of that player: the per-player rare rate <see cref="GearModelOptions.RareCapPlayers"/> players of the task reach (the
/// k-th highest of the players' rare coins over their hours, like the curve's cap). A drop only one or two players got
/// counts at most at the third luckiest player's rate, and with fewer players with rare drops there is no rare rate.
/// The rows carry no drop counts (a row is a whole session with any number of drops), so players are the evidence.
/// </summary>
public sealed record GearRareRate(double PlayerCap)
{
    /// <summary>No cap: the plain pooled rate.</summary>
    public static readonly GearRareRate Uncapped = new(double.PositiveInfinity);

    private readonly record struct PlayerRare(string Player, double Hours, double Rare);

    /// <summary>The cap read from the task's builds; uncapped when <see cref="GearModelOptions.RareCapPlayers"/> is 0.</summary>
    public static GearRareRate Of(IEnumerable<GearPlayerBuild> builds, GearModelOptions options)
    {
        if (options.RareCapPlayers <= 0)
            return Uncapped;
        var rates = PerPlayer(builds).Select(p => (p.Player, p.Rare / p.Hours));
        return new GearRareRate(GearSharedItems.KthHighest(rates, options.RareCapPlayers) ?? 0);
    }

    /// <summary>The builds' rare coins (each player's capped at <see cref="PlayerCap"/> × their hours) over their hours; 0 without hours.</summary>
    public double PerHour(IEnumerable<GearPlayerBuild> builds)
    {
        var players = PerPlayer(builds);
        var hours = players.Sum(p => p.Hours);
        return hours > 0 ? players.Sum(Capped) / hours : 0;
    }

    private double Capped(PlayerRare player)
        => double.IsPositiveInfinity(PlayerCap) ? player.Rare : Math.Min(player.Rare, PlayerCap * player.Hours);

    private static List<PlayerRare> PerPlayer(IEnumerable<GearPlayerBuild> builds)
        => builds.GroupBy(b => b.Player, StringComparer.Ordinal)
            .Select(g => new PlayerRare(g.Key, g.Sum(b => b.Hours), g.Sum(b => b.Rare)))
            .Where(p => p.Hours > 0)
            .ToList();
}
