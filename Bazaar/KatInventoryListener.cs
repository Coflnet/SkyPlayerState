using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class KatInventoryListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Pet Sitter")
            return;

        if (!(args.msg.Chest.Items[13].ItemName?.Contains("Lvl") ?? false))
        {
            args.currentState.ExtractedInfo.KatStatus = new(); // empty object = no kat but know that its none
            return;
        }
    }
}
