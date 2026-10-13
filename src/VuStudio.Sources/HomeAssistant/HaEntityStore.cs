using System.Text.Json.Nodes;
using VuStudio.Sources.Remote;

namespace VuStudio.Sources.HomeAssistant;

/// <summary>The state of one entity: the state string and its attributes.</summary>
internal sealed record HaState(string State, JsonObject Attributes)
{
    public string? Text(string attribute) => JsonRead.Str(Attributes, attribute);
}

/// <summary>An entity selector: <c>sensor.power</c> or, for an attribute, <c>climate.living#current_temperature</c>.</summary>
internal sealed record HaSelector(string EntityId, string? Attribute)
{
    public static HaSelector? Parse(string text)
    {
        var parts = text.Trim().Split('#', 2, StringSplitOptions.TrimEntries);
        var entity = parts[0].ToLowerInvariant();
        var dot = entity.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == entity.Length - 1 || entity.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.')))
        {
            return null;
        }

        return new HaSelector(entity, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }
}

/// <summary>
/// Keeps the entity states of a Home Assistant WebSocket session and applies the (undocumented) compressed
/// <c>subscribe_entities</c> events: <c>a</c> = added, <c>c</c> = changed (<c>+</c> new/changed, <c>-</c> removed
/// attributes) and <c>r</c> = removed. State keys: <c>s</c> state, <c>a</c> attributes.
/// Also understands the verbose <c>state_changed</c> events used as the fallback.
/// </summary>
internal sealed class HaEntityStore
{
    private readonly Dictionary<string, HaState> _states = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, HaState> States => _states;

    /// <summary>Applies a <c>subscribe_entities</c> event; returns the ids whose state or attributes changed and those removed.</summary>
    public (HashSet<string> Changed, HashSet<string> Removed) ApplyCompressed(JsonNode? ev)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);

        if (JsonRead.Get(ev, "a") is JsonObject added)
        {
            foreach (var (id, node) in added)
            {
                _states[id] = new HaState(JsonRead.Str(node, "s") ?? "", CloneObject(JsonRead.Get(node, "a")));
                changed.Add(id);
            }
        }

        if (JsonRead.Get(ev, "c") is JsonObject updates)
        {
            foreach (var (id, diff) in updates)
            {
                var current = _states.GetValueOrDefault(id) ?? new HaState("", []);
                var state = current.State;
                var attributes = current.Attributes;
                if (JsonRead.Get(diff, "+") is { } plus)
                {
                    if (JsonRead.Str(plus, "s") is { } s)
                    {
                        state = s;
                    }

                    if (JsonRead.Get(plus, "a") is JsonObject changedAttributes)
                    {
                        foreach (var (key, value) in changedAttributes)
                        {
                            attributes[key] = value?.DeepClone();
                        }
                    }
                }

                if (JsonRead.Get(JsonRead.Get(diff, "-"), "a") is JsonArray gone)
                {
                    foreach (var key in gone.Select(k => VuStudio.Sources.Common.JsonValues.ToText(k)).OfType<string>())
                    {
                        attributes.Remove(key);
                    }
                }

                _states[id] = new HaState(state, attributes);
                changed.Add(id);
            }
        }

        if (JsonRead.Get(ev, "r") is JsonArray removals)
        {
            foreach (var node in removals)
            {
                if (VuStudio.Sources.Common.JsonValues.ToText(node) is { } id && _states.Remove(id))
                {
                    removed.Add(id);
                    changed.Remove(id);
                }
            }
        }

        return (changed, removed);
    }

    /// <summary>Applies a verbose <c>state_changed</c> event (<c>data.entity_id</c>, <c>data.new_state</c>). Returns the entity id, or <c>null</c>.</summary>
    public string? ApplyStateChanged(JsonNode? ev)
    {
        var data = JsonRead.Get(ev, "data");
        var id = JsonRead.Str(data, "entity_id");
        if (id is null)
        {
            return null;
        }

        if (JsonRead.Get(data, "new_state") is JsonObject newState)
        {
            Set(id, newState);
        }
        else
        {
            _states.Remove(id);
        }

        return id;
    }

    /// <summary>Loads the answer of <c>get_states</c> or REST <c>/api/states</c>: an array of <c>{entity_id, state, attributes}</c>.</summary>
    public List<string> LoadAll(JsonNode? array)
    {
        var ids = new List<string>();
        foreach (var item in JsonRead.Items(array))
        {
            if (JsonRead.Str(item, "entity_id") is { } id)
            {
                Set(id, item);
                ids.Add(id);
            }
        }

        return ids;
    }

    public void Set(string id, JsonNode state) =>
        _states[id] = new HaState(JsonRead.Str(state, "state") ?? "", CloneObject(JsonRead.Get(state, "attributes")));

    private static JsonObject CloneObject(JsonNode? node) => node is JsonObject o ? (JsonObject)o.DeepClone() : [];
}
