using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VuStudio.Core.Providers;

/// <summary>
/// Typed, forgiving read access to a provider's JSON settings, falling back to the defaults
/// declared in its <see cref="ProviderDescriptor"/>.
/// </summary>
public sealed class ProviderSettings
{
    private readonly JsonObject _values;
    private readonly IReadOnlyDictionary<string, SettingField> _fields;

    public ProviderSettings(JsonObject? values, ProviderDescriptor? descriptor = null)
        : this(values, descriptor?.Settings ?? [])
    {
    }

    public ProviderSettings(JsonObject? values, IReadOnlyList<SettingField> fields)
    {
        _values = values ?? new JsonObject();
        _fields = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
    }

    public JsonObject Raw => _values;

    public JsonNode? Get(string key) => _values.TryGetPropertyValue(key, out var node) && node is not null
        ? node
        : _fields.TryGetValue(key, out var field) ? field.Default : null;

    public string? GetString(string key, string? fallback = null)
    {
        var node = Get(key);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
            {
                return string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
            }

            return value.ToJsonString().Trim('"');
        }

        return fallback;
    }

    public string RequireString(string key) =>
        GetString(key) ?? throw new InvalidOperationException($"Setting '{key}' is required.");

    public double GetDouble(string key, double fallback = 0)
    {
        var node = Get(key);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
        }

        return fallback;
    }

    public int GetInt(string key, int fallback = 0) => (int)Math.Round(GetDouble(key, fallback));

    public bool GetBool(string key, bool fallback = false)
    {
        var node = Get(key);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out b))
            {
                return b;
            }
        }

        return fallback;
    }

    public TimeSpan GetSeconds(string key, double fallbackSeconds) =>
        TimeSpan.FromSeconds(Math.Max(0.05, GetDouble(key, fallbackSeconds)));

    public IReadOnlyList<string> GetStringList(string key)
    {
        return Get(key) switch
        {
            JsonArray array => array
                .Select(n => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n?.ToJsonString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList(),
            JsonValue value when value.TryGetValue<string>(out var s) => s
                .Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            _ => [],
        };
    }

    /// <summary>Rows of a <see cref="SettingKind.Table"/> setting, each wrapped for typed access.</summary>
    public IReadOnlyList<ProviderSettings> GetTable(string key)
    {
        var columns = _fields.TryGetValue(key, out var field) ? field.Columns ?? [] : [];
        return Get(key) is JsonArray array
            ? array.OfType<JsonObject>().Select(row => new ProviderSettings(row, columns)).ToList()
            : [];
    }
}
