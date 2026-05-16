using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState.Services;

public class BazaarSignalPublisher
{
    private readonly IConnectionMultiplexer redis;
    private readonly ILogger<BazaarSignalPublisher> logger;

    public BazaarSignalPublisher(IConnectionMultiplexer redis, ILogger<BazaarSignalPublisher> logger)
    {
        this.redis = redis;
        this.logger = logger;
    }

    public async Task PublishAsync(BazaarSignalEvent signal)
    {
        if (string.IsNullOrWhiteSpace(signal.ItemTag) || string.IsNullOrWhiteSpace(signal.Type))
        {
            return;
        }

        await redis.GetSubscriber().PublishAsync(
            RedisChannel.Literal(BazaarSignalChannels.LiveSignals),
            JsonConvert.SerializeObject(signal));
        logger.LogDebug("Published bazaar signal {Type} for {ItemTag}", signal.Type, signal.ItemTag);
    }
}