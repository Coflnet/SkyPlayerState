using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public class TabListUpdate : UpdateListener
{
    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Tab != null)
            args.currentState.LastTab = args.msg.Tab;

        return Task.CompletedTask;
    }
}