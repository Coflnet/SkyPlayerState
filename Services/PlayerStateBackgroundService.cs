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
    private Prometheus.Counter consumeCount = Prometheus.Metrics.CreateCounter("sky_playerstate_conume", "How many messages were consumed");

    public ConcurrentDictionary<string, StateObject> States = new();
    private IPersistenceService persistenceService;
    private ActivitySource activitySource;

    private ConcurrentDictionary<UpdateMessage.UpdateKind, List<UpdateListener>> Handlers = new();

    public PlayerStateBackgroundService(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<PlayerStateBackgroundService> logger, IPersistenceService persistenceService, ActivitySource activitySource)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
        AddHandler<SettingsListener>(UpdateMessage.UpdateKind.Setting);
        // handlers are executed in this order
        AddHandler<ChatHistoryUpdate>(UpdateMessage.UpdateKind.CHAT);
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
        AddHandler<RecipeUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<KatInventoryListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<ForgeListener>(UpdateMessage.UpdateKind.INVENTORY);

        AddHandler<KuudraListener>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.Scoreboard);
        AddHandler<CollectionListener>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.Scoreboard | UpdateMessage.UpdateKind.CHAT | UpdateMessage.UpdateKind.Tab);
        AddHandler<TradeDetect>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.CHAT);
        AddHandler<TradeInfoListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<ShensListener>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<SkillListener>(UpdateMessage.UpdateKind.INVENTORY);
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
            handler = (T)Activator.CreateInstance(typeof(T), scope.ServiceProvider.GetRequiredService<ILogger<T>>());
        }
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

        var backOff = false;
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
            await Task.WhenAny(Task.WhenAll(batch.Select(async update =>
            {
                backOff = await Update(update);
                consumeCount.Inc();
            })), Task.Delay(TimeSpan.FromSeconds(0.7)));

            if (backOff)
            {
                logger.LogWarning("Backoff cause error");
                await Task.Delay(8000);
                backOff = false;
            }
            KeepStateCountInCheck();
        }, stoppingToken, 25);
        var retrieved = new UpdateMessage();
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
            var bootstrapServers = config["KAFKA_HOST"];
            using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
            try
            {
                // increase the number of partitions for the topic "my-topic"
                adminClient.CreatePartitionsAsync(new PartitionsSpecification[] { new PartitionsSpecification(){
                    Topic = config["TOPICS:STATE_UPDATE"],
                    IncreaseTo = 10
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
                await item.Process(args);
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
            await Update(msg, attempt + 1);
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
