using System.Linq;
using System.Threading.Tasks;
using Castle.Core.Logging;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class KatInventoryListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Pet Sitter")
            return;

        if (!(args.msg.Chest.Items[13].ItemName?.Contains("Lvl") ?? false))
        {
            args.GetService<ILogger<KatInventoryListener>>()
                .LogInformation("Kat empty for {PlayerId}", args.currentState.PlayerId);
            args.currentState.ExtractedInfo.KatStatus = new(); // empty object = no kat but know that its none
            return;
        }
    }
}
