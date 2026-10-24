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
        kafkaCreator.CreateTopicIfNotExist(topic, 1).Wait();
        producer = kafkaCreator.BuildProducer<string, BazaarOrderLifecycleEvent>();
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
    }

    /// <summary>Emits, resolving a missing item tag from the name first (without touching the offer).</summary>
    internal static async System.Threading.Tasks.Task EmitResolved(UpdateArgs args, Offer offer, string source, string state)
    {
        try
        {
            var producer = Resolve(args);
            if (producer == null)
                return;
            var tag = offer.ItemTag;
            if (string.IsNullOrWhiteSpace(tag) && !string.IsNullOrEmpty(args.msg.UserId))
                tag = await BazaarOrderListener.GetTagForName(args, offer.ItemName);
            producer.Emit(args, offer, source, state, tag);
        }
        catch { /* never affect the caller */ }
    }
}
