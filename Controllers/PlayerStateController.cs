using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections;
using Coflnet.Sky.PlayerState.Services;
using System.Dynamic;
using System.Collections.Generic;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.PlayerState.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class PlayerStateController : ControllerBase
    {
        private readonly IPersistenceService service;

        /// <summary>
        /// Creates a new instance of <see cref="PlayerStateController"/>
        /// </summary>
        /// <param name="service"></param>
        public PlayerStateController(IPersistenceService service)
        {
            this.service = service;
        }

        /// <summary>
        /// Retrieves bazaar order state
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("{playerId}/bazaar")]
        public async Task<List<Bazaar.Offer>> TrackFlip(string playerId)
        {
            var data = await service.GetStateObject(playerId);
            return data.BazaarOffers;
        }
        [HttpGet]
        [Route("{playerId}")]
        public async Task<StateObject> GetFullState(string playerId)
        {
            return await service.GetStateObject(playerId);
        }

        [HttpGet]
        [Route("{playerId}/lastChest")]
        public async Task<List<Coflnet.Sky.PlayerState.Models.Item>> GetInventory(string playerId)
        {
            var data = await service.GetStateObject(playerId);
            return data?.RecentViews?.LastOrDefault()?.Items ?? throw new CoflnetException("no_inventory", $"No inventory found for {playerId}. Make sure you use the CoflMod and opened your inventory.");
        }
        [HttpGet]
        [Route("{playerId}/limits")]
        public async Task<LimitsSummary> GetLimits(string playerId)
        {
            var data = await service.GetStateObject(playerId);
            return data?.Limits;
        }
        [HttpGet("{playerId}/skills")]
        public async Task<SkillService.Skill[]> GetSkills(Guid playerId, [FromServices] SkillService service)
        {
            return await service.GetSkills(playerId);
        }

        [HttpGet]
        [Route("{playerId}/extracted")]
        public async Task<ExtractedInfo> GetPlayerData(string playerId)
        {
            var data = await service.GetStateObject(playerId);
            return data?.ExtractedInfo ?? throw new CoflnetException("no_player_data", $"No player data found for {playerId}. Make sure you use the CoflMod and opened the skyblock menu.");
        }

        [HttpGet]
        [Route("{playerId}/profit/location")]
        public async Task<List<TrackedProfitService.Period>> GetLocationProfit(string playerId, [FromServices] TrackedProfitService service)
        {
            var data = await service.GetPeriodsForPlayer(playerId);
            if (data == null)
                throw new CoflnetException("no_location_profit", $"No location profit found for {playerId}. Make sure you use the CoflMod.");
            return data;
        }

        [HttpGet]
        [Route("{playerId}/profit/history")]
        public async Task<List<TrackedProfitService.Period>> GetHistoryProfit(string playerId, DateTime before, int count, [FromServices] TrackedProfitService service)
        {
            return await service.GetHistoryForPlayer(playerId, before, count);
        }

    }
}
