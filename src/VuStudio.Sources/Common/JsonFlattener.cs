using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VuStudio.Sources.Common;

/// <summary>One leaf of a JSON document: a number (booleans count as 1/0) or a text.</summary>
/// <param name="Key">Path of the leaf with <c>/</c> between levels, e.g. <c>main/temp</c> or <c>items/0/name</c>.</param>
public readonly record struct FlatValue(string Key, double? Number, string? Text);

/// <summary>
/// Flattens a JSON document into its leaves: objects and arrays are walked (array elements by index),
/// numbers and booleans become numbers, strings become texts, nulls are skipped.
/// </summary>
public static class JsonFlattener
{
    /// <summary>Key given to a document that is a single value rather than an object or array.</summary>
    public const string RootKey = "value";

    public const int DefaultMaxValues = 256;
    public const int DefaultMaxDepth = 8;

    /// <summary>
    /// Returns the leaves of <paramref name="root"/> in document order. At most <paramref name="maxValues"/>
    /// are returned and nesting deeper than <paramref name="maxDepth"/> is ignored, so a huge response cannot
    /// flood the sensor catalog.
    /// </summary>
    public static IReadOnlyList<FlatValue> Flatten(JsonNode? root, int maxValues = DefaultMaxValues, int maxDepth = DefaultMaxDepth)
    {
        var result = new List<FlatValue>();
        if (root is JsonValue)
        {
            Add(result, RootKey, root, maxValues);
        }
        else
        {
            Walk(result, "", root, 0, maxValues, maxDepth);
        }

        return result;
    }

    private static void Walk(List<FlatValue> result, string prefix, JsonNode? node, int depth, int maxValues, int maxDepth)
    {
        if (result.Count >= maxValues || depth > maxDepth)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    Walk(result, Join(prefix, name), child, depth + 1, maxValues, maxDepth);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(result, Join(prefix, i.ToString(CultureInfo.InvariantCulture)), array[i], depth + 1, maxValues, maxDepth);
                }

                break;
            case JsonValue:
                Add(result, prefix, node, maxValues);
                break;
        }
    }

    private static void Add(List<FlatValue> result, string key, JsonNode? node, int maxValues)
    {
        if (result.Count >= maxValues || node is not JsonValue value)
        {
            return;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                if (JsonValues.TryGetNumber(value, out var number))
                {
                    result.Add(new FlatValue(key, number, null));
                }

                break;
            case JsonValueKind.String:
                if (JsonValues.ToText(value) is { } text)
                {
                    result.Add(new FlatValue(key, null, text));
                }

                break;
        }
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "/" + name;
}
