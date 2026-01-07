using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#nullable enable
namespace Coflnet.Sky.PlayerState.Services;
public class ItemCompare : IEqualityComparer<Item>
{
    private ElementComparer internalComparer = new();
    public bool Equals(Item? x, Item? y)
    {
        return x != null && y != null &&
            (AttributeMatch(x, y))
           && EnchantMatch(x, y)
           && x.Color == y.Color
           && x.Tag == y.Tag;
    }

    private bool AttributeMatch(Item x, Item y)
    {
        var xNorm = ItemAttributeNormalizer.NormalizeDictionary(x.ExtraAttributes);
        var yNorm = ItemAttributeNormalizer.NormalizeDictionary(y.ExtraAttributes);
        var matches = xNorm == null && yNorm == null || xNorm != null && yNorm != null && xNorm.Count == yNorm.Count && !xNorm.Except(yNorm, internalComparer).Any();
        if (matches)
            return true;
        return false;
    }

    private static bool EnchantMatch(Item? x, Item? y)
    {
        return (y?.Enchantments?.Count ?? 0) == 0 && x?.Enchantments == null || (y?.Enchantments != null && x?.Enchantments != null &&
            y.Enchantments?.Count == x.Enchantments?.Count
                && x.Enchantments!.Sum(x => x.Value) == y.Enchantments!.Sum(x => x.Value) && !x.Enchantments!.Except(y.Enchantments!).Any());
    }

    public int GetHashCode(Item obj)
    {
        return HashCode.Combine(obj.ExtraAttributes?.GetValueOrDefault("uuid"), obj.Tag);
    }

    public class ElementComparer : IEqualityComparer<KeyValuePair<string, object>>
    {
        public bool Equals(KeyValuePair<string, object> x, KeyValuePair<string, object> y)
        {
            if (x.Value is IEnumerable<KeyValuePair<object, object>> dumb)
                x = new(x.Key, dumb.Select(a => new KeyValuePair<string, object>(a.Key.ToString()!, a.Value)));
            if (y.Value is IEnumerable<KeyValuePair<object, object>> dumb2)
                y = new(y.Key, dumb2.Select(a => new KeyValuePair<string, object>(a.Key.ToString()!, a.Value)));

            if (x.Value is IEnumerable<object> jx && y.Value is IEnumerable<object> jy)
            {
                var combined = jx.Zip(jy);
                foreach (var item in combined)
                {
                    var second = item.Second;
                    var first = item.First;
                    if (item.Second is Dictionary<object, object> dict)
                        second = dict.ToDictionary(x => x.Key.ToString()!, x => x.Value);
                    if (item.First is Dictionary<object, object> dict2)
                        first = dict2.ToDictionary(x => x.Key.ToString()!, x => x.Value);
                    if (item.First is KeyValuePair<string, object> el1 && second is KeyValuePair<string, object> el2)
                    {
                        if (!Equals(el1, el2))
                            return false;
                    }
                    else if (item.First is Dictionary<string, object> list1 && second is Dictionary<string, object> list2)
                    {
                        if (!this.Equal(list1, list2))
                            return false;
                    }
                    else if (!Equals(item.First, item.Second))
                        return false;
                }
                return true;
            }

            return x.Key == y.Key && (x.Value.Equals(y.Value)
                || x.Value is IEnumerable<KeyValuePair<string, object>> xi && y.Value is IEnumerable<KeyValuePair<string, object>> yi && this.Equal(xi, yi)
                || x.Value is IEnumerable<object> xe && y.Value is IEnumerable<object> ye && Enumerable.SequenceEqual<object>(xe, ye)
                || IsNumeric(x.Value) && Convert.ToInt64(x.Value) == Convert.ToInt64(y.Value)
            );
        }

        private static bool IsNumeric(object x)
        {
            return x is byte || x is short || x is int || x is long || x is ushort || x is uint || x is ulong || x is double || x is float;
        }


        private bool Equal(IEnumerable<KeyValuePair<string, object>> a, IEnumerable<KeyValuePair<string, object>> b)
        {
            var combined = a.Zip(b);
            foreach (var item in combined)
            {
                if (!Equals(item.First, item.Second))
                    return false;
            }
            return true;
        }

        public int GetHashCode([DisallowNull] KeyValuePair<string, object> obj)
        {
            var code = 0;
            if (obj.Value is IEnumerable col && obj.Value is not string)
                foreach (var p in col)
                    if (p is KeyValuePair<string, object> xi)
                        code ^= GetHashCode(xi);
                    else if (p is KeyValuePair<object, object> xo)
                        code ^= GetHashCode(new KeyValuePair<string, object>(xo.Key.ToString()!, xo.Value));
                    else if (p is Dictionary<string, object> dict)
                        foreach (var item in dict)
                        {
                            code ^= GetHashCode(item);
                        }
                    else if (p is Dictionary<object, object> dict2)
                        foreach (var item in dict2)
                        {
                            code ^= GetHashCode(new KeyValuePair<string, object>(item.Key.ToString()!, item.Value));
                        }
                    else if (p is JProperty childObj)
                    {
                        var type = childObj.GetType();
                        code ^= GetHashCode(new KeyValuePair<string, object>(childObj.Name, childObj.Value));
                    }
                    else if (p is JToken token)
                    {
                        if (token.Type == JTokenType.String)
                            code ^= token.Value<string>()?.GetHashCode() ?? 0;
                        else if (token.Type == JTokenType.Integer)
                            code ^= token.Value<int>();
                        else if (token.Type == JTokenType.Float)
                            code ^= token.Value<double>().GetHashCode();
                        else if (token.Type == JTokenType.Boolean)
                            code ^= token.Value<bool>().GetHashCode();
                        else if (token.Type == JTokenType.Object)
                        {
                            code ^= token.First?.Value<object>()?.ToString()?.GetHashCode() ?? 0;
                        }
                        else
                        {
                            code ^= token.ToString().GetHashCode();
                        }
                    }
                    else
                    {
                        var type = p.GetType();
                        code ^= p.GetHashCode();
                    }
            else if (IsNumeric(obj.Value))
                code = (int)(Convert.ToInt64(obj.Value) & int.MaxValue);
            else
                code = obj.Value.GetHashCode();
            return HashCode.Combine(obj.Key, code);
        }
    }
}
