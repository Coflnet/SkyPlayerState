using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

public class HuntingListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest == null || chest.Name == null || args.msg.UserId == null)
            return;

        var items = chest.Items;
        if (items == null || items.Count == 0)
            return;

        if (chest.Name.Contains("Huntaxe"))
        {
            var weaponItem = items[22]; // Huntaxe is always in slot 23 (index 22)
            Logger.LogInformation("Setting WeaponInHuntaxe for player {playerId} to {itemName}", args.currentState.PlayerId, weaponItem.ItemName);
            args.currentState.ExtractedInfo.WeaponInHuntaxe = weaponItem;
        }
        if (!chest.Name.StartsWith("Hunting Toolkit"))
            return;
        args.currentState.ExtractedInfo.HuntingToolkitItems = items.Take(3 * 9).Where(i => i?.Tag != null).ToArray();
    }
}