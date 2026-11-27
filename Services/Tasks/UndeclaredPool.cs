using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The undeclared pool of a task: expensive, infrequent gains the task does not declare as a drop (see
/// <see cref="TaskPeriodFolder.UndeclaredExpensive"/>). Nothing of it is lost, but one player's purchases or gear never
/// move a public estimate:
/// <list type="bullet">
/// <item>a tag only counts (as a rare drop) once <see cref="PromotionPlayers"/> distinct players found at least
/// <see cref="MinPlayerCoins"/> of it in the task (all buckets, the 8 day window, ramp weighted and decayed);</item>
/// <item>a promoted tag counts each player's coins only up to the second largest player's coins of that tag, so one
/// player's volume (26 bought books next to two found ones) can not carry the rate.</item>
/// </list>
/// The coins are stored per tag and player in a <c>map&lt;text, double&gt;</c> keyed <c>TAG|player</c>
/// (<see cref="TaskBucketAggregateRow.UndeclaredPlayerCoins"/>).
/// </summary>
public static class UndeclaredPool
{
    /// <summary>Distinct players of a task (all buckets, the aggregate window) that promote an undeclared tag.</summary>
    public const int PromotionPlayers = 3;
    /// <summary>
    /// The (ramp weighted, decayed) coins of a tag a player needs to count toward <see cref="PromotionPlayers"/>: one rare
    /// candidate's worth. A contribution fades below it as its day row ages.
    /// </summary>
    public const double MinPlayerCoins = 1_000_000;
    /// <summary>
    /// Players kept per tag and day row, those with the most coins. At least <see cref="PromotionPlayers"/>, so a capped
    /// row alone can still promote; the coins of the others are kept as <see cref="TaskBucketAggregateRow.UndeclaredTrimmedCoins"/>.
    /// </summary>
    internal const int PlayersPerTag = 5;
    /// <summary>Undeclared tags kept per day row, like <see cref="TaskAggregateService.RareItemLimit"/>.</summary>
    internal const int TagLimit = TaskAggregateService.RareItemLimit;
    private const char Separator = '|';
    private const string UnknownPlayer = "unknown";

    internal static string PlayerEntry(string tag, string player) => tag + Separator + (player ?? UnknownPlayer);

    /// <summary>The (tag, player) of a stored entry, null for a malformed one.</summary>
    internal static (string Tag, string Player)? ParseEntry(string entry)
    {
        var at = entry?.LastIndexOf(Separator) ?? -1;
        if (at <= 0 || at == entry.Length - 1)
            return null;
        return (entry[..at], entry[(at + 1)..]);
    }

    /// <summary>Adds the positive, finite <paramref name="coins"/> (per tag) of <paramref name="player"/> to <paramref name="poolCoins"/>.</summary>
    internal static void Add(Dictionary<string, double> poolCoins, Dictionary<string, double> coins, string player)
    {
        foreach (var (tag, value) in (coins ?? []).Where(e => e.Value > 0 && double.IsFinite(e.Value)))
        {
            var entry = PlayerEntry(tag, player);
            poolCoins[entry] = poolCoins.GetValueOrDefault(entry) + value;
        }
    }

    // ── day rows ──

    /// <summary>Adds <paramref name="existing"/>'s pool to <paramref name="row"/>. Rows written before the pool existed have none.</summary>
    internal static void AddExisting(TaskBucketAggregateRow row, TaskBucketAggregateRow existing)
    {
        row.UndeclaredPlayerCoins ??= new();
        foreach (var (entry, coins) in existing.UndeclaredPlayerCoins ?? [])
            row.UndeclaredPlayerCoins[entry] = row.UndeclaredPlayerCoins.GetValueOrDefault(entry) + coins;
        row.UndeclaredTrimmedCoins = (row.UndeclaredTrimmedCoins ?? 0) + (existing.UndeclaredTrimmedCoins ?? 0);
    }

    /// <summary>
    /// Bounds a day row: the <see cref="TagLimit"/> tags found by the most players (then the most coins), each with its
    /// <see cref="PlayersPerTag"/> players of the most coins. What is cut off is added to
    /// <see cref="TaskBucketAggregateRow.UndeclaredTrimmedCoins"/>, never counted in a rate but not lost either.
    /// </summary>
    internal static void Trim(TaskBucketAggregateRow row)
    {
        if (row.UndeclaredPlayerCoins == null || row.UndeclaredPlayerCoins.Count == 0)
            return;
        var kept = ByTag(row.UndeclaredPlayerCoins)
            .OrderByDescending(g => g.Value.Count).ThenByDescending(g => g.Value.Values.Sum())
            .Take(TagLimit)
            .SelectMany(g => g.Value.OrderByDescending(p => p.Value).Take(PlayersPerTag)
                .Select(p => (Entry: PlayerEntry(g.Key, p.Key), Coins: p.Value)))
            .ToDictionary(e => e.Entry, e => e.Coins);
        var trimmed = row.UndeclaredPlayerCoins.Where(e => !kept.ContainsKey(e.Key)).Sum(e => e.Value);
        row.UndeclaredPlayerCoins = kept;
        row.UndeclaredTrimmedCoins = (row.UndeclaredTrimmedCoins ?? 0) + trimmed;
    }

    /// <summary>The well formed entries of a stored map as tag -> player -> coins.</summary>
    private static Dictionary<string, Dictionary<string, double>> ByTag(Dictionary<string, double> entries, double weight = 1)
    {
        var result = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (entry, coins) in entries)
        {
            if (ParseEntry(entry) is not { } parsed)
                continue;
            AddCoins(result, parsed.Tag, parsed.Player, coins * weight);
        }
        return result;
    }

    private static void AddCoins(Dictionary<string, Dictionary<string, double>> target, string tag, string player, double coins)
    {
        if (!target.TryGetValue(tag, out var players))
            target[tag] = players = new();
        players[player] = players.GetValueOrDefault(player) + coins;
    }

    // ── merged aggregates ──

    /// <summary>Adds a day row's pool, scaled by its decay <paramref name="weight"/>, to the merged aggregate.</summary>
    internal static void AddRow(BucketAggregate agg, TaskBucketAggregateRow row, double weight)
    {
        foreach (var (tag, players) in ByTag(row.UndeclaredPlayerCoins ?? [], weight))
            AddPlayers(agg.UndeclaredPlayerCoins, tag, players);
        agg.UndeclaredTrimmedCoins += (row.UndeclaredTrimmedCoins ?? 0) * weight;
    }

    /// <summary>Adds <paramref name="other"/>'s pool, promotion included, to <paramref name="merged"/> (all buckets of a task).</summary>
    internal static void Merge(BucketAggregate merged, BucketAggregate other)
    {
        foreach (var (tag, players) in other.UndeclaredPlayerCoins)
            AddPlayers(merged.UndeclaredPlayerCoins, tag, players);
        merged.UndeclaredTrimmedCoins += other.UndeclaredTrimmedCoins;
        merged.PromotedUndeclared.UnionWith(other.PromotedUndeclared);
        foreach (var (tag, coins) in other.PromotedUndeclaredItemCoins)
            merged.PromotedUndeclaredItemCoins[tag] = merged.PromotedUndeclaredItemCoins.GetValueOrDefault(tag) + coins;
    }

    private static void AddPlayers(Dictionary<string, Dictionary<string, double>> target, string tag,
        Dictionary<string, double> players)
    {
        foreach (var (player, coins) in players)
            AddCoins(target, tag, player, coins);
    }

    /// <summary>Coins per player and undeclared tag over <paramref name="buckets"/> (the buckets of one task).</summary>
    internal static Dictionary<string, Dictionary<string, double>> TaskPlayerCoins(IEnumerable<BucketAggregate> buckets)
    {
        var result = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (tag, players) in buckets.SelectMany(agg => agg.UndeclaredPlayerCoins))
            AddPlayers(result, tag, players);
        return result;
    }

    /// <summary>
    /// Players of a tag whose coins count toward promotion (at least <see cref="MinPlayerCoins"/>). Contributions
    /// without a player are kept under one "unknown" entry, which is no independent evidence.
    /// </summary>
    internal static int EvidencePlayers(Dictionary<string, double> playerCoins)
        => playerCoins.Count(p => p.Key != UnknownPlayer && p.Value >= MinPlayerCoins);

    /// <summary>
    /// The share of each player's coins a promoted tag counts, per promoted tag: 1 up to the second largest player's
    /// coins, less above it. Tags without <see cref="PromotionPlayers"/> evidence players are not in the result.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, double>> PromotionShares(
        Dictionary<string, Dictionary<string, double>> taskPlayerCoins)
        => taskPlayerCoins.Where(e => EvidencePlayers(e.Value) >= PromotionPlayers)
            .ToDictionary(e => e.Key, e => CappedShares(e.Value));

    private static Dictionary<string, double> CappedShares(Dictionary<string, double> playerCoins)
    {
        var cap = playerCoins.Values.OrderByDescending(c => c).Skip(1).First();
        return playerCoins.ToDictionary(e => e.Key, e => e.Value > 0 ? Math.Min(e.Value, cap) / e.Value : 0);
    }

    /// <summary>
    /// Sets every bucket's <see cref="BucketAggregate.PromotedUndeclared"/> to its task's promoted tags and its
    /// <see cref="BucketAggregate.PromotedUndeclaredItemCoins"/> to its players' counted share of them, so the buckets of a
    /// task add up to the task wide capped pool.
    /// </summary>
    internal static void PromoteAll(Dictionary<(string task, byte bucket), BucketAggregate> snapshot)
    {
        foreach (var task in snapshot.GroupBy(e => e.Key.task, e => e.Value, StringComparer.OrdinalIgnoreCase))
        {
            var shares = PromotionShares(TaskPlayerCoins(task));
            foreach (var agg in task)
                Promote(agg, shares);
        }
    }

    private static void Promote(BucketAggregate agg, Dictionary<string, Dictionary<string, double>> shares)
    {
        agg.PromotedUndeclared = shares.Keys.ToHashSet();
        agg.PromotedUndeclaredItemCoins = shares
            .Where(e => agg.UndeclaredPlayerCoins.ContainsKey(e.Key))
            .ToDictionary(e => e.Key, e => CountedCoins(agg.UndeclaredPlayerCoins[e.Key], e.Value));
    }

    private static double CountedCoins(Dictionary<string, double> playerCoins, Dictionary<string, double> shares)
        => playerCoins.Sum(p => p.Value * shares.GetValueOrDefault(p.Key));

    /// <summary>The promoted undeclared tags of <paramref name="task"/> in <paramref name="snapshot"/>, empty without data.</summary>
    internal static HashSet<string> PromotedOf(Dictionary<(string task, byte bucket), BucketAggregate> snapshot, string task)
    {
        for (byte b = 0; b < StatScoreService.BucketCount; b++)
            if (snapshot.TryGetValue((task, b), out var agg))
                return agg.PromotedUndeclared;
        return new();
    }
}
