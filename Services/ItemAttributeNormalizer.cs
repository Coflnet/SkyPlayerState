using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Provides common logic for normalizing item attributes by removing volatile/transient fields
/// that should not affect item identity matching. Used by both ItemCompare and CassandraItemCompare.
/// </summary>
public static class ItemAttributeNormalizer
{
    /// <summary>
    /// List of attribute keys that are volatile and should be removed before comparison.
    /// </summary>
    public static readonly HashSet<string> VolatileAttributeKeys = new()
    {
        "drill_fuel",
        "compact_blocks",
        "bottle_of_jyrre_seconds",
        "bottle_of_jyrre_last_update",
        "builder's_ruler_data",
        "champion_combat_xp",
        "farmed_cultivating",
        "mined_crops",
        "timestamp",
        "tier"
    };

    /// <summary>
    /// Removes volatile attributes from a Dictionary{string, object}.
    /// </summary>
    public static Dictionary<string, object>? NormalizeDictionary(Dictionary<string, object>? attrs)
    {
        if (attrs == null)
            return null;
        
        var result = new Dictionary<string, object>(attrs);
        
        // Convert any nested JObjects to dictionaries for consistent comparison
        foreach (var key in result.Keys.ToList())
        {
            if (result[key] is JObject jobj)
            {
                result[key] = jobj.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            }
        }
        
        RemoveVolatileKeys(result);
        return result;
    }

    /// <summary>
    /// Removes volatile attributes from a JObject.
    /// </summary>
    public static JObject NormalizeJObject(JObject obj)
    {
        var left = (JObject)obj.DeepClone();
        RemoveVolatileKeysFromJObject(left);
        return left;
    }

    private static void RemoveVolatileKeys(Dictionary<string, object> attrs)
    {
        foreach (var key in VolatileAttributeKeys)
            attrs.Remove(key);

        // Handle petInfo nested object - deeply clean volatile fields from within it
        if (attrs.TryGetValue("petInfo", out var petInfoGeneric))
        {
            if (petInfoGeneric is Dictionary<string, object> petInfo)
            {
                RemoveVolatilePetInfoKeys(petInfo);
            }
            else if (petInfoGeneric is JObject petInfoJObj)
            {
                RemoveVolatilePetInfoKeysFromJObject(petInfoJObj);
            }
            // For other types, we need to normalize them as well
            // This handles the case where Newtonsoft creates intermediate types
        }

        // Remove personal_deletor and *_data fields
        var keysToRemove = attrs.Keys.Where(k => k.EndsWith("_data") || k.StartsWith("personal_deletor_")).ToList();
        foreach (var key in keysToRemove)
            attrs.Remove(key);
    }

    private static void RemoveVolatilePetInfoKeys(Dictionary<string, object> petInfo)
    {
        petInfo.Remove("active");
        petInfo.Remove("noMove");
        petInfo.Remove("uniqueId");
        petInfo.Remove("exp");
        petInfo.Remove("hideInfo");
        petInfo.Remove("hideRightClick");
    }

    private static void RemoveVolatilePetInfoKeysFromJObject(JObject petInfo)
    {
        petInfo.Remove("active");
        petInfo.Remove("noMove");
        petInfo.Remove("uniqueId");
        petInfo.Remove("exp");
        petInfo.Remove("hideInfo");
        petInfo.Remove("hideRightClick");
    }

    private static void RemoveVolatileKeysFromJObject(JObject obj)
    {
        foreach (var key in VolatileAttributeKeys)
            obj.Remove(key);

        // Handle petInfo nested object
        if (obj.TryGetValue("petInfo", out var petInfoGeneric) && petInfoGeneric is JObject petInfo)
        {
            petInfo.Remove("active");
            petInfo.Remove("noMove");
            petInfo.Remove("uniqueId");
            petInfo.Remove("exp");
            petInfo.Remove("hideInfo");
            petInfo.Remove("hideRightClick");
        }

        // Remove fields with values > 20 and all floats, and pattern-matched keys
        foreach (var item in obj.Properties().ToList())
        {
            if (item.Value.Type == JTokenType.Integer && item.Value.Value<long>() > 20)
                obj.Remove(item.Name);
            else if (item.Value.Type == JTokenType.Float)
                obj.Remove(item.Name);
            else if (item.Name.EndsWith("_data") || item.Name.StartsWith("personal_deletor_"))
                obj.Remove(item.Name);
        }
    }
}
