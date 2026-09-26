using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Builds <see cref="TaskParams"/> from local state and runs every registered task's
/// <see cref="ProfitTask.Execute"/> for one player, entirely in-process - the port of what
/// SkyModCommands' TaskCommand.BuildParameters/TaskService.ExecuteAll and
/// Controllers/TaskController.GetTaskResults used to do over HTTP against this same service.
/// Server side, <see cref="TaskParams.ServerEstimates"/> (via <see cref="TaskEstimator"/>) is
/// computed in-process instead of round tripping through this service's own REST API.
/// </summary>
public class TaskExecutionService
{
    private readonly IPersistenceService persistence;
    private readonly TrackedProfitService profitService;
    private readonly TaskPriceService priceService;
    private readonly TaskEstimator estimator;
    private readonly CurrentMayorService mayorService;
    private readonly TaskRegistry registry;
    private readonly IItemsApi itemsApi;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<TaskExecutionService> logger;

    private static readonly TimeSpan NamesCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OptionalReadTimeout = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Guard for the player-specific reads in <see cref="LoadStateAndHistory"/> and
    /// <see cref="BuildParameters"/> (state, history, mayor) - mirrors the ~1s guard
    /// <see cref="Controllers.TaskController.GetEstimates"/> already applies to its own state read.
    /// </summary>
    private static readonly TimeSpan PlayerDataReadTimeout = TimeSpan.FromSeconds(1);
    /// <summary>Guard for <see cref="TaskEstimator.EstimateAll"/>, which computes every registered task.</summary>
    private static readonly TimeSpan EstimatesReadTimeout = TimeSpan.FromSeconds(5);
    private Dictionary<string, string> cachedNames;
    private DateTime namesFetchedAt = DateTime.MinValue;
    private readonly SemaphoreSlim namesRefreshLock = new(1, 1);

