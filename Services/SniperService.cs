using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Core;
using RestSharp;
using MessagePack;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.PlayerState.Services;

public class SniperService
{
    private RestClient sniperClient;
    private IConfiguration config;
    private ILogger<SniperService> logger;
    public SniperService(IConfiguration config, ILogger<SniperService> logger)
    {
        this.config = config;
        sniperClient = new(config["SNIPER_BASE_URL"] ?? throw new ArgumentNullException("SNIPER_BASE_URL"));
        this.logger = logger;
    }

    public async Task<List<Sniper.Client.Model.PriceEstimate>> GetPrices(IEnumerable<Models.Item> items)
    {
        return await GetPrices(items.Select(ToAuctionRepresent));
    }

    public async Task<List<Sniper.Client.Model.PriceEstimate>> GetPrices(IEnumerable<SaveAuction> auctionRepresent)
    {
        var request = new RestRequest("/api/sniper/prices", RestSharp.Method.Post);
        var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
        request.AddJsonBody(JsonConvert.SerializeObject(Convert.ToBase64String(MessagePackSerializer.Serialize(auctionRepresent, options))));

        var respone = await sniperClient.ExecuteAsync(request).ConfigureAwait(false);
        if (respone.StatusCode == 0)
        {
            logger.LogError("sniper service could not be reached");
            return auctionRepresent.Select(a => new Sniper.Client.Model.PriceEstimate()).ToList();
        }
        try
        {
            return JsonConvert.DeserializeObject<List<Sniper.Client.Model.PriceEstimate>>(respone.Content);
        }
        catch (System.Exception)
        {
            logger.LogError("responded with " + respone.StatusCode + respone.Content);
            throw;
        }
    }

    public IEnumerable<SaveAuction> FromitemRepresent(Models.Item[] items)
    {
        return items.Select(ToAuctionRepresent);
    }

    private static SaveAuction ToAuctionRepresent(Models.Item i)
    {
        var auction = new SaveAuction()
        {
            Count = i.Count ?? 1,
            Tag = i.Tag,
            ItemName = i.ItemName,

        };
        auction.Enchantments = i.Enchantments?.Select(e => new Enchantment()
        {
            Type = Enum.TryParse<Enchantment.EnchantmentType>(e.Key, out var type) ? type : Enchantment.EnchantmentType.unknown,
            Level = e.Value
        }).ToList() ?? new();
        if (i.ExtraAttributes != null)
        {
            auction.Tier = Enum.TryParse<Tier>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "tier").Value?.ToString() ?? "", out var tier) ? tier : Tier.UNKNOWN;
            auction.Reforge = Enum.TryParse<ItemReferences.Reforge>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "modifier").Value?.ToString() ?? "", out var reforge) ? reforge : ItemReferences.Reforge.Unknown;
            auction.SetFlattenedNbt(NBT.FlattenNbtData(i.ExtraAttributes));
        }
        else
        {
            auction.FlatenedNBT = new();
        }
        return auction;
    }
}
