using System.Text.Json;
using System.Text.Json.Nodes;

namespace VuStudio.Sources.Common;

/// <summary>Conversions from loosely typed <see cref="JsonNode"/> values to sensor payloads.</summary>
internal static class JsonValues
{
    /// <summary>
    /// Reads a number from a JSON number, a numeric string (<c>"23.5"</c>) or a boolean (true = 1).
    /// Objects, arrays, <c>null</c> and non-numeric strings yield <c>false</c>.
    /// </summary>
    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        switch (json.GetValueKind())
        {
            case JsonValueKind.Number:
                return TryGetRawNumber(json, out value) && double.IsFinite(value);
            case JsonValueKind.String:
                return json.TryGetValue<string>(out var s) && NumberParser.TryParse(s, out value);
            case JsonValueKind.True:
                value = 1;
                return true;
            case JsonValueKind.False:
                value = 0;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Text form of a value: strings as they are, numbers and booleans in JSON notation, objects and
    /// arrays as compact JSON. Returns <c>null</c> for JSON <c>null</c>. Long text is cut at <paramref name="maxLength"/>.
    /// </summary>
    public static string? ToText(JsonNode? node, int maxLength = 1000)
    {
        if (node is null)
        {
            return null;
        }

        var text = node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var s)
            ? s
            : node.ToJsonString();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static bool TryGetRawNumber(JsonValue json, out double value)
    {
        // JsonValues parsed from text are JsonElement-backed and convert to anything; values created in code
        // (JsonValue.Create(5)) only convert to their own CLR type, so try the common ones.
        if (json.TryGetValue(out value))
        {
            return true;
        }

        if (json.TryGetValue<long>(out var l))
        {
            value = l;
            return true;
        }

        if (json.TryGetValue<int>(out var i))
        {
            value = i;
            return true;
        }

        if (json.TryGetValue<decimal>(out var d))
        {
            value = (double)d;
            return true;
        }

        if (json.TryGetValue<float>(out var f))
        {
            value = f;
            return true;
        }

        value = 0;
        return false;
    }
}
