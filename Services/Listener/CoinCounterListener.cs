using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Listens for chat messages that indicate coin transactions and updates counters
/// </summary>
public class CoinCounterListener : UpdateListener
{
    private readonly ICoinCounterService coinCounterService;
    private readonly CoinCounterParser parser;

    public CoinCounterListener(ICoinCounterService coinCounterService)
    {
        this.coinCounterService = coinCounterService;
        this.parser = new CoinCounterParser();
    }

    public override async Task Process(UpdateArgs args)
    {
        foreach (var chatMsg in args.msg.ChatBatch)
        {
            if (parser.TryParse(chatMsg, out var type, out var amount))
            {
                if (type.HasValue)
                {
                    await coinCounterService.IncrementCounter(
                        args.msg.PlayerId,
                        args.msg.ReceivedAt,
                        type.Value,
                        amount);

                    Console.WriteLine($"[CoinCounter] {args.msg.PlayerId}: {type.Value} +{amount} coins");
                }
            }
        }
    }
}
