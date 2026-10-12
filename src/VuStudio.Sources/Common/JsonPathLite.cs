using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace VuStudio.Sources.Common;

/// <summary>
/// A deliberately small JSONPath subset for picking one value out of a JSON document.
/// <list type="bullet">
/// <item><description>Optional root marker: <c>$.a.b</c> and <c>a.b</c> are the same.</description></item>
/// <item><description>Properties: <c>a.b</c>, or quoted for odd names: <c>$['key with space']</c>, <c>$["a.b"]</c>
/// (a backslash escapes the next character).</description></item>
/// <item><description>Array elements: <c>a[0]</c>, <c>a[-1]</c> (from the end). Dot form works too: <c>a.0.b</c>.</description></item>
/// <item><description>Chains of the above: <c>$.items[0].tags[-1]</c>, <c>$[2]</c>, <c>[0].name</c>.</description></item>
/// </list>
/// Wildcards, recursive descent (<c>..</c>), slices, unions and filters are not supported and are
/// rejected with a <see cref="FormatException"/> rather than silently ignored.
/// </summary>
public sealed class JsonPathLite
{
    private readonly Step[] _steps;

    private JsonPathLite(string expression, Step[] steps)
    {
        Expression = expression;
        _steps = steps;
    }

    /// <summary>The original path text.</summary>
    public string Expression { get; }

