using System.Collections.Generic;
using Coflnet.Sky.Core;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

[MessagePackObject]
public class ChestView
{
    /// <summary>
    /// All items in the ui view
    /// </summary>
    [Key(0)]
    public List<Item> Items = new();
    /// <summary>
    /// Name of the chest
    /// </summary>
    [Key(1)]
    public string Name = string.Empty;
    /// <summary>
    /// Position of the chest in the world
    /// </summary>
    [Key(2)]
    public BlockPos? Position;
    [Key(3)]
    public DateTime OpenedAt;
    /// <summary>
    /// Index into <see cref="Items"/> of the item the player holds (selected hotbar slot), null when the mod does not send it
    /// (older mods). Marks the optimal tool as held (evidence heldSlot), see <see cref="Services.Gear.OptimalTool"/>.
    /// </summary>
    [Key(4)]
    public int? HeldSlot;
}
#nullable restore