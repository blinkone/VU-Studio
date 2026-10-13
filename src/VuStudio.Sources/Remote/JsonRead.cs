using System.Text.Json.Nodes;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Remote;

/// <summary>Null-tolerant readers for loosely shaped JSON answers of remote devices.</summary>
internal static class JsonRead
{
    public static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>Walks a chain of object keys.</summary>
    public static JsonNode? Path(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            node = Get(node, key);
        }

        return node;
    }

    public static double? Num(JsonNode? node, string key) => JsonValues.TryGetNumber(Get(node, key), out var v) && double.IsFinite(v) ? v : null;

    public static double? Num(JsonNode? node) => JsonValues.TryGetNumber(node, out var v) && double.IsFinite(v) ? v : null;

    public static string? Str(JsonNode? node, string key) => JsonValues.ToText(Get(node, key));

    public static bool? Bool(JsonNode? node, string key) => Get(node, key) switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        { } other when JsonValues.TryGetNumber(other, out var n) => n != 0,
        _ => null,
    };

    /// <summary>The object elements of an array (or of an array found under <paramref name="key"/>), skipping anything else.</summary>
    public static IEnumerable<JsonObject> Items(JsonNode? node, string? key = null)
    {
        var array = (key is null ? node : Get(node, key)) as JsonArray;
        return array is null ? [] : array.OfType<JsonObject>();
    }
}