    public TaskExecutionService(IPersistenceService persistence, TrackedProfitService profitService,
        TaskPriceService priceService, TaskEstimator estimator, CurrentMayorService mayorService,
        TaskRegistry registry, IItemsApi itemsApi, IServiceProvider serviceProvider,
        ILogger<TaskExecutionService> logger)
    {
        this.persistence = persistence;
        this.profitService = profitService;
        this.priceService = priceService;
        this.estimator = estimator;
        this.mayorService = mayorService;
        this.registry = registry;
        this.itemsApi = itemsApi;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Executes every registered task for <paramref name="playerId"/> and returns all results,
    /// best profit/hour first (accessible tasks before inaccessible ones).
    /// </summary>
    public async Task<List<TaskResult>> ExecuteAll(string playerId, CancellationToken cancellationToken = default)
    {
        var parameters = await BuildParameters(playerId, cancellationToken);
        return await ExecuteAll(parameters);
    }

    /// <summary>
    /// Core loop: runs every registered task against an already-built <see cref="TaskParams"/> and
    /// returns all results, best profit/hour first (accessible tasks before inaccessible ones). Split
    /// out from the <c>playerId</c> overload so it can be unit tested against a
    /// hand-built <see cref="TaskParams"/> without needing every upstream dependency (persistence,
    /// prices, estimator, ...) wired up.
    /// </summary>
    public async Task<List<TaskResult>> ExecuteAll(TaskParams parameters)
    {
        var all = await Task.WhenAll(registry.Tasks.Select(task => ExecuteSafe(task, parameters)));
        return all
            .OrderBy(r => r.IsAccessible ? 0 : 1)
            .ThenByDescending(r => r.ProfitPerHour)
            .ToList();
    }

    /// <summary>
    /// Executes a single registered task (looked up the same way the classifier/estimator name
    /// tasks - <see cref="TaskRegistry.GetByName"/>) for <paramref name="playerId"/>. Returns null
    /// when no task is registered under that name.
    /// </summary>
    public async Task<TaskResult> ExecuteOne(string playerId, string taskName, CancellationToken cancellationToken = default)
    {
        var task = registry.GetByName(taskName);
        if (task == null)
            return null;
        var parameters = await BuildParameters(playerId, cancellationToken);
        return await ExecuteSafe(task, parameters);
    }

    /// <summary>Same lookup/execute as the <c>playerId</c> overload, against an already-built <see cref="TaskParams"/>.</summary>
    public Task<TaskResult> ExecuteOne(TaskParams parameters, string taskName)
    {
        var task = registry.GetByName(taskName);
        return task == null ? Task.FromResult<TaskResult>(null) : ExecuteSafe(task, parameters);
    }

    private static async Task<TaskResult> ExecuteSafe(ProfitTask task, TaskParams parameters)
    {
        try
        {
            var result = await task.Execute(parameters);
            result.Name ??= task.Name;
            return result;
        }
        catch (Exception e)
        {
            return new TaskResult
            {
                ProfitPerHour = 0,
                Name = task.Name,
                Message = $"Error calculating {task.Name}",
                Details = e.ToString()
            };
        }
    }

    private async Task<TaskParams> BuildParameters(string playerId, CancellationToken cancellationToken)
    {
        var pricesTask = priceService.GetPrices(cancellationToken);
        var bazaarPricesTask = priceService.GetBazaarPrices(cancellationToken);
        var namesTask = GetNames(cancellationToken);
        // GetCurrentMayor neither times out nor honours a cancellation token internally (it only
        // guards against the mayor API returning an error, not against it hanging) - guard it here
        // the same way as the state/history reads below instead of letting a stuck mayor API call
        // hang the whole request.
        var mayorTask = LoadOptional(() => mayorService.GetCurrentMayor(), (string)null,
            "current mayor", playerId, PlayerDataReadTimeout, cancellationToken);

        // State is keyed by name and history by uuid (see LoadStateAndHistory), so history can only
        // be requested once the state (and thus the uuid to resolve it from) has loaded - it cannot
        // be kicked off eagerly alongside the tasks above the way it used to be.
        var (state, history, resolvedId) = await LoadStateAndHistory(playerId, cancellationToken);

        var prices = await pricesTask;
        var bazaarPrices = await bazaarPricesTask;
        var names = await namesTask;
        var mayor = await mayorTask;

        var locationProfit = history
            .Where(p => p.EndTime - p.StartTime < TimeSpan.FromHours(1))
            .GroupBy(p => p.Location)
            .ToDictionary(g => g.Key, g => g.ToArray());

        // Stat aware, saturation adjusted estimates - this is the community tier, computed
        // in-process instead of over this service's own REST api (see GetEstimates). Guarded like
        // the reads above: EstimateAll already swallows failures in its own sub-fetches internally,
        // but this is a defense-in-depth backstop so a bug or an unexpectedly slow task estimate
        // cannot fail/hang the whole /results request - callers degrade to formula/no-data tasks
        // instead (see MethodTask.Execute).
        var serverEstimates = await LoadOptional(() => estimator.EstimateAll(state, prices, cancellationToken),
            new List<TaskEstimate>(), "task estimates", resolvedId, EstimatesReadTimeout, cancellationToken);

        return new TaskParams
        {
            TestTime = DateTime.UtcNow,
            ExtractedInfo = state?.ExtractedInfo ?? new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            CleanPrices = prices.ToDictionary(p => p.Key, p => (long)p.Value),
            BazaarPrices = bazaarPrices,
            Names = names,
            LocationProfit = locationProfit,
            MaxAvailableCoins = state?.ExtractedInfo?.Purse > 0 ? state.ExtractedInfo.Purse : 1_000_000_000,
            CurrentMayor = mayor,
            // Superseded by ServerEstimates: unlike SkyModCommands' in-memory-only global averages
            // (reset on every restart, not shared across replicas), this service's TaskAggregateService
            // already persists community data in Cassandra and TaskEstimator blends it above.
            GlobalAverageDrops = null,
            ServerEstimates = serverEstimates.ToDictionary(e => e.TaskName, e => e, StringComparer.OrdinalIgnoreCase),
            ServiceProvider = serviceProvider,
            PlayerUuid = resolvedId,
            PlayerName = playerId
        };
    }

    /// <summary>
    /// Loads the player state (keyed by NAME - see <see cref="IPersistenceService.GetStateObject"/>)
    /// and the tracked profit history (keyed by UUID - see
    /// <see cref="TrackedProfitService.GetHistoryForPlayer"/>) for one player, resolving the id used
    /// for the history lookup (and <see cref="TaskParams.PlayerUuid"/>) from the loaded state's
    /// <see cref="McInfo.Uuid"/> rather than reusing <paramref name="playerId"/> for both - state and
    /// history are keyed differently, so using the same id for both (as this used to) leaves the
    /// state lookup fine but silently queries history under the wrong id whenever the two differ.
    /// <paramref name="playerId"/> is normally the player's name (matching how every caller - both
    /// SkyModCommands' TaskClaimCommand and the update pipeline itself - identifies players). Called
    /// with a uuid instead (the shape SkyModCommands' task commands used to send), the state lookup
    /// still runs (there is no uuid-&gt;state path to prefer instead - see <see cref="PersistenceService"/>
    /// - so it comes back empty), and that same uuid is used directly for history/PlayerUuid since
    /// the empty state carries none of its own. Exposed (not private) so
    /// <c>TaskExecutionService.Tests.cs</c> can verify this resolution against a mocked
    /// <see cref="IPersistenceService"/>/<see cref="TrackedProfitService"/> without needing the
    /// prices/estimator/mayor dependencies <see cref="BuildParameters"/> also wires up.
    /// </summary>
    internal async Task<(StateObject State, List<TrackedProfitService.Period> History, string ResolvedId)> LoadStateAndHistory(
        string playerId, CancellationToken cancellationToken)
    {
        // Guarded like TaskController.GetEstimates/LoadState: a thrown or hanging read must not
        // fail/hang the whole /results request - degrade to an empty ExtractedInfo (state) or empty
        // LocationProfit (history) instead. The call itself (not just the await) has to happen
        // inside LoadOptional's try, so a mock (or a real dependency) that throws synchronously is
        // caught the same as one that returns a faulted/never-completing task.
        var state = await LoadOptional(() => persistence.GetStateObject(playerId), (StateObject)null,
            "player state", playerId, PlayerDataReadTimeout, cancellationToken);
        var resolvedId = state?.McInfo?.Uuid != null && state.McInfo.Uuid != Guid.Empty
            ? state.McInfo.Uuid.ToString("N") : playerId;
        var history = await LoadOptional(() => profitService.GetHistoryForPlayer(resolvedId, DateTime.UtcNow, 300),
            new List<TrackedProfitService.Period>(), "task history", resolvedId, PlayerDataReadTimeout, cancellationToken);
        return (state, history, resolvedId);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with a bounded timeout, falling back to
    /// <paramref name="fallback"/> and logging a warning instead of throwing/hanging on a timeout or
    /// any other failure - honours <paramref name="cancellationToken"/> (a client disconnect still
    /// cancels the request rather than being swallowed as an optional-read failure). Mirrors the
    /// pattern already used by <see cref="Controllers.TaskController.LoadState"/> and
    /// <see cref="TaskEstimator.LoadPlayerStats"/>, generalized here since <see cref="BuildParameters"/>
    /// needs it for several independent optional reads (state, history, mayor, estimates).
    /// </summary>
    private async Task<T> LoadOptional<T>(Func<Task<T>> operation, T fallback, string name, string playerId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            logger.LogWarning(e, "{name} timed out for {player}; continuing without it", name, playerId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{name} unavailable for {player}; continuing without it", name, playerId);
        }
        return fallback;
    }

    private async Task<Dictionary<string, string>> GetNames(CancellationToken cancellationToken)
    {
        if (cachedNames != null && DateTime.UtcNow - namesFetchedAt < NamesCacheDuration)
            return cachedNames;
        if (!await namesRefreshLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            return cachedNames ?? new();
        try
        {
            if (cachedNames != null && DateTime.UtcNow - namesFetchedAt < NamesCacheDuration)
                return cachedNames;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(OptionalReadTimeout);
            var names = await itemsApi.ItemNamesGetAsync(cancellationToken: cts.Token);
            cachedNames = names?.ToDictionary(i => i.Tag, i => i.Name) ?? new();
            namesFetchedAt = DateTime.UtcNow;
            return cachedNames;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            namesFetchedAt = DateTime.UtcNow - NamesCacheDuration + TimeSpan.FromSeconds(30);
            logger.LogWarning(e, "item names unavailable for task results, serving {count} stale/tags", cachedNames?.Count ?? 0);
            return cachedNames ?? new();
        }
        finally
        {
            namesRefreshLock.Release();
        }
    }
}
