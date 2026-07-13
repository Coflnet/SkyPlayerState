using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerState.Bazaar;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Removes everything stored about a single player, used when the account behind it is deleted.
/// The stores are split across two keys: the state blob and the coin counters are keyed by the
/// player name (that is what the mod reports as playerId), everything else by the minecraft uuid.
/// </summary>
public class PlayerDataDeletionService
{
    private readonly IPersistenceService persistenceService;
    private readonly StorageService storageService;
    private readonly SkillService skillService;
    private readonly TrackedProfitService trackedProfitService;
    private readonly IBazaarProfitTracker bazaarProfitTracker;
    private readonly Tasks.TaskAggregateService taskAggregateService;
    private readonly ICoinCounterService coinCounterService;
    private readonly ITransactionService transactionService;
    private readonly PlayerStateBackgroundService backgroundService;
    private readonly ILogger<PlayerDataDeletionService> logger;

    public PlayerDataDeletionService(
        IPersistenceService persistenceService,
        StorageService storageService,
        SkillService skillService,
        TrackedProfitService trackedProfitService,
        IBazaarProfitTracker bazaarProfitTracker,
        Tasks.TaskAggregateService taskAggregateService,
        ICoinCounterService coinCounterService,
        ITransactionService transactionService,
        PlayerStateBackgroundService backgroundService,
        ILogger<PlayerDataDeletionService> logger)
    {
        this.persistenceService = persistenceService;
        this.storageService = storageService;
        this.skillService = skillService;
        this.trackedProfitService = trackedProfitService;
        this.bazaarProfitTracker = bazaarProfitTracker;
        this.taskAggregateService = taskAggregateService;
        this.coinCounterService = coinCounterService;
        this.transactionService = transactionService;
        this.backgroundService = backgroundService;
        this.logger = logger;
    }

    /// <summary>
    /// Deletes all data of a player.
    /// </summary>
    /// <param name="playerId">the player name the state is stored under, may be null if unknown</param>
    /// <param name="playerUuid">the minecraft uuid, may be <see cref="Guid.Empty"/> if unknown</param>
    public async Task<DeletionSummary> DeletePlayer(string playerId, Guid playerUuid)
    {
        var summary = new DeletionSummary();
        if (!StateObject.IsAnonymous(playerId))
        {
            // the profile ids are part of the storage partition key and only known from the state,
            // so the state has to be read before it is dropped
            var state = await persistenceService.GetStateObject(playerId);
            summary.ProfileIds = state.Profiles?.Select(p => p.Uuid).Where(u => u != Guid.Empty).ToList() ?? new();
            if (playerUuid == Guid.Empty && state.McInfo != null)
                playerUuid = state.McInfo.Uuid;
            else if (playerUuid != Guid.Empty && state.McInfo != null && state.McInfo.Uuid != Guid.Empty
                && state.McInfo.Uuid != playerUuid)
                throw new CoflnetException("uuid_mismatch",
                    $"The uuid {playerUuid} does not match the uuid {state.McInfo.Uuid} stored for player {playerId}, refusing to delete");

            // evict the live copy first, otherwise the next update would just save it again
            backgroundService.States.TryRemove(playerId, out _);
            await persistenceService.DeleteStateObject(playerId);
            await coinCounterService.DeleteCounters(playerId);
            summary.StateDeleted = true;
        }
        if (playerUuid == Guid.Empty)
        {
            logger.LogWarning("Deleted player state of {playerId} without a uuid, uuid keyed data was kept", playerId);
            return summary;
        }
        summary.PlayerUuid = playerUuid;
        if (summary.ProfileIds.Count > 0)
            await storageService.DeleteStorage(playerUuid, summary.ProfileIds);
        await skillService.DeleteSkills(playerUuid);
        await transactionService.DeletePlayerTransactions(playerUuid);
        await bazaarProfitTracker.DeletePlayer(playerUuid);
        // the profit and task tables store the uuid in its dashless form
        var dashless = playerUuid.ToString("N");
        await trackedProfitService.DeletePlayer(dashless);
        await taskAggregateService.DeletePlayerStats(dashless);
        summary.UuidDataDeleted = true;
        logger.LogInformation("Deleted all data of player {playerId} ({uuid})", playerId, playerUuid);
        return summary;
    }

    /// <summary>
    /// What a <see cref="DeletePlayer"/> call removed
    /// </summary>
    public class DeletionSummary
    {
        /// <summary>
        /// True when the state blob, its redis mirror and the coin counters were removed
        /// </summary>
        public bool StateDeleted { get; set; }
        /// <summary>
        /// True when the uuid keyed data (storage, skills, transactions, bazaar, profit, tasks) was removed
        /// </summary>
        public bool UuidDataDeleted { get; set; }
        /// <summary>
        /// The uuid the data was removed for, <see cref="Guid.Empty"/> if none was known
        /// </summary>
        public Guid PlayerUuid { get; set; }
        /// <summary>
        /// The profiles the stored chests were removed for
        /// </summary>
        public List<Guid> ProfileIds { get; set; } = new();
    }
}