    /// <summary>Parses a path. Throws <see cref="FormatException"/> for malformed or unsupported syntax.</summary>
    public static JsonPathLite Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new JsonPathLite(path, new Scanner(path).ReadSteps());
    }

    /// <summary>Parses a path without throwing.</summary>
    public static bool TryParse(string? path, out JsonPathLite? parsed)
    {
        parsed = null;
        if (path is null)
        {
            return false;
        }

        try
        {
            parsed = Parse(path);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates <paramref name="path"/> against <paramref name="root"/>. Returns <c>null</c> when nothing
    /// is found or the value is JSON <c>null</c>. Throws <see cref="FormatException"/> for an invalid path.
    /// </summary>
    public static JsonNode? Select(JsonNode? root, string path) => Parse(path).Evaluate(root);

    /// <summary>Like <see cref="Select"/> but reports an invalid path or a missing value as <c>false</c>.</summary>
    public static bool TrySelect(JsonNode? root, string? path, out JsonNode? node)
    {
        node = null;
        if (!TryParse(path, out var parsed))
        {
            return false;
        }

        node = parsed!.Evaluate(root);
        return node is not null;
    }

    /// <summary>Walks the path from <paramref name="root"/>; <c>null</c> when any step has no match.</summary>
    public JsonNode? Evaluate(JsonNode? root)
    {
        var current = root;
        foreach (var step in _steps)
        {
            if (current is null)
            {
                return null;
            }

            current = step switch
            {
                PropertyStep property => current switch
                {
                    JsonObject obj => obj.TryGetPropertyValue(property.Name, out var value) ? value : null,
                    JsonArray array when property.ArrayIndex is { } i => ElementAt(array, i),
                    _ => null,
                },
                IndexStep index => current switch
                {
                    JsonArray array => ElementAt(array, index.Value),
                    JsonObject obj => obj.TryGetPropertyValue(index.Value.ToString(CultureInfo.InvariantCulture), out var value) ? value : null,
                    _ => null,
                },
                _ => null,
            };
        }

        return current;
    }

    public override string ToString() => Expression;

    private static JsonNode? ElementAt(JsonArray array, int index)
    {
        if (index < 0)
        {
            index += array.Count;
        }

        return index >= 0 && index < array.Count ? array[index] : null;
    }

    private abstract record Step;

    /// <summary>A property lookup. <see cref="ArrayIndex"/> is set when the dot-form name is an integer, so <c>a.0</c> also indexes arrays.</summary>
    private sealed record PropertyStep(string Name, int? ArrayIndex) : Step;

    private sealed record IndexStep(int Value) : Step;

    private sealed class Scanner(string text)
    {
        private int _pos;

        public Step[] ReadSteps()
        {
            var steps = new List<Step>();
            SkipWhitespace();
            if (_pos < text.Length && text[_pos] == '$')
            {
                // '$' is the root marker only when nothing but a path separator follows; "$ref" is a property name.
                var next = _pos + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next >= text.Length || text[next] is '.' or '[')
                {
                    _pos++;
                }
            }

            var first = true;
            while (true)
            {
                SkipWhitespace();
                if (_pos >= text.Length)
                {
                    break;
                }

                var c = text[_pos];
                if (c == '.')
                {
                    _pos++;
                    if (_pos < text.Length && text[_pos] == '.')
                    {
                        throw Unsupported("recursive descent '..'");
                    }

                    if (_pos < text.Length && text[_pos] == '[')
                    {
                        continue;
                    }

                    steps.Add(ReadName());
                }
                else if (c == '[')
                {
                    steps.Add(ReadBracket());
                }
                else if (first)
                {
                    steps.Add(ReadName());
                }
                else
                {
                    throw new FormatException($"Unexpected '{c}' at position {_pos} in path '{text}'.");
                }

                first = false;
            }

            return steps.ToArray();
        }

        private PropertyStep ReadName()
        {
            var start = _pos;
            while (_pos < text.Length && text[_pos] is not ('.' or '['))
            {
                _pos++;
            }

            var name = text[start.._pos].Trim();
            if (name.Length == 0)
            {
                throw new FormatException($"Empty property name at position {start} in path '{text}'.");
            }

            if (name.Contains('*'))
            {
                throw Unsupported("wildcards");
            }

            if (name.Contains(']'))
            {
                throw new FormatException($"Unexpected ']' in path '{text}'.");
            }

            int? arrayIndex = IsInteger(name) && int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
                ? i
                : null;
            return new PropertyStep(name, arrayIndex);
        }

        private Step ReadBracket()
        {
            var open = _pos;
            _pos++; // '['
            SkipWhitespace();
            if (_pos >= text.Length)
            {
                throw new FormatException($"Missing ']' in path '{text}'.");
            }

            Step step;
            if (text[_pos] is '\'' or '"')
            {
                step = new PropertyStep(ReadQuoted(), null);
                SkipWhitespace();
                if (_pos >= text.Length || text[_pos] != ']')
                {
                    throw new FormatException($"Missing ']' after quoted name in path '{text}'.");
                }

                _pos++;
                return step;
            }

            var close = text.IndexOf(']', _pos);
            if (close < 0)
            {
                throw new FormatException($"Missing ']' for '[' at position {open} in path '{text}'.");
            }

            var inner = text[_pos..close].Trim();
            _pos = close + 1;
            if (inner.Length == 0)
            {
                throw new FormatException($"Empty brackets in path '{text}'.");
            }

            if (inner == "*")
            {
                throw Unsupported("wildcards");
            }

            if (inner[0] == '?')
            {
                throw Unsupported("filters");
            }

            if (inner.Contains(':'))
            {
                throw Unsupported("slices");
            }

            if (inner.Contains(','))
            {
                throw Unsupported("unions");
            }

            if (!IsInteger(inner))
            {
                throw new FormatException($"'[{inner}]' in path '{text}' is not an index; quote property names: ['{inner}'].");
            }

            if (!int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            {
                throw new FormatException($"Index '{inner}' in path '{text}' is out of range.");
            }

            return new IndexStep(index);
        }

        private string ReadQuoted()
        {
            var quote = text[_pos++];
            var builder = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '\\')
                {
                    if (_pos >= text.Length)
                    {
                        break;
                    }

                    builder.Append(text[_pos++]);
                }
                else if (c == quote)
                {
                    return builder.ToString();
                }
                else
                {
                    builder.Append(c);
                }
            }

            throw new FormatException($"Unterminated quoted name in path '{text}'.");
        }

        private void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private static bool IsInteger(string s)
        {
            var start = s.Length > 0 && s[0] is '-' or '+' ? 1 : 0;
            if (start >= s.Length)
            {
                return false;
            }

            for (var i = start; i < s.Length; i++)
            {
                if (s[i] is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private FormatException Unsupported(string what) =>
            new($"JSONPath {what} are not supported (path '{text}').");
    }
}
