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
        // Temporary diagnostics: dump the full unprocessed chest view for a specific
        // player whenever any backpack is opened, so we can analyse why items appear
        // to be missing (e.g. pagination / trim logic). Remove once analysed.
        if (chestView.Name.Contains("Backpack") &&
            (string.Equals(args.currentState.PlayerId, "SpectChicken", System.StringComparison.OrdinalIgnoreCase)
             || string.Equals(args.currentState.McInfo?.Name, "SpectChicken", System.StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var blob = System.Text.Json.JsonSerializer.Serialize(chestView);
                Logger.LogInformation("Full backpack blob for SpectChicken chest {chestName} (rawItemCount {rawCount}): {blob}",
                    chestView.Name, chestView.Items.Count, blob);
            }
            catch (System.Exception e)
            {
                Logger.LogError(e, "Failed to serialize full backpack blob for SpectChicken chest {chestName}", chestView.Name);
            }
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
