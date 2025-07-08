using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Services;

public class CassandraItemCompare : IEqualityComparer<CassandraItem>
{
    public bool Equals(CassandraItem? x, CassandraItem? y)
    {
        return x != null && y != null && JToken.DeepEquals(Normalize(x), Normalize(y))
           && (x.Enchantments == y.Enchantments || y.Enchantments?.Count == x.Enchantments?.Count && y.Enchantments != null && x.Enchantments != null && !x.Enchantments.Except(y.Enchantments).Any())
           && x.Color == y.Color
           && x.Tag == y.Tag
           && (x.ItemName == null || y.ItemName == null || x.ItemName == y.ItemName);
    }

    private static JObject Normalize(CassandraItem? x)
    {
        var left = JsonConvert.DeserializeObject<JObject>(x?.ExtraAttributesJson ?? "{}");
        left!.Remove("drill_fuel");
        left.Remove("compact_blocks");
        left.Remove("bottle_of_jyrre_seconds");
        left.Remove("bottle_of_jyrre_last_update");
        left.Remove("builder's_ruler_data");
        left.Remove("champion_combat_xp");
        left.Remove("farmed_cultivating");
        left.Remove("mined_crops");
        if(left.TryGetValue("petInfo", out var petInfoGeneric) && petInfoGeneric is JObject petInfo)
        {
            petInfo.Remove("active");
            petInfo.Remove("noMove");
            petInfo.Remove("uniqueId");
            petInfo.Remove("exp");
            petInfo.Remove("noMove");
            petInfo.Remove("hideInfo");
            petInfo.Remove("hideRightClick");
            left.Remove("timestamp");
            left.Remove("tier");
        }
        foreach (var item in left.Properties().ToList())
        {
            if (item.Value.Type == JTokenType.Integer && item.Value.Value<long>() > 20)
                left.Remove(item.Name);
            // also for float 
            if (item.Value.Type == JTokenType.Float)
                left.Remove(item.Name);
            if(item.Name.EndsWith("_data") || item.Name.StartsWith("personal_deletor_"))
                left.Remove(item.Name);
        }
        return left;
    }

    int IEqualityComparer<CassandraItem>.GetHashCode(CassandraItem obj)
    {
        var left = Normalize(obj);
        var hash = 17;
        foreach (var item in left)
        {
            hash = hash * 23 + item.Key.GetHashCode();
            if (item.Value.Type == JTokenType.Integer)
                hash = hash * 23 + unchecked((int)item.Value.Value<long>());
            else if (item.Value.Type == JTokenType.Float)
                hash = hash * 23 + item.Value.Value<double>().GetHashCode();
            else if (item.Value.Type == JTokenType.String)
                hash = hash * 23 + item.Value.Value<string>()?.GetHashCode() ?? 0;
            else
                // for nested objects
                hash = hash * 23 + item.Value.ToString().GetHashCode();
        }
        foreach (var item in obj.Enchantments ?? new Dictionary<string, int>())
        {
            hash = hash * 23 + item.Key.GetHashCode() + item.Value;
        }
        return HashCode.Combine(hash, obj.Tag, obj.ItemName);
    }
}
