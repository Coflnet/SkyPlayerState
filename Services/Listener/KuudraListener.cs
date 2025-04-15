using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public class KuudraListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if(args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            await CheckKuudra(args);
    }

    private async Task CheckKuudra(UpdateArgs args)
    {
        if (args.msg.Scoreboard == null)
            return;
        var kuudra = args.msg.Scoreboard.Any(s => s.StartsWith(" ⏣ Kuudra's Hollow"));
        Console.WriteLine($"Started kuudra {args.msg.PlayerId} {kuudra}");
    }
}
