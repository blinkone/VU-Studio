using System.Text.Json;
using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.HomeAssistant;
using VuStudio.Sources.Remote;

namespace VuStudio.Sources.Mqtt;

/// <summary>One subscription: a topic filter and how to turn its payloads into a sensor.</summary>
internal sealed record MqttRow(
    string Filter,
    string IdBase,
    string Name,
    string? Path,
    string Unit,
    SensorCategory Category,
    double? Min,
    double? Max,
    bool IsText,
    double Scale,
    double Offset,
    double StaleSeconds,
    string? Device = null,
    string? Kind = null,
    string? Icon = null,
    bool Auto = false)
{
    public bool HasWildcard => Filter.Contains('+') || Filter.Contains('#');
}

/// <summary>A value extracted from a message, ready to be published.</summary>
internal sealed record MqttUpdate(SensorInfo Info, double? Number, string? Text);

internal static class MqttTopics
{
    /// <summary>MQTT topic filter matching: <c>+</c> is one level, <c>#</c> the rest (also zero levels). Topics starting with <c>$</c> are not matched by wildcards at the first level.</summary>
    public static bool Matches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        if (t[0].StartsWith('$') && f[0] is "+" or "#")
        {
            return false;
        }

        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#")
            {
                return i == f.Length - 1;
            }

            if (i >= t.Length)
            {
                return false;
            }

            if (f[i] != "+" && f[i] != t[i])
            {
                return false;
            }
        }

        return f.Length == t.Length;
    }

    public static bool IsValidFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Length > 65535)
        {
            return false;
        }

        var levels = filter.Split('/');
        for (var i = 0; i < levels.Length; i++)
        {
            var l = levels[i];
            if (l.Contains('#') && (l != "#" || i != levels.Length - 1))
            {
                return false;
            }

            if (l.Contains('+') && l != "+")
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Payload extraction: raw number, on/off words, or a JSONPath-lite path into a JSON object.</summary>
internal static class MqttPayload
{
    public const int MaxBytes = 64 * 1024;

    public static FieldValue Extract(string? payload, string? path, bool asText)
    {
        if (payload is null)
        {
            return default;
        }

        var text = payload.Trim();
        if (text.Length == 0)
        {
            return default;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return default;
            }

            if (!JsonPathLite.TrySelect(root, path, out var node))
            {
                return default;
            }

            if (asText)
            {
                return new FieldValue(null, JsonValues.ToText(node));
            }

            return JsonValues.TryGetNumber(node, out var n) ? new FieldValue(n, null)
                : HaMapping.BoolWord(JsonValues.ToText(node)) is { } b ? new FieldValue(b, null)
                : default;
        }

        if (asText)
        {
            return new FieldValue(null, text);
        }

        if (NumberParser.TryParse(text, out var number))
        {
            return new FieldValue(number, null);
        }

        if (HaMapping.BoolWord(text) is { } word)
        {
            return new FieldValue(word, null);
        }

        // A bare JSON scalar such as "21.5" or true.
        try
        {
            var scalar = JsonNode.Parse(text);
            if (scalar is JsonValue && JsonValues.TryGetNumber(scalar, out var fromJson))
            {
                return new FieldValue(fromJson, null);
            }
        }
        catch (JsonException)
        {
        }

        return default;
    }
}

/// <summary>Maps incoming messages to sensor updates according to the configured and discovered subscriptions.</summary>
internal sealed class MqttSensorMap
{
    private readonly List<MqttRow> _rows = [];
    private readonly Dictionary<string, MqttRow> _discovered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _textDecided = new(StringComparer.Ordinal);

    public MqttSensorMap(IEnumerable<MqttRow> rows)
    {
        _rows.AddRange(rows);
    }

    public IEnumerable<MqttRow> Rows => _rows.Concat(_discovered.Values);

    /// <summary>The topic filters to subscribe to for the configured rows.</summary>
    public IEnumerable<string> Filters => _rows.Select(r => r.Filter).Distinct(StringComparer.Ordinal);

    public void SetDiscovered(string key, MqttRow? row)
    {
        if (row is null)
        {
            _discovered.Remove(key);
        }
        else
        {
            _discovered[key] = row;
        }
    }

    /// <summary>Local sensor ids (with the given prefix) that a discovered row owned, so they can be invalidated when it is withdrawn.</summary>
    public string? DiscoveredIdBase(string key) => _discovered.TryGetValue(key, out var row) ? row.IdBase : null;

