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

namespace Coflnet.Sky.PlayerState.Services;

public class PlayerStateBackgroundService : BackgroundService
{
    public IServiceScopeFactory scopeFactory { private set; get; }
    private IConfiguration config;
    private ILogger<PlayerStateBackgroundService> logger;
    private Prometheus.Counter consumeCount = Prometheus.Metrics.CreateCounter("sky_playerstate_conume", "How many messages were consumed");

    public ConcurrentDictionary<string, StateObject> States = new();

    private ConcurrentDictionary<UpdateMessage.UpdateKind, List<UpdateListener>> Handlers = new();

    public PlayerStateBackgroundService(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<PlayerStateBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;

        AddHandler<ChatHistoryUpdate>(UpdateMessage.UpdateKind.CHAT);
        AddHandler<ProfileAndNameUpdate>(UpdateMessage.UpdateKind.CHAT);


        AddHandler<ItemIdAssignUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<RecentViewsUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<InventoryChangeUpdate>(UpdateMessage.UpdateKind.INVENTORY);
        AddHandler<AhBrowserListener>(UpdateMessage.UpdateKind.INVENTORY);

        AddHandler<TradeDetect>(UpdateMessage.UpdateKind.INVENTORY | UpdateMessage.UpdateKind.CHAT);
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
            handler = (T)Activator.CreateInstance(typeof(T),scope.ServiceProvider.GetRequiredService<ILogger<T>>());
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
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config["KAFKA_HOST"],
            SessionTimeoutMs = 9_000,
            AutoOffsetReset = AutoOffsetReset.Latest,
            GroupId = config["KAFKA_GROUP_ID"]
        };
        await TestCassandraConnection();

        await Kafka.KafkaConsumer.ConsumeBatch<UpdateMessage>(consumerConfig, new string[] { config["TOPICS:STATE_UPDATE"] }, async batch =>
        {
            if (batch.Max(b => b.ReceivedAt) < DateTime.Now - TimeSpan.FromHours(3))
                return;
            await Task.WhenAll(batch.Select(async update =>
            {
                await Update(update);
                consumeCount.Inc();
            }));
        }, stoppingToken, 5);
        var retrieved = new UpdateMessage();
    }

    private async Task TestCassandraConnection()
    {
        await ExecuteInScope(async sp =>
        {
            var transactionService = sp.GetRequiredService<ITransactionService>();
            logger.LogInformation("testing cassandra connection");
            await transactionService.GetItemTransactions(0, 1);
            logger.LogInformation("Cassandra connection works");
        });
    }

    private async Task Update(UpdateMessage msg)
    {
        if (msg.PlayerId == null)
            msg.PlayerId = "!anonym";
        var state = States.GetOrAdd(msg.PlayerId, (p) => new StateObject() { PlayerId = p });
        var args = new UpdateArgs()
        {
            currentState = state,
            msg = msg,
            stateService = this
        };
        try
        {
            await state.Lock.WaitAsync();
            foreach (var item in Handlers[msg.Kind])
            {
                await item.Process(args);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed update state on " + msg.Kind + " with " + JsonConvert.SerializeObject(msg));
        }
        finally
        {
            state.Lock.Release();
        }
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
}
