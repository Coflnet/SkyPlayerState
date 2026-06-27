using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.PlayerState.Controllers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using Confluent.Kafka.Admin;
using Coflnet.Sky.PlayerState.Bazaar;
using System.Diagnostics;

namespace Coflnet.Sky.PlayerState.Services;

public interface IPlayerStateService
{
    public Task ExecuteInScope(Func<IServiceProvider, Task> todo);
    public void TryExecuteInScope(Func<IServiceProvider, Task> todo);
    public AsyncServiceScope CreateAsyncScope();
}

public class PlayerStateBackgroundService : BackgroundService, IPlayerStateService
{
    public IServiceScopeFactory scopeFactory { private set; get; }
    private IConfiguration config;
    private ILogger<PlayerStateBackgroundService> logger;
    private ILoggerFactory loggerFactory;
    private Prometheus.Counter consumeCount = Prometheus.Metrics.CreateCounter("sky_playerstate_conume", "How many messages were consumed");
    private Prometheus.Counter droppedCount = Prometheus.Metrics.CreateCounter("sky_playerstate_dropped", "How many messages were dropped after exhausting retries");
    private static readonly Prometheus.Counter optionalHandlerFailedCount = Prometheus.Metrics.CreateCounter("sky_playerstate_optional_handler_failed", "How many times an optional handler threw and was skipped (state still saved), by handler.", new Prometheus.CounterConfiguration { LabelNames = new[] { "handler" } });

    public ConcurrentDictionary<string, StateObject> States = new();
    private IPersistenceService persistenceService;
    private ActivitySource activitySource;

    private ConcurrentDictionary<UpdateMessage.UpdateKind, List<UpdateListener>> Handlers = new();

