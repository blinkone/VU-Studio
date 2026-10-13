using System.Collections.Concurrent;

namespace VuStudio.Core.Sensors;

/// <summary>
/// Thread-safe store of every known sensor and its latest value. Consumers poll it (engine tick,
/// WebSocket batches) instead of subscribing to events, so bursts of updates never fan out.
/// </summary>
public sealed class SensorHub
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _version;
    private long _catalogVersion;

    public SensorHub(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Default staleness threshold for sensors that do not specify one.</summary>
    public TimeSpan DefaultStaleAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Incremented on every value update.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Incremented whenever sensors are added, changed or removed.</summary>
    public long CatalogVersion => Interlocked.Read(ref _catalogVersion);

    public int Count => _entries.Count;

    public ISensorSink CreateSink(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return new Sink(this, providerId);
    }

    public bool TryGet(string sensorId, out SensorSnapshot snapshot)
    {
        if (_entries.TryGetValue(sensorId, out var entry))
        {
            snapshot = ToSnapshot(entry, _time.GetUtcNow());
            return true;
        }

        snapshot = null!;
        return false;
    }

    public SensorSnapshot? Get(string sensorId) => TryGet(sensorId, out var s) ? s : null;

    /// <summary>Latest fresh numeric value, or <c>null</c> when unknown or stale.</summary>
    public double? GetNumber(string sensorId)
    {
        if (!_entries.TryGetValue(sensorId, out var entry))
        {
            return null;
        }

        var state = entry.State;
        return state.HasNumber && !IsStale(entry, state, _time.GetUtcNow()) ? state.Number : null;
    }

    /// <summary>Latest fresh text value (for Text and Color sensors), or <c>null</c>.</summary>
    public string? GetText(string sensorId)
    {
        if (!_entries.TryGetValue(sensorId, out var entry))
        {
            return null;
        }

        var state = entry.State;
        return !IsStale(entry, state, _time.GetUtcNow()) ? state.Text : null;
    }

    public IReadOnlyList<SensorSnapshot> GetAll()
    {
        var now = _time.GetUtcNow();
        return _entries.Values.Select(e => ToSnapshot(e, now)).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<SensorInfo> GetCatalog() =>
        _entries.Values.Select(e => e.Info).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();

    /// <summary>Snapshots of sensors updated after <paramref name="sinceVersion"/>.</summary>
    public IReadOnlyList<SensorSnapshot> GetChangedSince(long sinceVersion, out long currentVersion)
    {
        currentVersion = Version;
        var now = _time.GetUtcNow();
        var list = new List<SensorSnapshot>();
        foreach (var entry in _entries.Values)
        {
            if (entry.State.Version > sinceVersion)
            {
                list.Add(ToSnapshot(entry, now));
            }
        }

        return list;
    }

    /// <summary>Removes every sensor whose id starts with <c>{providerId}/</c>.</summary>
    public void RemoveProvider(string providerId)
    {
        var prefix = providerId + "/";
        var removed = false;
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && _entries.TryRemove(key, out _))
            {
                removed = true;
            }
        }

        if (removed)
        {
            Interlocked.Increment(ref _catalogVersion);
        }
    }

    private void Define(string providerId, SensorInfo info)
    {
        var id = FullId(providerId, info.Id);
        var normalized = info with { Id = id, ProviderId = providerId };
        _entries.AddOrUpdate(
            id,
            _ => new Entry(normalized),
            (_, existing) =>
            {
                existing.Info = normalized;
                return existing;
            });
        Interlocked.Increment(ref _catalogVersion);
    }

    private void Remove(string providerId, string localId)
    {
        if (_entries.TryRemove(FullId(providerId, localId), out _))
        {
            Interlocked.Increment(ref _catalogVersion);
        }
    }

    private void Update(string providerId, string localId, double? number, string? text)
    {
        var id = FullId(providerId, localId);
        if (!_entries.TryGetValue(id, out var entry))
        {
            // Values for undefined sensors are accepted with minimal metadata so that quick
            // integrations (push API, scripts) work without an explicit Define call.
            Define(providerId, new SensorInfo
            {
                Id = localId,
                Name = localId,
                ValueKind = number.HasValue || text is null ? SensorValueKind.Number : SensorValueKind.Text,
            });
            entry = _entries[id];
        }

        var version = Interlocked.Increment(ref _version);
        entry.State = new State(number ?? double.NaN, text, _time.GetUtcNow().UtcTicks, version);
    }

    private static string FullId(string providerId, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        var prefix = providerId + "/";
        return localId.StartsWith(prefix, StringComparison.Ordinal) ? localId : prefix + localId;
    }

    private bool IsStale(Entry entry, State state, DateTimeOffset now)
    {
        if (state.Ticks == 0)
        {
            return true;
        }

        var staleAfter = entry.Info.StaleAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : DefaultStaleAfter;
        return now.UtcTicks - state.Ticks > staleAfter.Ticks;
    }

    private SensorSnapshot ToSnapshot(Entry entry, DateTimeOffset now)
    {
        var state = entry.State;
        return new SensorSnapshot(
            entry.Info,
            state.HasNumber ? state.Number : null,
            state.Text,
            state.Ticks == 0 ? null : new DateTimeOffset(state.Ticks, TimeSpan.Zero),
            state.Version,
            IsStale(entry, state, now));
    }

    private sealed class Entry(SensorInfo info)
    {
        private volatile State _state = State.Empty;

        public SensorInfo Info { get; set; } = info;

        public State State
        {
            get => _state;
            set => _state = value;
        }
    }

    private sealed record State(double Number, string? Text, long Ticks, long Version)
    {
        public static readonly State Empty = new(double.NaN, null, 0, 0);

        public bool HasNumber => !double.IsNaN(Number);
    }

    private sealed class Sink(SensorHub hub, string providerId) : ISensorSink
    {
        public string ProviderId { get; } = providerId;

        public void Define(SensorInfo info) => hub.Define(ProviderId, info);

        public void Remove(string localId) => hub.Remove(ProviderId, localId);

        public void Clear() => hub.RemoveProvider(ProviderId);

        public void Publish(string localId, double value)
        {
            if (double.IsFinite(value))
            {
                hub.Update(ProviderId, localId, value, null);
            }
            else
            {
                Invalidate(localId);
            }
        }

        public void PublishText(string localId, string? text) => hub.Update(ProviderId, localId, null, text);

        public void Invalidate(string localId)
        {
            var id = FullId(ProviderId, localId);
            if (hub._entries.TryGetValue(id, out var entry))
            {
                var version = Interlocked.Increment(ref hub._version);
                entry.State = State.Empty with { Version = version };
            }
        }
    }
}
