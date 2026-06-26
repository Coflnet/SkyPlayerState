using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.Extensions.Logging;

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
        Logger.LogInformation("Saved storage item for player {playerId} in chest {chestName}, items: {itemCount} at position {position}", args.currentState.PlayerId, chestView.Name, itemsToStore, chestView.Position);
    }

    public static bool IsNotStorage(Models.ChestView chestView)
    {
        return !chestView.Name.StartsWith("Ender Chest") && !chestView.Name.Contains("Backpack (Slot")
        && chestView.Name != "Chest" && chestView.Name != "Large Chest" //island chests
        && chestView.Name != "Chest Storage" && chestView.Name != "Medium Shelves" && !chestView.Name.Contains("Chest+") //furniture
        ;
    }
}
