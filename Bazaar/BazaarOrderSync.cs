using System.Diagnostics;
using Prometheus;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.Bazaar.Client.Client;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderSync(HttpClient client)
{
    internal const string SourceName = "Coflnet.Sky.UserState.Bazaar";
    internal static readonly ActivitySource Traces = new(SourceName);
    internal static readonly Counter Syncs = Metrics.CreateCounter("sky_userstate_bazaar_sync_total", "Order synchronization attempts", new CounterConfiguration { LabelNames = new[] { "operation", "result" } });
    private static readonly Gauge Waiting = Metrics.CreateGauge("sky_userstate_bazaar_retry_waits", "Order observations currently waiting ten seconds to retry");

    internal static async Task Retry(Func<Task> send, ILogger logger, CancellationToken cancellationToken = default,
        string operation = "observe", string userId = null, string itemTag = null, DateTime? created = null)
    {
        using var span = Traces.StartActivity("bazaar.order.sync");
        span?.SetTag("bazaar.user_id", userId);
        span?.SetTag("bazaar.item_tag", itemTag);
        span?.SetTag("bazaar.operation", operation);
        span?.SetTag("bazaar.order_created", created?.ToString("O"));
        var started = Stopwatch.GetTimestamp();
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                await send();
                Syncs.WithLabels(operation, "success").Inc();
                logger.Log(attempts > 1 ? LogLevel.Information : LogLevel.Debug,
                    "Bazaar sync {Operation} succeeded for {UserId}/{ItemTag}, order created {OrderCreated}, attempts {Attempts}, elapsed {ElapsedMs} ms; TraceId {TraceId}",
                    operation, userId, itemTag, created, attempts, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Activity.Current?.TraceId.ToString());
                return;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && IsTemporary(e))
            {
                Syncs.WithLabels(operation, "retry").Inc();
                logger.LogWarning(e, "Bazaar sync {Operation} unavailable for {UserId}/{ItemTag}, order created {OrderCreated}, attempt {Attempt}; retry in 10 seconds; TraceId {TraceId}",
                    operation, userId, itemTag, created, attempts, Activity.Current?.TraceId.ToString());
                Waiting.Inc();
                try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
                finally { Waiting.Dec(); }
            }
            catch (Exception e)
            {
                Syncs.WithLabels(operation, "failure").Inc();
                span?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
                logger.LogError(e, "Bazaar sync {Operation} rejected for {UserId}/{ItemTag}, order created {OrderCreated}; TraceId {TraceId}",
                    operation, userId, itemTag, created, Activity.Current?.TraceId.ToString());
                throw;
            }
        }
    }

    private static bool IsTemporary(Exception error) => error switch {
        ApiException e => e.ErrorCode == 0 || e.ErrorCode == 408 || e.ErrorCode == 429 || e.ErrorCode >= 500,
        HttpRequestException e => e.StatusCode == null || (int)e.StatusCode >= 500
            || e.StatusCode == HttpStatusCode.RequestTimeout || (int)e.StatusCode == 429,
        TaskCanceledException => true,
        _ => false
    };

    public virtual async Task Observe(UpdateArgs args)
    {
        // Capture the observation once so a retry cannot change its identity or timestamp.
        var orders = args.currentState.BazaarOffers.Select(o => new OrderEntry {
            ItemId = o.ItemTag, Amount = (int)o.Amount, Filled = (int)o.FilledAmount,
            PricePerUnit = o.PricePerUnit, IsSell = o.IsSell, Timestamp = o.Created,
            UserId = args.msg.UserId, PlayerName = args.currentState.McInfo.Name
        }).ToList();
        var observation = new {
            args.msg.UserId, PlayerName = args.currentState.McInfo.Name,
            Timestamp = args.msg.ReceivedAt, Orders = orders
        };
        var logger = args.GetService<ILogger<BazaarOrderSync>>();
        logger.LogDebug("Synchronizing personal Bazaar view for {UserId}/{PlayerName}: {OrderCount} orders at {ObservedAt:o}",
            args.msg.UserId, observation.PlayerName, orders.Count, observation.Timestamp);
        await Retry(async () => {
            using var response = await client.PostAsJsonAsync("OrderBook/player", observation);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Using legacy Bazaar registration for {UserId}; personal reconciliation endpoint is unavailable", args.msg.UserId);
                // Older SkyBazaar instances support registration but not full reconciliation.
                foreach (var order in orders)
                {
                    using var legacy = await client.PostAsJsonAsync("OrderBook", order);
                    legacy.EnsureSuccessStatusCode();
                }
                return;
            }
            response.EnsureSuccessStatusCode();
        }, logger, operation: "observe", userId: args.msg.UserId);
    }
}
