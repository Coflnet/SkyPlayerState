using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>A modifier change measured over players, unrounded (see <see cref="GearClusterPublisher"/>).</summary>
public record MeasuredGearEffect(string Id, IReadOnlyList<GearKeyChange> Changes, int Players, int Pairs, double Ratio);

/// <summary>
/// Within-player paired deltas (pure). A player's observations of one task, in time order, form periods: runs of the
/// same finest setup key (L3, L2 for combat categories; unkeyed observations are skipped). Two ADJACENT periods with at
/// least <see cref="GearClusterOptions.MinHoursPerSide"/> each give the ratio of the player's common rates (after /
/// before, each side from its own period only), which measures the change without the skill difference between players.
/// An A → B → A sequence gives A → B and B → A.
/// Changes are aggregated by what differs (so "harvesting 5 -&gt; 6" pools players with otherwise different setups) in a
/// canonical direction (an inverse change counts with the inverted ratio); counters never count as a change. Each player
/// counts once per change (geometric mean of their ratios); the published ratio is the median over at least
/// <see cref="GearClusterOptions.MinPlayers"/> players.
/// </summary>
public static class GearModifierEffects
{
    private sealed record Period(GearSetupKey Key, double Hours, double Common)
    {
        public double Rate => Common / Hours;
    }

    private sealed record Transition(string Player, string Id, List<GearKeyChange> Changes, double Ratio);

    public static List<MeasuredGearEffect> Derive(IEnumerable<GearClusterInput> inputs, string? category, GearClusterOptions? options = null)
    {
        options ??= GearClusterOptions.Default;
        var index = GearSetupKeys.FinestLevel(category) - 1;
        var transitions = inputs.GroupBy(i => i.Player, StringComparer.Ordinal)
            .SelectMany(g => PlayerTransitions(g.Key, g, index, options.MinHoursPerSide))
            .ToList();
        return transitions.GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(Aggregate)
            .Where(e => e.Players >= options.MinPlayers)
            .OrderByDescending(e => e.Players).ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static MeasuredGearEffect Aggregate(IGrouping<string, Transition> group)
    {
        var perPlayer = group.GroupBy(t => t.Player, StringComparer.Ordinal)
            .Select(p => Math.Exp(p.Average(t => Math.Log(t.Ratio))))
            .ToList();
        return new MeasuredGearEffect(group.Key, group.First().Changes, perPlayer.Count, group.Count(), GearStatistics.Median(perPlayer));
    }

    private static IEnumerable<Transition> PlayerTransitions(string player, IEnumerable<GearClusterInput> observations, int index, double minHours)
    {
        var periods = Periods(observations, index);
        for (var i = 1; i < periods.Count; i++)
        {
            var (before, after) = (periods[i - 1], periods[i]);
            if (before.Hours < minHours || after.Hours < minHours || before.Rate <= 0 || after.Rate <= 0)
                continue;
            var changes = Diff(before.Key, after.Key);
            if (changes.Count > 0)
                yield return Canonical(player, changes, after.Rate / before.Rate);
        }
    }

    /// <summary>Runs of the same key in time order, each with its own hours and common coins.</summary>
    private static List<Period> Periods(IEnumerable<GearClusterInput> observations, int index)
    {
        var periods = new List<Period>();
        foreach (var observation in observations.OrderBy(o => o.Observation.FoldedAt))
        {
            var key = observation.Keys[index];
            if (key == null)
                continue;
            var last = periods.Count > 0 ? periods[^1] : null;
            var current = last != null && last.Key.Text == key.Text ? last : new Period(key, 0, 0);
            var extended = current with { Hours = current.Hours + observation.Hours, Common = current.Common + observation.Observation.CommonCoins };
            if (ReferenceEquals(current, last))
                periods[^1] = extended;
            else
                periods.Add(extended);
        }
        return periods;
    }

    /// <summary>The parts that differ, by component name; earned counters (<c>*.counters.*</c>) are not a change.</summary>
    public static List<GearKeyChange> Diff(GearSetupKey before, GearSetupKey after)
        => before.Parts.Keys.Union(after.Parts.Keys)
            .Where(k => !k.Contains(".counters.", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new GearKeyChange { Component = k, From = before.Parts.GetValueOrDefault(k), To = after.Parts.GetValueOrDefault(k) })
            .Where(c => c.From != c.To)
            .ToList();

    /// <summary>
    /// The direction whose first change goes up (absent &lt; numbers ascending &lt; text): an upgrade and its downgrade
    /// share one id, the downgrade with the inverted ratio.
    /// </summary>
    private static Transition Canonical(string player, List<GearKeyChange> changes, double ratio)
    {
        var first = changes[0];
        if (CompareValues(first.From, first.To) > 0)
        {
            changes = changes.Select(c => new GearKeyChange { Component = c.Component, From = c.To, To = c.From }).ToList();
            ratio = 1 / ratio;
        }
        var id = string.Join("|", changes.Select(c => $"{c.Component}:{c.From ?? "-"}>{c.To ?? "-"}"));
        return new Transition(player, id, changes, ratio);
    }

    private static int CompareValues(string? a, string? b)
    {
        if (a == null && b == null)
            return 0;
        if (a == null)
            return -1;
        if (b == null)
            return 1;
        var aNumber = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x);
        var bNumber = double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y);
        if (aNumber && bNumber)
            return x.CompareTo(y);
        if (aNumber != bNumber)
            return aNumber ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }
}
