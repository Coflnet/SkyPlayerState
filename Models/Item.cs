using System.Collections.Generic;
using System.Dynamic;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Coflnet.Sky.PlayerState.Models;
#nullable enable
/// <summary>
/// Transfer Object for item data
/// </summary>
[MessagePackObject]
public class Item
{
    /// <summary>
    /// 
    /// </summary>
    [Key(0)]
    public long? Id { get; set; }
    /// <summary>
    /// The item name for display
    /// </summary>
    [Key(1)]
    [BsonElement("Name")]
    public string? ItemName { get; set; } = null!;
    /// <summary>
    /// Hypixel item tag for this item
    /// </summary>
    [Key(2)]
    public string Tag { get; set; } = null!;
    /// <summary>
    /// Other aditional attributes
    /// </summary>
    [Key(3)]
    public Dictionary<string, object>? ExtraAttributes { get; set; }

    /// <summary>
    /// Enchantments if any
    /// </summary>
    [Key(4)]
    public Dictionary<string, byte>? Enchantments { get; set; }  = new();
    /// <summary>
    /// Color element
    /// </summary>
    [Key(5)]
    public int? Color { get; set; } 
    /// <summary>
    /// Item Description aka Lore displayed in game, is a written form of <see cref="ExtraAttributes"/>
    /// </summary>
    [Key(6)]
    public string? Description { get; set; }
    /// <summary>
    /// Stacksize
    /// </summary>
    [Key(7)]
    public int? Count { get; set; }

    public Item(Item item)
    {
        Id = item.Id;
        ItemName = item.ItemName;
        Tag = item.Tag;
        ExtraAttributes = item.ExtraAttributes == null ? null : new Dictionary<string, object>(item.ExtraAttributes);
        Enchantments = item.Enchantments == null ? null : new Dictionary<string, byte>(item.Enchantments);
        Color = item.Color;
        Description = item.Description;
        Count = item.Count;
    }

    public Item()
    {
    }
}
#nullable restore