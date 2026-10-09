using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public abstract class IslandTask : ProfitTask
{
    protected abstract string RegionName { get; }
    protected abstract HashSet<string> locationNames { get; }

    /// <summary>Every IslandTask's "where" is the first of its own locations (an island-wide tracker).</summary>
    protected override string Where => locationNames.FirstOrDefault();
    /// <summary>No per-task wiki curated separately - the island's own wiki page (with its map) covers it.</summary>
    protected override string WikiUrl => WhereWikiUrl;

    public virtual bool IsPossibleAt(DateTime time)
    {
        // Default implementation assumes the task is always possible
        return true;
    }

    /// <summary>
    /// Generic followable steps for an island-wide activity tracker: there is no single "method",
    /// so this just points the player at the island and lets the automatic tracking do the rest.
    /// </summary>
    protected override List<TaskStep> Steps
    {
        get
        {
            var steps = new List<TaskStep>();
            void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });
            var warp = EffectiveWarpCommand;
            if (warp != null)
                Add($"Type {warp} to get close.", warp);
            Add($"Go do anything profitable on {RegionName} - mining, farming, killing mobs, fishing, whatever this island is for.", WhereWikiUrl);
            Add("We automatically add up the coins from everything you collect while you're there.");
            Add("Check /cofl task again after a while to see your real coins/hour.");
            return steps;
        }
    }

    /// <summary>True when <paramref name="location"/> is one of this tracker's zones.</summary>
    public bool CoversLocation(string location) => SkyblockZones.Matches(locationNames, location);
    /// <summary>Number of zones tracked, smaller = more specific.</summary>
    public int LocationCount => locationNames.Count;

    private MethodBreakdown BuildBreakdown() => NewGuidanceBreakdown("Island", "player_data");

    /// <summary>
    /// Breakdown with the <see cref="ProfitTask.LateReward"/> items as labelled drops (see
    /// <see cref="ApplyLateReward"/> on method tasks): a client can show the contest coupons as their own part.
    /// </summary>
    private MethodBreakdown BuildBreakdown(List<Period> latePeriods, double workHours, TaskParams parameters)
    {
        var breakdown = BuildBreakdown();
        var spec = LateReward;
        if (spec == null || latePeriods.Count == 0 || workHours <= 0)
            return breakdown;
        breakdown.TrackedHours = workHours;
        breakdown.Drops = [];
        foreach (var group in latePeriods.SelectMany(p => p.ItemsCollected).Where(i => i.Value > 0 && spec.Tags.Contains(i.Key))
                     .GroupBy(i => i.Key, i => i.Value))
        {
            var price = parameters.CleanPrices?.GetValueOrDefault(group.Key) ?? 0;
            var perHour = group.Sum() / workHours;
            breakdown.Drops.Add(new DropInfo
            {
                ItemTag = group.Key,
                Name = parameters.Names?.GetValueOrDefault(group.Key) ?? group.Key,
                RatePerHour = perHour,
                PriceEach = price,
                ContributionPerHour = perHour * price,
                Kind = LateRewardSpec.DropKind,
                Label = spec.Label
            });
        }
        breakdown.LateRewardPerHour = breakdown.Drops.Sum(d => d.ContributionPerHour);
        breakdown.LateRewardLabel = spec.Label;
        return breakdown;
    }

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        // reward-only periods the listener credited to this tracker: value without work time
        var latePeriods = LateReward == null ? []
            : parameters.LocationProfit.Values.SelectMany(v => v).Where(IsAttributedLateReward).ToList();
        var locations = parameters.LocationProfit
            .Select(l => (key: l.Key, periods: l.Value.Where(p => !latePeriods.Contains(p)).ToArray()))
            .Where(l => l.periods.Length > 0 && SkyblockZones.Matches(locationNames, l.key))
            .Select(l => (data: l.periods,
                totalProfit: l.periods.Sum(l => l.Profit),
                totalTime: TimeSpan.FromHours(l.periods.Sum(l => (l.EndTime - l.StartTime).TotalHours)),
                perHour: l.periods.Sum(l => l.Profit) / l.periods.Sum(l => (l.EndTime - l.StartTime).TotalHours)))
            .OrderByDescending(l => l.totalTime < TimeSpan.FromMinutes(1) ? l.perHour / 100 : l.perHour)
            .ToList();
        if (locations.Count == 0)
        {
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"No {Name} activity tracked so far.",
                Details = $"Please do {RegionName} island \nand do some activities \nso we can calculate the profitability.",
                OnClick = EffectiveWarpCommand,
                Breakdown = BuildBreakdown()
            });
        }
        if (!IsPossibleAt(parameters.TestTime))
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"The {RegionName} island/task is not possible at this time.",
                Details = "Its time locked in some way",
                OnClick = EffectiveWarpCommand,
                Breakdown = BuildBreakdown()
            });
        var bestLocation = locations.First();
        var totalTime = locations.Sum(l => l.data.Sum(d => (d.EndTime - d.StartTime).TotalHours));
        var fmt = parameters.Formatter;
        var formattedDuration = fmt.FormatTime(TimeSpan.FromHours(totalTime));
        var items = locations.SelectMany(l=>l.data).Concat(latePeriods).SelectMany(i => i.ItemsCollected)
            .GroupBy(i => i.Key, i => i.Value)
            .ToDictionary(g => g.Key, g => g.Sum())
            .OrderByDescending(i => i.Value);
        var itemBreakDown = items
            .Take(20)
            .Select(i => $"{McColorCodes.YELLOW}{i.Key} {McColorCodes.GRAY}x{i.Value}")
            .Aggregate((a, b) => a + "\n" + b);
        var totalProfit = locations.Sum(l => l.totalProfit) + latePeriods.Sum(p => p.Profit);
        var perHour = totalProfit / totalTime;
        var itemCount = items.Where(i => i.Value > 0).Sum(i => i.Value);
        return Task.FromResult(new TaskResult
        {
            ProfitPerHour = (int)perHour,
            Message = $"{Name} with {McColorCodes.AQUA}{fmt.FormatPrice(totalProfit)} {McColorCodes.GRAY}with {McColorCodes.GREEN}{itemCount} items {McColorCodes.GRAY}over {formattedDuration}.",
            Details = $"Total locations considered: {locations.Count}\n" +
                      $"Time tracked: {formattedDuration}\n"
                      + $"Items collected:\n{itemBreakDown}",
            OnClick = EffectiveWarpCommand,
            Breakdown = BuildBreakdown(latePeriods, totalTime, parameters)
        });
    }

    public override string Description => $"Calculates profit while being on {RegionName} island";
}