    public PlayerStateBackgroundService(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<PlayerStateBackgroundService> logger, IPersistenceService persistenceService, ActivitySource activitySource, ILoggerFactory loggerFactory)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        AddHandler<SettingsListener>(UpdateMessage.UpdateKind.Setting);
        // handlers are executed in this order
        AddHandler<ChatHistoryUpdate>(UpdateMessage.UpdateKind.CHAT);
        AddHandler<CoinCounterListener>(UpdateMessage.UpdateKind.CHAT);
        AddHandler<ProfileAndNameUpdate>(UpdateMessage.UpdateKind.CHAT | UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<BazaarOrderListener>(UpdateMessage.UpdateKind.CHAT);
        AddHandler<TradeLimitsUpdate>(UpdateMessage.UpdateKind.CHAT);
        AddHandler<KatChatListener>(UpdateMessage.UpdateKind.CHAT);


        AddHandler<ItemIdAssignUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<InventoryChangeUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<AhBrowserListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<BazaarListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<RecentViewsUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<BoosterCookieExtractor>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<ActivePetListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<RecipeUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<RngMeterUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<KatInventoryListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<ForgeListener>(UpdateMessage.UpdateKind.INVENTORY);

        AddHandler<HeartOfTheListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<HuntingListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<KuudraListener>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.Scoreboard);
        AddHandler<TabListUpdate>(UpdateMessage.UpdateKind.Tab);
        AddHandler<CollectionListener>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.Scoreboard | UpdateMessage.UpdateKind.CHAT | UpdateMessage.UpdateKind.Tab);
        AddHandler<TradeDetect>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.CHAT);
        AddHandler<TradeInfoListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<ShensListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<SkillListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<StorageListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<AttributeMenuListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<MayorAuraListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<PlayerElectionListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<MythologicalRitualListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<BitListener>(UpdateMessage.UpdateKind.INVENTORY);
        this.persistenceService = persistenceService;
        this.activitySource = activitySource;
    }

    private void AddHandler<T>(UpdateMessage.UpdateKind kinds = UpdateMessage.UpdateKind.UNKOWN) where T : UpdateListener
    {
        T handler;
        try
        {
            handler = Activator.CreateInstance<T>();
        }
        catch (System.Exception)
        {
            var scope = scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            // Try to resolve the listener from DI, otherwise create with ActivatorUtilities
            var resolved = sp.GetService(typeof(T));
            if (resolved != null)
                handler = (T)resolved;
            else
                handler = (T)ActivatorUtilities.CreateInstance(sp, typeof(T));
        }
        handler.SetLogger(loggerFactory.CreateLogger(typeof(T)));
        foreach (var item in Enum.GetValues<UpdateMessage.UpdateKind>())
        {
            if (kinds != UpdateMessage.UpdateKind.UNKOWN && (item == UpdateMessage.UpdateKind.UNKOWN || !kinds.HasFlag(item)))
                continue;
            Handlers.GetOrAdd(item, k => new List<UpdateListener>()).Add(handler);
        }
    }
    /// <summary>
    /// Called by asp.net on startup
    /// </summary>
    /// <param name="stoppingToken">is canceled when the applications stops</param>
    /// <returns></returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("booting handlers");
        foreach (var item in Handlers.SelectMany(h => h.Value).GroupBy(h => h.GetType()).Select(g => g.First()))
        {
            await item.Load(stoppingToken);
        }
        logger.LogInformation("Initialized handlers, consuming");
        var consumerConfig = new ConsumerConfig(Kafka.KafkaCreator.GetClientConfig(config))
        {
            SessionTimeoutMs = 9_000,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            GroupId = config["KAFKA_GROUP_ID"]
        };
        await TestCassandraConnection();

        await Kafka.KafkaConsumer.ConsumeBatch<UpdateMessage>(consumerConfig, new string[] { config["TOPICS:STATE_UPDATE"] }, async batch =>
        {
            if (batch.Max(b => b.ReceivedAt) < DateTime.UtcNow - TimeSpan.FromHours(1))
            {
                logger.LogWarning("Received old batch of {0} messages", batch.Count());
                _ = Task.WhenAll(batch.Select(async update =>
                {
                    if (update.Kind == UpdateMessage.UpdateKind.INVENTORY && !update.Chest.Name.StartsWith("You"))
                    {
                        // Only "You ..." (trade) and "Your Bazaar Orders" (bazaar) are relevant for tracking
                        return;
                    }
                    if (update.Kind == UpdateMessage.UpdateKind.CHAT && !update.ChatBatch.Any(m => m.StartsWith(" + ") && !m.StartsWith(" - ")))
                    {
                        // Ignore no chat messages
                        return;
                    }
                    try
                    {
                        await Update(update);
                        if (Random.Shared.NextDouble() < 0.01)
                            logger.LogWarning("Processed old update {0}", JsonConvert.SerializeObject(update));
                    }
                    catch (System.Exception e)
                    {
                        logger.LogError(e, "Error while processing old update");
                    }
                }));
                return;
            }
            logger.LogInformation("Consuming batch of {0} messages", batch.Count());
            using var span = activitySource.StartActivity("Batch", ActivityKind.Consumer);
            // Await the whole batch so offsets are only committed (by the consumer, after this
            // returns) once every message has actually been processed - no commit-before-done.
            // Per-message failures are retried inside Update and dropped there after 3 attempts,
            // so a single failing/poison message no longer stalls or backs off the whole pipeline.
            await Task.WhenAll(batch.Select(async update =>
            {
                await Update(update);
                consumeCount.Inc();
            }));
            KeepStateCountInCheck();
        }, stoppingToken, 50);
        var retrieved = new UpdateMessage();
    }

    /// <summary>
    /// Called by the host on shutdown (planned restarts are the common case). The consumer is
    /// stopped first via base.StopAsync so <see cref="States"/> stops mutating, then every
    /// in-memory state is force-saved to cassandra - otherwise state that hasn't reached its 8s
    /// save window (and the in-memory delayed-save queue) would be lost on restart.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // stop consuming first so no new updates land while we flush
        await base.StopAsync(cancellationToken);
        var total = States.Count;
        logger.LogInformation("Shutdown: flushing {count} in-memory player states to cassandra", total);
        var sw = Stopwatch.StartNew();
        var flushed = 0;
        // bound concurrency so the flush burst doesn't overwhelm cassandra
        using var throttler = new SemaphoreSlim(20);
        await Task.WhenAll(States.Values.Select(async state =>
        {
            await throttler.WaitAsync();
            try
            {
                // honour the per-player lock so we don't serialize a state mid-update
                await state.Lock.WaitAsync(cancellationToken);
                try
                {
                    await persistenceService.ForceSave(state);
                    Interlocked.Increment(ref flushed);
                }
                finally
                {
                    state.Lock.Release();
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to flush state for {playerId} on shutdown", state.PlayerId);
            }
            finally
            {
                throttler.Release();
            }
        }));
        logger.LogInformation("Shutdown: flushed {flushed}/{total} states in {ms}ms", flushed, total, sw.ElapsedMilliseconds);
    }

    private void KeepStateCountInCheck()
    {
        if (States.Count < 200)
            return;
        foreach (var key in States.Keys)
        {
            var item = States[key];
            if (item.LastAccess < DateTime.UtcNow - TimeSpan.FromHours(0.5))
            {
                States.TryRemove(key, out _);
            }
        }

        if (States.Count < 600)
            return;

        var oldest = States.OrderBy(s => s.Value.LastAccess).First();
        States.TryRemove(oldest.Key, out var removed);
        logger.LogWarning("States count is {0} removed {1}, last used {used}", States.Count, removed?.PlayerId, removed?.LastAccess);
    }

    private async Task TestCassandraConnection()
    {
        await ExecuteInScope(async sp =>
        {
            var transactionService = sp.GetRequiredService<ITransactionService>();
            logger.LogInformation("testing cassandra connection");
            await transactionService.GetItemTransactions(0, 1);
            logger.LogInformation("Cassandra connection works");
            // Use the shared Kafka client config (brokers + SASL + TLS from KAFKA:* / OpenBao),
            // not the bare KAFKA_HOST default which points at the unresolvable "kafka" host and
            // carries no auth - that blocked startup before the consumer ever started.
            using var adminClient = new AdminClientBuilder(Kafka.KafkaCreator.GetClientConfig(config)).Build();
            try
            {
                // increase the number of partitions for the topic "my-topic"
                adminClient.CreatePartitionsAsync(new PartitionsSpecification[] { new PartitionsSpecification(){
                    Topic = config["TOPICS:STATE_UPDATE"],
                    IncreaseTo = 6
                } }).Wait();
            }
            catch (Exception e)
            {
                if (!e.Message.Contains("Partition count must be greater then current number of partitions") && !e.Message.Contains("already has"))
                    logger.LogError(e, "failed to increase partitions");
            }
        });

    }

    private async Task<bool> Update(UpdateMessage msg, int attempt = 0)
    {
        if (msg.PlayerId == null)
            msg.PlayerId = "!anonym";
        if (msg.PlayerId == "Ekwav")
        {
            // dump for debug
            logger.LogInformation("Received update for Ekwav {0}", JsonConvert.SerializeObject(msg));
        }
        var state = States.GetOrAdd(msg.PlayerId, (p) => new StateObject() { });
        using var args = new UpdateArgs()
        {
            currentState = state,
            msg = msg,
            stateService = this
        };
        var error = false;
        try
        {
            await state.Lock.WaitAsync();
            if (state.PlayerId == null)
            {
                state.PlayerId = msg.PlayerId;
                var loaded = await persistenceService.GetStateObject(msg.PlayerId);
                loaded.Lock = state.Lock;
                loaded.PlayerId = state.PlayerId;
                state = loaded;
                States[msg.PlayerId] = state;
            }
            using var span = activitySource.StartActivity("Update", ActivityKind.Consumer);
            span?.SetTag("playerId", msg.PlayerId);
            span?.SetTag("kind", msg.Kind.ToString());
            foreach (var item in Handlers[msg.Kind])
            {
                using var procSpan = activitySource.StartActivity("Process", ActivityKind.Consumer);
                procSpan?.SetTag("handler", item.GetType().Name);
                try
                {
                    await item.Process(args);
                }
                catch (Exception e) when (item.Optional)
                {
                    // optional enrichment failed - log, count and carry on so the remaining handlers
                    // still run and the state is still persisted. Core handlers are NOT caught here:
                    // their failure propagates to the retry/drop path below (manual-intervention signal).
                    optionalHandlerFailedCount.WithLabels(item.GetType().Name).Inc();
                    procSpan?.SetTag("optional_failed", true);
                    logger.LogWarning(e, "optional handler {handler} failed for {player} on {kind}, continuing", item.GetType().Name, msg.PlayerId, msg.Kind);
                }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await persistenceService.SaveStateObject(state);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "failed to save state");
                }
            });
            state.LastAccess = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed update state on " + msg.Kind + " with " + JsonConvert.SerializeObject(msg));
            error = true;
        }
        finally
        {
            state.Lock.Release();
        }
        if (error && attempt < 3) // after finally to avoid semaphore lock
            return await Update(msg, attempt + 1);
        if (error)
        {
            // exhausted retries: drop the message so it can't stall the partition, but make the
            // loss observable via a metric (alerted on) and a log line instead of silently skipping.
            droppedCount.Inc();
            logger.LogError("Dropping update for {player} on {kind} after {attempts} failed attempts", msg.PlayerId, msg.Kind, attempt + 1);
        }
        return error;
    }

    public async Task ExecuteInScope(Func<IServiceProvider, Task> todo)
    {
        using var scope = scopeFactory.CreateScope();
        await todo(scope.ServiceProvider);
    }

    public void TryExecuteInScope(Func<IServiceProvider, Task> todo)
    {
        Task.Run(async () =>
        {
            try
            {
                await ExecuteInScope(todo);
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to execute in scope");
            }
        });
    }

    public AsyncServiceScope CreateAsyncScope()
    {
        return scopeFactory.CreateAsyncScope();
    }
}
