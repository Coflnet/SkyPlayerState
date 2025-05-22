using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Models;

public interface ICassandraItem
{
    Guid ItemId { get; set; }
    long? Id { get; set; }
    string? ItemName { get; set; }
    string Tag { get; set; }
    string ExtraAttributesJson { get; set; }
    Dictionary<string, int>? Enchantments { get; set; }
    int? Color { get; set; }
}
#nullable enable
/// <summary>
/// see <see cref="TransactionService.GetItemsTable"/> for key definition
/// </summary>
public class CassandraItem : ICassandraItem
{
    public Guid ItemId { get; set; }
    /// <summary>
    /// Numeric internal id
    /// </summary>
    public long? Id { get; set; }
    public string? ItemName { get; set; }
    public string Tag { get; set; } = null!;
    public string ExtraAttributesJson { get; set; } = null!;
    public Dictionary<string, int>? Enchantments { get; set; }
    public int? Color { get; set; }

    public CassandraItem(Item item)
    {
        ItemId = item.ExtraAttributes?.TryGetValue("uuid", out var uuid) == true ? Guid.Parse(uuid.ToString()!) : default;
        ItemName = item.ItemName;
        Tag = item.Tag;
        Enchantments = item.Enchantments?.OrderBy(k => k.Key).ToDictionary(x => x.Key, x => (int)x.Value) ?? new Dictionary<string, int>();
        Color = item.Color;
        Id = item.Id;
        ExtraAttributesJson = Newtonsoft.Json.JsonConvert.SerializeObject(item.ExtraAttributes);
    }

    public CassandraItem()
    {
    }

    internal Item ToTransfer()
    {
        return new Item()
        {
            Color = Color,
            Enchantments = Enchantments?.ToDictionary(x => x.Key, x => (byte)x.Value),
            ExtraAttributes = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(ExtraAttributesJson)?
                .ToDictionary(x=>x.Key, x => ConvertJTokenToNative(JToken.FromObject(x.Value))),
            Id = Id,
            ItemName = ItemName,
            Tag = Tag,
            Count = 1
        };
    }

        public static object ConvertJTokenToNative(JToken token)
    {
        if (token is JValue jValue)
        {
            // JValue represents a primitive value (string, number, boolean, null)
            // The Value property will already be a .NET native type.
            // For dates, Newtonsoft might deserialize them as DateTime objects if recognized.
            // If they are strings, they'll remain strings.
            return jValue.Value;
        }
        else if (token is JArray jArray)
        {
            // Convert JArray to List<object>
            var list = new List<object>();
            foreach (var item in jArray)
            {
                list.Add(ConvertJTokenToNative(item));
            }
            return list;
        }
        else if (token is JObject jObject)
        {
            // Convert JObject to Dictionary<string, object>
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); // Or your preferred comparer
            foreach (var property in jObject.Properties())
            {
                dict[property.Name] = ConvertJTokenToNative(property.Value);
            }
            return dict;
        }
        else if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }
        else
        {
            // Should not happen with standard JSON deserialization
            throw new InvalidOperationException("Unsupported JToken type: " + token.GetType());
        }
    }
}


#nullable restore