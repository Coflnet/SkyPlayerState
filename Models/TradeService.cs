using System.Collections.Generic;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using System.Linq;
using System.ComponentModel.DataAnnotations;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Configuration;
using Confluent.Kafka;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

public interface ITradeService
{
    Task<IEnumerable<Transaction>> GetTradeTransactions(string itemTag, Guid itemId, DateTime end);
    Task ProduceTrade(TradeModel trade);
}

[MessagePackObject]
public class TradeModel
{
    [MessagePack.Key(0)]
    public string UserId { get; set; }
    [MessagePack.Key(1)]
    public Guid MinecraftUuid { get; set; }
    [MessagePack.Key(2)]
    public List<Item> Spent { get; set; }
    [MessagePack.Key(3)]
    public List<Item> Received { get; set; }
    [MessagePack.Key(4)]
    public string OtherSide { get; set; }
    [MessagePack.Key(5)]
    public DateTime TimeStamp { get; set; }
}

public class TradeService : ITradeService
{
    private ITransactionService transactionService;
    private IItemsService itemService;
    private string topic = "sky-player-trade";
    private IProducer<string, TradeModel> producer;

    public TradeService(ITransactionService transactionService, IItemsService itemService, Kafka.KafkaCreator kafkaCreator, IConfiguration configuration)
    {
        this.transactionService = transactionService;
        this.itemService = itemService;
        this.topic = configuration["TOPICS:PLAYER_TRADE"] ?? throw new ValidationException("No TOPICS:PLAYER_TRADE defined");
        kafkaCreator.CreateTopicIfNotExist(topic, 1).Wait();
        producer = kafkaCreator.BuildProducer<string,TradeModel>();
    }
    public async Task<IEnumerable<Transaction>> GetTradeTransactions(string itemTag, Guid itemId, DateTime end)
    {
        var items = await itemService.FindItems(new ItemIdSearch[] { new() { Tag = itemTag, Uuid = itemId } });
        var intItemId = items.FirstOrDefault()?.Id ?? throw new ValidationException("Item not found");
        // history is returned newest first; this lookup always used the oldest (first) transfer of the item
        var transfers = await transactionService.GetItemTransactions(intItemId, TransactionService.MaxItemTransactions);
        var first = SelectOldestTransfer(transfers) ?? throw new ValidationException("No transfers for item found");
        var tradeTransactions = await transactionService.GetTransactions(first.PlayerUuid, TimeSpan.FromSeconds(30), first.TimeStamp + TimeSpan.FromSeconds(1));

        return tradeTransactions;
    }

    public static Transaction SelectOldestTransfer(IEnumerable<Transaction> transfers)
    {
        return transfers.OrderBy(t => t.TimeStamp).FirstOrDefault();
    }

    public Task ProduceTrade(TradeModel trade)
    {
        return producer.ProduceAsync(topic, new Message<string, TradeModel> { Key = trade.UserId, Value = trade });
    }
}
#nullable restore