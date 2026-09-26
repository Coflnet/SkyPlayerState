using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;

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

    private MethodBreakdown BuildBreakdown() => NewGuidanceBreakdown("Island", "player_data");

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        var locations = parameters.LocationProfit
            .Where(l => SkyblockZones.Matches(locationNames, l.Key))
            .Select(l => (data: l.Value,
                totalProfit: l.Value.Sum(l => l.Profit),
                totalTime: TimeSpan.FromHours(l.Value.Sum(l => (l.EndTime - l.StartTime).TotalHours)),
                perHour: l.Value.Sum(l => l.Profit) / l.Value.Sum(l => (l.EndTime - l.StartTime).TotalHours)))
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
        var items = locations.SelectMany(l=>l.data).SelectMany(i => i.ItemsCollected)
            .GroupBy(i => i.Key, i => i.Value)
            .ToDictionary(g => g.Key, g => g.Sum())
            .OrderByDescending(i => i.Value);
        var itemBreakDown = items
            .Take(20)
            .Select(i => $"{McColorCodes.YELLOW}{i.Key} {McColorCodes.GRAY}x{i.Value}")
            .Aggregate((a, b) => a + "\n" + b);
        var totalProfit = locations.Sum(l => l.totalProfit);
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
            Breakdown = BuildBreakdown()
        });
    }

    public override string Description => $"Calculates profit while being on {RegionName} island";
}