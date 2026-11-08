using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Keeps the 36 player slots (main inventory, then hotbar) of every upload in <see cref="Models.StateObject.Inventory"/>.
/// Uses <see cref="InventoryLayout"/>: taking the last 36 slots of a 46 slot (1.21) bare inventory kept the offhand
/// and dropped the first main slot.
/// </summary>
public class InventoryChangeUpdate : UpdateListener
{
    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        args.currentState.Inventory = InventoryLayout.PlayerInventory(args.msg.Chest.Items);
        return Task.CompletedTask;
    }
}
