using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Confluent.Kafka;
using MessagePack;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.PlayerState.Bazaar;

/// <summary>
/// A completed bazaar flip exported for downstream consumers (e.g. the bazaar flipper's realized-profit
/// oracle). Unlike the stored <see cref="CompletedBazaarFlip"/> (coins*10 tenths), prices here are in
/// whole coins for convenience, matching the /BazaarProfit REST responses.
/// </summary>
[MessagePackObject]
public class BazaarFlipEvent
{
    /// <summary>Minecraft UUID of the player that completed the flip.</summary>
    [MessagePack.Key(0)]
    public Guid PlayerUuid { get; set; }
    /// <summary>Item tag, e.g. "ENCHANTED_DIAMOND_BLOCK".</summary>
    [MessagePack.Key(1)]
    public string ItemTag { get; set; }
    /// <summary>Item display name.</summary>
    [MessagePack.Key(2)]
    public string ItemName { get; set; }
    /// <summary>Number of items in the matched round trip.</summary>
    [MessagePack.Key(3)]
    public int Amount { get; set; }
    /// <summary>Total buy cost of the matched amount, in coins.</summary>
    [MessagePack.Key(4)]
    public double BuyPrice { get; set; }
    /// <summary>Total sell proceeds (after bazaar tax, as claimed) of the matched amount, in coins.</summary>
    [MessagePack.Key(5)]
    public double SellPrice { get; set; }
    /// <summary>Realized profit (sell - buy), in coins.</summary>
    [MessagePack.Key(6)]
    public double Profit { get; set; }
    /// <summary>When the sell order was claimed (UTC).</summary>
    [MessagePack.Key(7)]
    public DateTime SoldAt { get; set; }
}

/// <summary>
/// Produces completed bazaar flips to Kafka so other services can build a realized-fill dataset
/// across all users (the store is per-player partitioned, so a push stream is the only all-users
/// export). Modeled on <see cref="Models.TradeService"/>. Topic: <c>TOPICS:BAZAAR_FLIP</c>.
/// </summary>
public class BazaarFlipExportProducer
{
    private readonly string topic;
    private readonly IProducer<string, BazaarFlipEvent> producer;

    public BazaarFlipExportProducer(Kafka.KafkaCreator kafkaCreator, IConfiguration configuration)
    {
        topic = configuration["TOPICS:BAZAAR_FLIP"] ?? throw new ValidationException("No TOPICS:BAZAAR_FLIP defined");
        kafkaCreator.CreateTopicIfNotExist(topic, 1).Wait();
        producer = kafkaCreator.BuildProducer<string, BazaarFlipEvent>();
    }

    /// <summary>Exports one completed flip. Key is the item tag so a consumer can co-locate an item's fills.</summary>
    public Task Produce(CompletedBazaarFlip flip)
    {
        var payload = new BazaarFlipEvent
        {
            PlayerUuid = flip.PlayerUuid,
            ItemTag = flip.ItemTag,
            ItemName = flip.ItemName,
            Amount = flip.Amount,
            BuyPrice = flip.BuyPrice / 10.0,
            SellPrice = flip.SellPrice / 10.0,
            Profit = flip.Profit / 10.0,
            SoldAt = flip.SoldAt
        };
        return producer.ProduceAsync(topic, new Message<string, BazaarFlipEvent> { Key = flip.ItemTag, Value = payload });
    }
}
