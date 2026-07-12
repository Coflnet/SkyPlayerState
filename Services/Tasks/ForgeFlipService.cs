using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Crafts.Client.Model;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Port of the mod side ForgeFlipService. Instead of calling the PlayerState HTTP api
/// for the heart of the mountain tier (which would be a self call) the local
/// <see cref="ExtractedInfo"/> is used directly.
/// </summary>
public class ForgeFlipService
{
    private readonly IForgeApi forgeApi;
    private readonly ProfileForgeClient profileClient;

    public ForgeFlipService(IForgeApi forgeApi, ProfileForgeClient profileClient)
    {
        this.forgeApi = forgeApi;
        this.profileClient = profileClient;
    }

    public async Task<IEnumerable<ForgeFlip>> GetForgeFlips(string mcUuid, ExtractedInfo extractedInfo, string profile = "current")
    {
        var forgeUnlockedTask = profileClient.GetForgeData(mcUuid, profile);
        var forgeFlips = await forgeApi.GetAllForgeAsync();
        if (mcUuid == null)
            return forgeFlips;
        var unlocked = await forgeUnlockedTask;
        if (extractedInfo?.HeartOfTheMountain?.Tier > 0)
            unlocked.HotMLevel = extractedInfo.HeartOfTheMountain.Tier;
        var result = new List<ForgeFlip>();
        foreach (var item in forgeFlips)
        {
            if (unlocked.HotMLevel < item.RequiredHotMLevel)
                continue;
            if (item.ProfitPerHour <= 0)
                continue;
            if (unlocked.QuickForgeSpeed != 0)
            {
                item.Duration = (int)((float)item.Duration * unlocked.QuickForgeSpeed);
            }
            if (item.ProfitPerHour > 1_000_000_000) // probably a calculation error, use daily volume instead
                item.ProfitPerHour = (item.CraftData.SellPrice - item.CraftData.CraftCost) * item.CraftData.Volume;
            result.Add(item);
        }
        return result.OrderByDescending(r => r.ProfitPerHour);
    }
}

/// <summary>
/// Slim profile service client, only the forge data lookup the task system needs.
/// </summary>
public class ProfileForgeClient
{
    private readonly RestClient profileClient;
    private readonly ILogger<ProfileForgeClient> logger;

    public ProfileForgeClient(IConfiguration config, ILogger<ProfileForgeClient> logger)
    {
        profileClient = new RestClient(config["PROFILE_BASE_URL"] ?? "http://localhost:5014");
        this.logger = logger;
    }

    public virtual async Task<ForgeData> GetForgeData(string playerId, string profile)
    {
        try
        {
            var request = new RestRequest($"api/profile/{playerId}/{profile}/data/forge", Method.Get);
            var response = await profileClient.ExecuteAsync<ForgeData>(request);
            return response.Data ?? new ForgeData();
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to load forge data for {playerId}, using defaults", playerId);
            return new ForgeData();
        }
    }

    public class ForgeData
    {
        public int HotMLevel { get; set; }
        public float QuickForgeSpeed { get; set; }
    }
}
