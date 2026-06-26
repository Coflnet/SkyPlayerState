using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class SettingsListener : UpdateListener
{
    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        args.currentState.Settings = args.msg.Settings;
        Logger.LogDebug("Settings of {name} updated to {settings}", args.currentState.McInfo.Name, JsonConvert.SerializeObject(args.currentState.Settings));
        return Task.CompletedTask;
    }
}