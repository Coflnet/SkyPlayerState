using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Unlocks an achievement carried by an <see cref="UpdateMessage.UpdateKind.Achievement"/> update.
/// Detections that happen outside this service (e.g. lowballing, handled by the mod backend) send such
/// a message; because the pipeline is partitioned by player id it is processed on the instance that
/// holds the players live state, so the unlock survives concurrent saves.
/// </summary>
public class AchievementListener : UpdateListener
{
    public override Task Process(UpdateArgs args)
    {
        var id = args.msg.AchievementId;
        if (string.IsNullOrEmpty(id))
            return Task.CompletedTask;
        if (!Enum.TryParse<Achievement>(id, ignoreCase: false, out var achievement) || !Enum.IsDefined(achievement))
        {
            args.GetService<ILogger<AchievementListener>>().LogWarning("Received unknown achievement id {id} for {player}", id, args.msg.PlayerId);
            return Task.CompletedTask;
        }
        if (args.GetService<IAchievementService>().Unlock(args.currentState, achievement))
            args.GetService<ILogger<AchievementListener>>().LogInformation("Unlocked achievement {achievement} for {player}", achievement, args.msg.PlayerId);
        return Task.CompletedTask;
    }
}
