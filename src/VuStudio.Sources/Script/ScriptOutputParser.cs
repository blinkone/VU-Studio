using System.Text.Json;
using System.Text.Json.Nodes;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Script;

/// <summary>How the output of a script is interpreted (the "parse" setting).</summary>
public enum ScriptParseMode
{
    /// <summary>Decide from the output: a single number, then JSON, then <c>key=value</c> lines, then plain text.</summary>
    Auto,

    /// <summary>A number: the whole output, or else the first number found in it.</summary>
    Number,

    /// <summary><c>key=value</c> or <c>key: value</c> lines, one sensor per key.</summary>
    KeyValue,

    /// <summary>A JSON document; its leaves become sensors.</summary>
    Json,
}

/// <summary>What the output of a script turned out to be.</summary>
public enum ScriptOutputFormat
{
    /// <summary>Nothing usable.</summary>
    None,

    /// <summary>A single number, published as <c>value</c>.</summary>
    Number,

    KeyValue,

    Json,

    /// <summary>Free text, published as the text sensor <c>value</c> (only <see cref="ScriptParseMode.Auto"/> falls back to this).</summary>
    Text,
}

/// <summary>The values found in a script's output.</summary>
public sealed class ScriptOutput
{
    internal ScriptOutput(ScriptOutputFormat format, IReadOnlyList<FlatValue> values, JsonNode? json)
    {
        Format = format;
        Values = values;
        Json = json;
    }

    public static ScriptOutput Empty { get; } = new(ScriptOutputFormat.None, [], null);

    /// <summary>What the output was recognised as.</summary>
    public ScriptOutputFormat Format { get; }

    /// <summary>
    /// The leaves, in output order. Keys of JSON documents are paths with <c>/</c> between levels
    /// (<c>main/temp</c>, <c>items/0/name</c>); a single number is <c>value</c>.
    /// </summary>
    public IReadOnlyList<FlatValue> Values { get; }

    /// <summary>
    /// A JSON view of the output for path lookups: the document itself, or for the other formats an object
    /// of the values by key.
    /// </summary>
    public JsonNode? Json { get; }

    public bool IsEmpty => Values.Count == 0;

