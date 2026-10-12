using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Common;

/// <summary>
/// A thin layer over <see cref="ISensorSink"/> for providers whose sensor set is discovered while running
/// (network interfaces, hwmon chips, JSON keys…). It remembers what was defined so that sensors are
/// defined once instead of on every poll, and it applies a default staleness window to every sensor
/// that does not specify one. Not thread-safe: use it from one provider task.
/// </summary>
internal sealed class SensorRegistry
{
    private readonly ISensorSink _sink;
    private readonly double? _defaultStaleAfterSeconds;
    private readonly Dictionary<string, SensorInfo> _defined = new(StringComparer.Ordinal);

    public SensorRegistry(ISensorSink sink, double? defaultStaleAfterSeconds = null)
    {
        _sink = sink;
        _defaultStaleAfterSeconds = defaultStaleAfterSeconds;
    }

    public int Count => _defined.Count;

    public IReadOnlyCollection<string> Ids => _defined.Keys;

    public bool IsDefined(string localId) => _defined.ContainsKey(localId);

    /// <summary>Defines the sensor, or updates its metadata when it changed. Identical re-definitions are ignored.</summary>
    public void Define(SensorInfo info)
    {
        if (info.StaleAfterSeconds is null && _defaultStaleAfterSeconds is { } stale)
        {
            info = info with { StaleAfterSeconds = stale };
        }

        if (_defined.TryGetValue(info.Id, out var existing) && existing == info)
        {
            return;
        }

        _defined[info.Id] = info;
        _sink.Define(info);
    }

    public void Publish(string localId, double value) => _sink.Publish(localId, value);

    public void PublishText(string localId, string? text) => _sink.PublishText(localId, text);

    public void Invalidate(string localId) => _sink.Invalidate(localId);

    public void Remove(string localId)
    {
        if (_defined.Remove(localId))
        {
            _sink.Remove(localId);
        }
    }

    /// <summary>Removes every sensor whose local id starts with <paramref name="prefix"/>; returns how many.</summary>
    public int RemovePrefix(string prefix)
    {
        var doomed = _defined.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var id in doomed)
        {
            Remove(id);
        }

        return doomed.Count;
    }

    public void Clear()
    {
        _defined.Clear();
        _sink.Clear();
    }
}
