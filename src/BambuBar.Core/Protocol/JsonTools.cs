using System.Globalization;
using System.Text.Json;

namespace BambuBar.Core.Protocol;

/// <summary>
/// The printer's JSON mixes numbers and numeric strings depending on firmware.
/// These helpers read either, like the Swift original's <c>int</c>/<c>double</c>/<c>string</c> helpers.
/// Reports omit optional fields (an empty external spool has no <c>state</c>), so read through
/// <see cref="Get"/> rather than indexing directly.
/// </summary>
public static class JsonTools
{
    /// <summary>Converts a <see cref="JsonElement"/> into plain CLR objects (dicts, lists, strings, numbers, bools).</summary>
    public static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => ToList(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            dict[property.Name] = ToObject(property.Value);
        return dict;
    }

    public static List<object?> ToList(JsonElement element)
    {
        var list = new List<object?>();
        foreach (var item in element.EnumerateArray())
            list.Add(ToObject(item));
        return list;
    }

    public static string? GetString(object? value)
    {
        switch (value)
        {
            case string s: return s;
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case double d: return d.ToString(CultureInfo.InvariantCulture);
            case bool b: return b ? "true" : "false";
            case JsonElement el:
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            default: return null;
        }
    }

    public static int? GetInt(object? value)
    {
        switch (value)
        {
            case int i: return i;
            case long l: return (int)l;
            case double d: return (int)d;
            case string s:
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i2)
                    ? i2
                    : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? (int)d2 : null;
            case JsonElement el:
                return el.ValueKind == JsonValueKind.Number ? GetInt(ToObject(el)) : GetInt(GetString(el));
            default: return null;
        }
    }

    public static double? GetDouble(object? value)
    {
        switch (value)
        {
            case double d: return d;
            case int i: return i;
            case long l: return (double)l;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null;
            case JsonElement el:
                return el.ValueKind == JsonValueKind.Number ? GetDouble(ToObject(el)) : GetDouble(GetString(el));
            default: return null;
        }
    }

    public static bool GetBool(object? value) => value switch
    {
        bool b => b,
        JsonElement el => el.ValueKind == JsonValueKind.True,
        _ => false,
    };

    public static Dictionary<string, object?>? GetDictionary(object? value) => value switch
    {
        Dictionary<string, object?> d => d,
        JsonElement el when el.ValueKind == JsonValueKind.Object => ToDictionary(el),
        _ => null,
    };

    public static List<object?>? GetList(object? value) => value switch
    {
        List<object?> l => l,
        JsonElement el when el.ValueKind == JsonValueKind.Array => ToList(el),
        _ => null,
    };

    /// <summary>Reads a key that firmware may omit; <c>null</c> instead of <see cref="KeyNotFoundException"/>.</summary>
    public static object? Get(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict is not null && dict.TryGetValue(key, out var value) ? value : null;
}
