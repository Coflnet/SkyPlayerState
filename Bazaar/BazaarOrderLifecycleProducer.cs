using System;
using System.ComponentModel.DataAnnotations;
using Confluent.Kafka;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

/// <summary>
/// Produces <see cref="BazaarOrderLifecycleEvent"/>s to <c>TOPICS:BAZAAR_ORDER_LIFECYCLE</c>, keyed by player uuid.
/// </summary>
public class BazaarOrderLifecycleProducer : IBazaarOrderLifecycleProducer
{
    private readonly string topic;
    private readonly IProducer<string, BazaarOrderLifecycleEvent> producer;
    private readonly ILogger<BazaarOrderLifecycleProducer> logger;

    public BazaarOrderLifecycleProducer(Kafka.KafkaCreator kafkaCreator, IConfiguration configuration, ILogger<BazaarOrderLifecycleProducer> logger)
    {
        this.logger = logger;
        topic = configuration["TOPICS:BAZAAR_ORDER_LIFECYCLE"] ?? throw new ValidationException("No TOPICS:BAZAAR_ORDER_LIFECYCLE defined");
        // Fire and forget: a down Kafka must not block resolving the producer (and with it the state update).
        // Produce proceeds regardless; broker auto-create or the delivery report surfaces real problems.
        _ = CreateTopic(kafkaCreator);
        producer = kafkaCreator.BuildProducer<string, BazaarOrderLifecycleEvent>();
    }

    private async System.Threading.Tasks.Task CreateTopic(Kafka.KafkaCreator kafkaCreator)
    {
        try
        {
            await kafkaCreator.CreateTopicIfNotExist(topic, 1);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not create bazaar order lifecycle topic {Topic}", topic);
        }
    }

    public void Emit(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride = null)
    {
        try
        {
            var payload = BazaarOrderLifecycleEvent.TryCreate(args, offer, source, state, itemTagOverride);
            if (payload == null)
                return;
            producer.Produce(topic, new Message<string, BazaarOrderLifecycleEvent> { Key = payload.PlayerUuid.ToString(), Value = payload }, report =>
            {
                if (report.Error.IsError)
                    logger.LogWarning("Failed to deliver bazaar order lifecycle event {EventId}: {Error}", payload.EventId, report.Error.Reason);
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not emit bazaar order lifecycle event {Source}/{State}", source, state);
        }
    }
}

/// <summary>Helpers that resolve the producer defensively; emissions must never alter listener flow.</summary>
internal static class BazaarOrderLifecycle
{
    internal static IBazaarOrderLifecycleProducer Resolve(UpdateArgs args)
    {
        try { return args.GetService<IBazaarOrderLifecycleProducer>(); }
        catch { return null; }
    }

    internal static void Emit(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride = null)
    {
        try { Resolve(args)?.Emit(args, offer, source, state, itemTagOverride); }
        catch { /* never affect the caller */ }
        RecordHistory(args, offer, source, state, itemTagOverride);
    }

    /// <summary>Emits, resolving a missing item tag from the name first (without touching the offer).</summary>
    internal static async System.Threading.Tasks.Task EmitResolved(UpdateArgs args, Offer offer, string source, string state)
    {
        string tag = null;
        try
        {
            var producer = Resolve(args);
            // nothing to emit to: keep the old early return and skip the tag lookup
            if (producer == null && !HistoryEnabled(args))
                return;
            tag = offer.ItemTag;
            if (string.IsNullOrWhiteSpace(tag) && !string.IsNullOrEmpty(args.msg.UserId))
                tag = await BazaarOrderListener.GetTagForName(args, offer.ItemName);
            producer?.Emit(args, offer, source, state, tag);
        }
        catch { /* never affect the caller */ }
        RecordHistory(args, offer, source, state, tag);
    }

    private static bool HistoryEnabled(UpdateArgs args)
    {
        try { return args.GetService<History.IBazaarOrderHistoryWriter>()?.Enabled == true; }
        catch { return false; }
    }

    /// <summary>
    /// Mirrors every lifecycle observation into the cross-player history tables (the single hook for order state
    /// changes). Queues only; any failure is swallowed so the order state update is never affected.
    /// </summary>
    private static void RecordHistory(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride)
    {
        try
        {
            var writer = args.GetService<History.IBazaarOrderHistoryWriter>();
            if (writer == null || !writer.Enabled)
                return;
            var lifecycleEvent = BazaarOrderLifecycleEvent.TryCreate(args, offer, source, state, itemTagOverride);
            if (lifecycleEvent != null)
                writer.EnqueueOrder(lifecycleEvent, offer.Customers);
        }
        catch { /* history is best effort and must never affect the caller */ }
    }
}
