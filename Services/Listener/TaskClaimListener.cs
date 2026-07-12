using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Applies a manual task claim carried by a <see cref="UpdateMessage.UpdateKind.TaskClaim"/> update.
/// The claim biases the classifier (it wins any tie it matches). A null value clears it.
/// Routed through the state pipeline so it lands on the instance holding the player's live state.
/// </summary>
public class TaskClaimListener : UpdateListener
{
    public override Task Process(UpdateArgs args)
    {
        var claim = args.msg.ClaimedTask;
        args.currentState.ExtractedInfo.ClaimedTask = string.IsNullOrWhiteSpace(claim) ? null : claim;
        args.currentState.ExtractedInfo.ClaimedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
