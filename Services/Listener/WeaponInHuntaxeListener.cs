using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public class WeaponInHuntaxeListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        var chest = args.msg.Chest;
        if (chest == null || chest.Name == null || !chest.Name.Contains("Huntaxe") || args.msg.UserId == null)
            return;

        var items = chest.Items;
        if (items == null || items.Count == 0)
            return;

        var weaponItem = items[22]; // Huntaxe is always in slot 23 (index 22)
        Console.WriteLine($"Setting WeaponInHuntaxe for player {args.currentState.PlayerId} to {weaponItem.ItemName}");
        args.currentState.ExtractedInfo.WeaponInHuntaxe = weaponItem;
    }
}