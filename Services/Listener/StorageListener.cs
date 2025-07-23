using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Sniper.Client.Api;

namespace Coflnet.Sky.PlayerState.Services;

public class StorageListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind != Models.UpdateMessage.UpdateKind.INVENTORY)
        {
            return;
        }
        var chestView = args.msg.Chest;
        if (chestView.Name == null || IsNotStorage(chestView))
        {
            return; // Only process Ender Chest or Storage chests
        }
        // there can be smaller backpacks or only partial enderchets
        var itemsToStore = (chestView.Items.Count / 9 - 4) * 9;
        await args.GetService<StorageService>().SaveStorageItem(new()
        {
            ChestName = chestView.Name,
            Position = chestView.Position,
            Items = chestView.Items.Take(itemsToStore).ToList(),
            OpenedAt = chestView.OpenedAt,
            PlayerId = args.currentState.McInfo.Uuid
        });
        Console.WriteLine($"Saved storage item for player {args.currentState.PlayerId} in chest {chestView.Name}, items: {itemsToStore} at position {chestView.Position}");
    }

    public static bool IsNotStorage(Models.ChestView chestView)
    {
        return !chestView.Name.StartsWith("Ender Chest") && !chestView.Name.Contains("Backpack (Slot") && !chestView.Name.Contains("Chest");
    }
}
