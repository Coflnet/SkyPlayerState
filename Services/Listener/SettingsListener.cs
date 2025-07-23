using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class SettingsListener : UpdateListener
{
    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        args.currentState.Settings = args.msg.Settings;
        Console.WriteLine($"Settings of {args.currentState.McInfo.Name} updated to {JsonConvert.SerializeObject(args.currentState.Settings)}");
        return Task.CompletedTask;
    }
}