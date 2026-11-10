using System;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Feeds the Crystal Nucleus chat lines into <see cref="NucleusRunTracker"/>. Registered after
/// <see cref="ProfileSwitchListener"/> so a profile switch in the same batch is already known.
/// </summary>
public class NucleusRunListener : UpdateListener
{
    /// <summary>Enrichment only, a failure must not drop the update</summary>
    public override bool Optional => true;

    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Kind != Models.UpdateMessage.UpdateKind.CHAT || args.msg.ChatBatch == null)
            return Task.CompletedTask;
        var at = args.msg.ReceivedAt == default ? DateTime.UtcNow : args.msg.ReceivedAt;
        // the claim is matched against location fragments, which are stamped with the processing time
        NucleusRunTracker.Apply(args.currentState.ExtractedInfo, args.msg.ChatBatch, at, DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
