using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// Gear setup estimates for every bound task. Recomputed whenever <see cref="TaskEstimator.GetCommunityEstimates"/>
/// hands out a new (refreshed) list, so it never runs more often than the community estimates themselves.
/// </summary>
public class GearSetupService
{
    private readonly TaskEstimator estimator;
    private readonly TaskAggregateService aggregates;
    private readonly TaskPriceService prices;
    private readonly TaskRegistry registry;
    private readonly GearProfileCatalog catalog;
    private readonly ILogger<GearSetupService> logger;
    private volatile CachedResult? cached;

    private sealed record CachedResult(List<TaskEstimate> Source, List<TaskGearSetups> Setups,
        Dictionary<string, TaskGearSetups> ByTask);

    public GearSetupService(TaskEstimator estimator, TaskAggregateService aggregates, TaskPriceService prices,
        TaskRegistry registry, ILogger<GearSetupService> logger)
        : this(estimator, aggregates, prices, registry, GearProfileCatalog.Embedded, logger)
    {
    }

    internal GearSetupService(TaskEstimator estimator, TaskAggregateService aggregates, TaskPriceService prices,
        TaskRegistry registry, GearProfileCatalog catalog, ILogger<GearSetupService> logger)
    {
        this.estimator = estimator;
        this.aggregates = aggregates;
        this.prices = prices;
        this.registry = registry;
        this.catalog = catalog;
        this.logger = logger;
        foreach (var error in catalog.Errors.Concat(GearProfileValidator.RegistryErrors(catalog, registry)))
            logger.LogError("gear profile skipped: {error}", error);
    }

    /// <summary>Every bound task's setups, best community rate first.</summary>
    public async Task<List<TaskGearSetups>> GetAll(CancellationToken cancellationToken = default)
        => (await GetCurrent(cancellationToken)).Setups;

    /// <summary>The setups of one task, null when no profile is bound to it.</summary>
    public async Task<TaskGearSetups?> Get(string taskName, CancellationToken cancellationToken = default)
        => (await GetCurrent(cancellationToken)).ByTask.GetValueOrDefault(taskName);

    /// <summary>Whether <paramref name="taskName"/> is a public method task at all (for 404 slugs).</summary>
    public bool IsKnownTask(string taskName) => GearProfileValidator.IsPublicTask(registry, taskName);

    private async Task<CachedResult> GetCurrent(CancellationToken cancellationToken)
    {
        var priceLookup = await prices.GetPrices(cancellationToken);
        var community = await estimator.GetCommunityEstimates(priceLookup, cancellationToken);
        var current = cached;
        if (current != null && ReferenceEquals(current.Source, community))
            return current;
        var setups = Compute(community, aggregates.GetSnapshot(), priceLookup);
        current = new CachedResult(community, setups,
            setups.ToDictionary(s => s.TaskName, StringComparer.OrdinalIgnoreCase));
        cached = current;
        return current;
    }

    internal List<TaskGearSetups> Compute(List<TaskEstimate> community,
        Dictionary<(string, byte), BucketAggregate> snapshot, Dictionary<string, double> priceLookup)
    {
        var byName = community.GroupBy(e => e.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<TaskGearSetups>();
        foreach (var bound in catalog.Bindings)
        {
            var setups = TryEstimate(bound, byName, snapshot, priceLookup);
            if (setups != null)
                result.Add(setups);
        }
        return result.OrderByDescending(s => s.CommunityCoinsPerHour).ThenBy(s => s.TaskName, StringComparer.Ordinal).ToList();
    }

    private TaskGearSetups? TryEstimate(BoundGearProfile bound, Dictionary<string, TaskEstimate> community,
        Dictionary<(string, byte), BucketAggregate> snapshot, Dictionary<string, double> priceLookup)
    {
        if (registry.GetByName(bound.Binding.Task) is not MethodTask { IsHidden: false } task)
            return null; // logged once in the constructor
        try
        {
            var name = TaskEstimator.GetTaskName(task);
            var shares = RevenueShares.FromAggregate(task, TaskEstimator.MergeBuckets(snapshot, name), priceLookup,
                bound.Binding.RareDropTags);
            var input = new GearEstimateInput(name, GearBindingResolver.Resolve(bound.Profile, bound.Binding),
                community.GetValueOrDefault(name), shares, community.GetValueOrDefault, priceLookup);
            return GearSetupEstimator.Estimate(input);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to estimate gear setups of {task}", bound.Binding.Task);
            return null;
        }
    }
}
