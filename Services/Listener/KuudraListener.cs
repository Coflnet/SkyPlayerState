using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class KuudraListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            await CheckKuudra(args);
        if(args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
            await GotInventory(args);
    }

    private async Task GotInventory(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Paid Chest")
            return;
        Console.WriteLine($"Got paid chest {args.msg.PlayerId} {JsonConvert.SerializeObject(args.msg.Chest)}");
    }

    private async Task CheckKuudra(UpdateArgs args)
    {
        if (args.msg.Scoreboard == null)
            return;
        var kuudra = args.msg.Scoreboard.Any(s => s.StartsWith(" ⏣ Kuudra's Hollow"));
        if(!kuudra)
            return;
        Console.WriteLine($"Started kuudra {args.msg.PlayerId} {string.Join(" ", args.msg.Scoreboard)}");
    }
}