    public IEnumerable<MqttUpdate> Process(string topic, string? payload)
    {
        foreach (var row in Rows)
        {
            if (!MqttTopics.Matches(row.Filter, topic))
            {
                continue;
            }

            var id = row.HasWildcard ? row.IdBase + "/" + LocalIds.SlugPath(topic) : row.IdBase;
            id = id.Length <= LocalIds.MaxLength ? id : id[..LocalIds.MaxLength];
            var name = row.HasWildcard ? $"{row.Name} ({topic})" : row.Name;

            var asText = row.IsText;
            var value = MqttPayload.Extract(payload, row.Path, asText);
            if (row.Auto && !_textDecided.ContainsKey(id))
            {
                // Discovered sensors without a unit: numeric when the first payload is a number, text otherwise.
                var numeric = MqttPayload.Extract(payload, row.Path, false);
                _textDecided[id] = !numeric.HasValue && value.Text is not null && string.IsNullOrEmpty(row.Unit) && row.Kind is null;
            }

            if (row.Auto && _textDecided.GetValueOrDefault(id))
            {
                asText = true;
                value = MqttPayload.Extract(payload, row.Path, true);
            }
            else if (row.Auto)
            {
                value = MqttPayload.Extract(payload, row.Path, false);
            }

            var kind = asText ? SensorKinds.Text : row.Kind ?? SensorHints.KindForUnit(row.Unit);
            var info = new SensorInfo
            {
                Id = id,
                Name = name,
                Device = row.Device,
                Category = row.Category,
                Kind = kind,
                Unit = asText ? Units.None : row.Unit,
                ValueKind = asText ? SensorValueKind.Text : SensorValueKind.Number,
                Min = asText ? null : row.Min,
                Max = asText ? null : row.Max,
                Icon = row.Icon ?? SensorHints.Icon(row.Category, kind),
                StaleAfterSeconds = row.StaleSeconds,
            };
            yield return new MqttUpdate(info, value.Number is { } n ? (n * row.Scale) + row.Offset : null, value.Text);
        }
    }
}

/// <summary>Home Assistant MQTT discovery: <c>&lt;prefix&gt;/&lt;component&gt;/[&lt;node&gt;/]&lt;object&gt;/config</c> messages become subscriptions.</summary>
internal static class MqttDiscovery
{
    public static readonly string[] Components = ["sensor", "binary_sensor"];

    public static IEnumerable<string> Filters(string prefix) =>
        Components.SelectMany(c => new[] { $"{prefix}/{c}/+/config", $"{prefix}/{c}/+/+/config" });

    public static bool IsConfigTopic(string prefix, string topic) =>
        topic.StartsWith(prefix + "/", StringComparison.Ordinal) && topic.EndsWith("/config", StringComparison.Ordinal) &&
        Components.Any(c => topic.StartsWith($"{prefix}/{c}/", StringComparison.Ordinal));

    /// <summary>
    /// Parses a discovery config. An empty payload (the device was removed) gives <c>null</c>; a config without a state topic or
    /// of an unsupported kind gives <c>null</c> too.
    /// </summary>
    public static MqttRow? Parse(string prefix, string topic, string? payload, double staleSeconds)
    {
        if (string.IsNullOrWhiteSpace(payload) || !IsConfigTopic(prefix, topic))
        {
            return null;
        }

        JsonNode? config;
        try
        {
            config = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        var component = topic[(prefix.Length + 1)..].Split('/')[0];
        var baseTopic = JsonRead.Str(config, "~");
        string? Expand(string? t) => t is null ? null : baseTopic is null ? t : t.StartsWith('~') ? baseTopic + t[1..] : t.EndsWith('~') ? t[..^1] + baseTopic : t;
        var stateTopic = Expand(HaMapping.Key(config, "state_topic", "stat_t"));
        if (stateTopic is null || !MqttTopics.IsValidFilter(stateTopic))
        {
            return null;
        }

        var objectId = topic[(prefix.Length + 1)..].Split('/') is { Length: >= 3 } parts ? string.Join('-', parts[1..^1]) : "sensor";
        var uniqueId = HaMapping.Key(config, "unique_id", "uniq_id") ?? objectId;
        var device = JsonRead.Str(JsonRead.Get(config, "device") ?? JsonRead.Get(config, "dev"), "name");
        var name = JsonRead.Str(config, "name") ?? uniqueId;
        var deviceClass = HaMapping.Key(config, "device_class", "dev_cla");
        var unit = HaMapping.Key(config, "unit_of_measurement", "unit_of_meas");
        var path = HaMapping.PathFromTemplate(HaMapping.Key(config, "value_template", "val_tpl"));

        if (component == "binary_sensor")
        {
            return new MqttRow(stateTopic, "ha/" + LocalIds.SlugPath(uniqueId), name, path, Units.None, SensorCategory.Home, 0, 1, false, 1, 0, staleSeconds, device, SensorKinds.State, null, true);
        }

        var map = HaMapping.Map(unit, deviceClass, uniqueId, name);
        return new MqttRow(stateTopic, "ha/" + LocalIds.SlugPath(uniqueId), name, path, map.Unit, map.Category, map.Min, map.Max, false, map.Scale, map.Offset, staleSeconds, device, map.Kind, map.Icon, unit is null && deviceClass is null);
    }
}
