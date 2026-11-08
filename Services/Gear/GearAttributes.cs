using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Defensive readers for <see cref="Models.Item.ExtraAttributes"/> values: depending on the path (Kafka json, MessagePack,
/// Cassandra) a value is a JToken, a boxed long/int/short/byte/double, a string, a Dictionary&lt;string, object&gt; or a
/// Dictionary&lt;object, object&gt;.
/// </summary>
public static class GearAttributes
{
    public static object? Get(IReadOnlyDictionary<string, object>? attributes, string key)
        => attributes != null && attributes.TryGetValue(key, out var value) ? Unwrap(value) : null;

    public static long? GetLong(IReadOnlyDictionary<string, object>? attributes, string key) => ToLong(Get(attributes, key));

    public static string? GetString(IReadOnlyDictionary<string, object>? attributes, string key)
        => AsString(Get(attributes, key));

    /// <summary>A primitive value as text, null for compounds and lists.</summary>
    public static string? AsString(object? value) => Unwrap(value) switch
    {
        string text => text,
        IConvertible primitive => Convert.ToString(primitive, CultureInfo.InvariantCulture),
        _ => null
    };

    public static long? ToLong(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            null => null,
            bool b => b ? 1 : 0,
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            sbyte sb => sb,
            uint ui => ui,
            ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
            double d => double.IsFinite(d) ? (long)Math.Floor(d) : null,
            float f => float.IsFinite(f) ? (long)Math.Floor(f) : null,
            decimal m => (long)Math.Floor(m),
            string text => ParseLong(text),
            IConvertible other => ConvertLong(other),
            _ => null
        };
    }

    /// <summary>ushort, char and every other integer MessagePack may hand back (it picks the smallest fitting type).</summary>
    private static long? ConvertLong(IConvertible value)
    {
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static long? ParseLong(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return (long)Math.Floor(d);
        return null;
    }

    /// <summary>A nested compound as a string keyed dictionary, null when the value is not a compound.</summary>
    public static Dictionary<string, object>? AsDictionary(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            Dictionary<string, object> dict => dict,
            IDictionary<string, object> dict => new Dictionary<string, object>(dict),
            Dictionary<object, object> objects => FromObjectKeys(objects),
            JObject obj => FromJObject(obj),
            string text when text.StartsWith('{') => TryParseJson(text),
            _ => null
        };
    }

    private static Dictionary<string, object> FromObjectKeys(Dictionary<object, object> objects)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in objects)
            if (key != null && value != null)
                result[key.ToString()!] = value;
        return result;
    }

    private static Dictionary<string, object> FromJObject(JObject obj)
    {
        var result = new Dictionary<string, object>();
        foreach (var property in obj.Properties())
            if (property.Value.Type != JTokenType.Null)
                result[property.Name] = property.Value;
        return result;
    }

    private static Dictionary<string, object>? TryParseJson(string text)
    {
        try
        {
            return FromJObject(JObject.Parse(text));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static object? Unwrap(object? value) => value is JValue jValue ? jValue.Value : value;
}