    /// <summary>
    /// Finds a value by a JSON path (<c>$.main.temp</c>) or, failing that, by key ignoring case and punctuation
    /// (<c>CPU Temp</c> finds <c>cpu_temp</c>). Returns <c>null</c> when absent.
    /// </summary>
    public JsonNode? Select(string path)
    {
        if (JsonPathLite.TrySelect(Json, path, out var node))
        {
            return node;
        }

        var wanted = LocalIds.SlugPath(path, "");
        if (wanted.Length > 0)
        {
            foreach (var value in Values)
            {
                if (LocalIds.SlugPath(value.Key, "") == wanted)
                {
                    return value.Number is { } number ? JsonValue.Create(number) : JsonValue.Create(value.Text);
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Turns the text a script printed into sensor values. With <see cref="ScriptParseMode.Auto"/>:
/// a single number becomes the value <c>value</c>; a JSON object (or array) is flattened into its
/// numeric leaves, named <c>a/b</c>, and its strings, as text; <c>key=value</c> or <c>key: value</c> lines
/// give one value each. Anything else that is not empty becomes the text <c>value</c>.
/// </summary>
public static class ScriptOutputParser
{
    private const int MaxKeyLength = 64;

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Parses the value of the "parse" setting; unknown text means <see cref="ScriptParseMode.Auto"/>.</summary>
    public static ScriptParseMode ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "number" => ScriptParseMode.Number,
        "keyvalue" or "key-value" or "kv" => ScriptParseMode.KeyValue,
        "json" => ScriptParseMode.Json,
        _ => ScriptParseMode.Auto,
    };

    /// <summary>
    /// Parses <paramref name="output"/>. Empty output gives <see cref="ScriptOutput.Empty"/>; output that does not
    /// fit an explicitly requested mode throws, with a message fit to show the user.
    /// </summary>
    /// <exception cref="FormatException">The output is not in the requested format.</exception>
    public static ScriptOutput Parse(string? output, ScriptParseMode mode = ScriptParseMode.Auto)
    {
        var text = (output ?? "").Trim().TrimStart('﻿').Trim();
        if (text.Length == 0)
        {
            return ScriptOutput.Empty;
        }

        return mode switch
        {
            ScriptParseMode.Number => ParseFirstNumber(text) ?? throw new FormatException("No number found in the output."),
            ScriptParseMode.KeyValue => ParseKeyValue(text) ?? throw new FormatException("No key=value or key: value lines found in the output."),
            ScriptParseMode.Json => ParseJson(text),
            _ => ParseAuto(text),
        };
    }

    /// <summary>Like <see cref="Parse"/> but reports a mismatch as <c>false</c> and a message in <paramref name="error"/>.</summary>
    public static bool TryParse(string? output, ScriptParseMode mode, out ScriptOutput result, out string? error)
    {
        try
        {
            result = Parse(output, mode);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            result = ScriptOutput.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static ScriptOutput ParseAuto(string text)
    {
        if (NumberParser.TryParse(text, out var number))
        {
            return NumberOutput(number);
        }

        if (text[0] is '{' or '[')
        {
            try
            {
                return ParseJson(text);
            }
            catch (FormatException)
            {
                // "[INFO] starting up" is not JSON; carry on with the other formats.
            }
        }

        if (ParseKeyValue(text) is { } keyValue)
        {
            return keyValue;
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return TextOutput(firstLine.Length <= 1000 ? firstLine : firstLine[..1000]);
    }

    private static ScriptOutput? ParseFirstNumber(string text) =>
        NumberParser.TryFindFirst(text, out var number) ? NumberOutput(number) : null;

    private static ScriptOutput ParseJson(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, LenientJson);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The output is not valid JSON: " + ex.Message, ex);
        }

        var values = JsonFlattener.Flatten(root);
        return values.Count == 0
            ? throw new FormatException("The JSON output contains no numbers or texts.")
            : new ScriptOutput(ScriptOutputFormat.Json, values, root);
    }

    private static ScriptOutput? ParseKeyValue(string text)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, FlatValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.AsSpan().IndexOfAny('=', ':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var valueText = Unquote(line[(separator + 1)..].Trim());
            if (key.Length == 0 || key.Length > MaxKeyLength || valueText.Length == 0 || !IsPlausibleKey(key))
            {
                continue;
            }

            var value = NumberParser.TryParse(valueText, out var number)
                ? new FlatValue(key, number, null)
                : new FlatValue(key, null, valueText.Length <= 1000 ? valueText : valueText[..1000]);
            if (byKey.TryAdd(key, value))
            {
                order.Add(key);
            }
            else
            {
                byKey[key] = value; // a repeated key keeps its place but takes the latest value
            }
        }

        if (order.Count == 0)
        {
            return null;
        }

        var values = order.Select(k => byKey[k]).ToList();
        var json = new JsonObject();
        foreach (var value in values)
        {
            json[value.Key] = value.Number is { } number ? JsonValue.Create(number) : JsonValue.Create(value.Text);
        }

        return new ScriptOutput(ScriptOutputFormat.KeyValue, values, json);
    }

    private static ScriptOutput NumberOutput(double number) => new(
        ScriptOutputFormat.Number,
        [new FlatValue(JsonFlattener.RootKey, number, null)],
        new JsonObject { [JsonFlattener.RootKey] = number });

    private static ScriptOutput TextOutput(string text) => new(
        ScriptOutputFormat.Text,
        [new FlatValue(JsonFlattener.RootKey, null, text)],
        new JsonObject { [JsonFlattener.RootKey] = text });

    /// <summary>Keys are words: letters, digits, spaces and a few separators. This keeps prose and URLs out.</summary>
    private static bool IsPlausibleKey(string key)
    {
        foreach (var c in key)
        {
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '/' or '(' or ')' or '#' or '@'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1].Trim() : value;
}
