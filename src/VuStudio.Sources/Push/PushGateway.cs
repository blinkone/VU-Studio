using System.Text.RegularExpressions;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Push;

/// <summary>
/// The entry point for external programs (REST API, Stream Deck, scripts) that push values into VU Studio.
/// Register it as a singleton in the host; the <c>push</c> provider attaches its sensor sink to it while it
/// runs. Thread-safe.
/// </summary>
/// <remarks>
/// The gateway remembers every pushed sensor, so values pushed while the provider is stopped or restarting
/// are not lost and sensors reappear when the provider comes back. It limits how many sensors can be
/// created, because a misbehaving client must not be able to flood the catalog.
/// </remarks>
public sealed partial class PushGateway
{
    /// <summary>Longest accepted text value.</summary>
    public const int MaxTextLength = 1000;

    private const int MaxNameLength = 100;
    private const int MaxUnitLength = 16;
    private const int MaxIconLength = 64;
    private const int MaxKindLength = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private ISensorSink? _sink;
    private double _defaultStaleAfterSeconds = 120;
    private int _maxSensors = 500;

    /// <summary>Creates a gateway. DI picks <paramref name="time"/> up when a <see cref="TimeProvider"/> is registered.</summary>
    public PushGateway(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The gateway used when the host did not register one.</summary>
    public static PushGateway Shared { get; } = new();

    /// <summary>True while a running <c>push</c> provider is attached, i.e. pushed values reach the hub.</summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _sink is not null;
            }
        }
    }

    /// <summary>Number of sensors currently known to the gateway.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Local ids of the known sensors, sorted.</summary>
    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return _entries.Keys.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>
    /// Sets the value of a sensor, creating it on first use. A number takes precedence over a text; a text
    /// of the form <c>#RRGGBB</c> makes a color sensor.
    /// </summary>
    /// <param name="localId">Sensor id, matching <c>[a-z0-9][a-z0-9/_.-]*</c>. It becomes <c>push/{localId}</c>.</param>
    /// <param name="value">A finite number, or <c>null</c> to push <paramref name="text"/>.</param>
    /// <param name="text">Text value, used when <paramref name="value"/> is <c>null</c>.</param>
    /// <param name="meta">Optional metadata; see <see cref="PushSensorMeta"/>.</param>
    /// <exception cref="ArgumentException">The id is malformed, there is neither value nor text, or a value is out of range.</exception>
    /// <exception cref="InvalidOperationException">The sensor limit was reached.</exception>
    public void Push(string localId, double? value, string? text = null, PushSensorMeta? meta = null)
    {
        if (value is null && text is null)
        {
            throw new ArgumentException("A value or a text is required.", nameof(value));
        }

        if (value is { } number && !double.IsFinite(number))
        {
            throw new ArgumentException("The value must be a finite number.", nameof(value));
        }

        if (text is { Length: > MaxTextLength })
        {
            throw new ArgumentException($"The text is too long (limit {MaxTextLength} characters).", nameof(text));
        }

        meta = Sanitize(meta);
        var kind = value.HasValue
            ? SensorValueKind.Number
            : ColorPattern().IsMatch(text!) ? SensorValueKind.Color : SensorValueKind.Text;
        var stored = kind == SensorValueKind.Color ? text!.ToUpperInvariant() : text;

        lock (_gate)
        {
            var id = NormalizeId(localId);
            if (!_entries.TryGetValue(id, out var entry))
            {
                if (_entries.Count >= _maxSensors)
                {
                    throw new InvalidOperationException($"Too many pushed sensors (limit {_maxSensors}); remove some first.");
                }

                entry = new Entry();
                _entries[id] = entry;
            }

            entry.Meta = Merge(entry.Meta, meta);
            var info = ToInfo(id, entry.Meta, kind);
            var changed = entry.Info != info;
            entry.Info = info;
            entry.Number = value;
            entry.Text = value.HasValue ? null : stored;
            entry.PushedAt = _time.GetUtcNow();

            if (_sink is { } sink)
            {
                if (changed)
                {
                    sink.Define(info);
                }

                Publish(sink, id, entry);
            }
        }
    }

    /// <summary>Forgets a sensor and removes it from the hub. Returns false when it did not exist.</summary>
    /// <exception cref="ArgumentException">The id is malformed.</exception>
    public bool Remove(string localId)
    {
        lock (_gate)
        {
            var id = NormalizeId(localId);
            if (!_entries.Remove(id))
            {
                return false;
            }

            _sink?.Remove(id);
            return true;
        }
    }

    /// <summary>Connects the running provider. Known sensors are defined again and recent values replayed.</summary>
    internal void Attach(ISensorSink sink, double defaultStaleAfterSeconds, int maxSensors)
    {
        lock (_gate)
        {
            _sink = sink;
            _defaultStaleAfterSeconds = defaultStaleAfterSeconds;
            _maxSensors = maxSensors;
            var now = _time.GetUtcNow();
            foreach (var (id, entry) in _entries)
            {
                entry.Info = ToInfo(id, entry.Meta, entry.Info.ValueKind);
                sink.Define(entry.Info);
                var stale = entry.Info.StaleAfterSeconds ?? defaultStaleAfterSeconds;
                if ((now - entry.PushedAt).TotalSeconds <= stale)
                {
                    Publish(sink, id, entry);
                }
            }
        }
    }

    /// <summary>Disconnects <paramref name="sink"/> if it is the attached one; the pushed state is kept.</summary>
    internal void Detach(ISensorSink sink)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sink = null;
            }
        }
    }

    private static void Publish(ISensorSink sink, string id, Entry entry)
    {
        if (entry.Number is { } number)
        {
            sink.Publish(id, number);
        }
        else
        {
            sink.PublishText(id, entry.Text);
        }
    }

    private string NormalizeId(string? localId)
    {
        var id = localId ?? "";

        // The hub prefixes the provider id itself and does not double it, so "push/x" and "x" are the same sensor.
        var prefix = (_sink?.ProviderId ?? "push") + "/";
        if (id.StartsWith(prefix, StringComparison.Ordinal))
        {
            id = id[prefix.Length..];
        }

        if (!LocalIds.IsValid(id))
        {
            throw new ArgumentException(
                $"Invalid sensor id '{localId}': use lowercase letters, digits, '/', '_', '.' and '-' " +
                $"(pattern [a-z0-9][a-z0-9/_.-]*), starting with a letter or digit, at most {LocalIds.MaxLength} characters.",
                nameof(localId));
        }

        return id;
    }

    private static PushSensorMeta? Sanitize(PushSensorMeta? meta)
    {
        if (meta is null)
        {
            return null;
        }

        if (meta.Category is { } category && !Enum.IsDefined(category))
        {
            throw new ArgumentException($"Unknown category '{(int)category}'.", nameof(meta));
        }

        return meta with
        {
            Name = Clean(meta.Name, MaxNameLength),
            Unit = Clean(meta.Unit, MaxUnitLength),
            Icon = Clean(meta.Icon, MaxIconLength),
            Device = Clean(meta.Device, MaxNameLength),
            Kind = Clean(meta.Kind, MaxKindLength),
            Min = Finite(meta.Min),
            Max = Finite(meta.Max),
            StaleAfterSeconds = meta.StaleAfterSeconds is { } s && double.IsFinite(s) && s > 0 ? s : null,
        };

        static string? Clean(string? text, int max)
        {
            var trimmed = text?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
        }

        static double? Finite(double? number) => number is { } n && double.IsFinite(n) ? n : null;
    }

    /// <summary>Members set in <paramref name="update"/> replace those in <paramref name="current"/>.</summary>
    private static PushSensorMeta Merge(PushSensorMeta? current, PushSensorMeta? update)
    {
        if (current is null)
        {
            return update ?? new PushSensorMeta();
        }

        if (update is null)
        {
            return current;
        }

        return new PushSensorMeta(
            update.Name ?? current.Name,
            update.Unit ?? current.Unit,
            update.Category ?? current.Category,
            update.Min ?? current.Min,
            update.Max ?? current.Max,
            update.Icon ?? current.Icon)
        {
            Device = update.Device ?? current.Device,
            Kind = update.Kind ?? current.Kind,
            StaleAfterSeconds = update.StaleAfterSeconds ?? current.StaleAfterSeconds,
        };
    }

    private SensorInfo ToInfo(string id, PushSensorMeta meta, SensorValueKind valueKind)
    {
        var isNumber = valueKind == SensorValueKind.Number;
        var unit = isNumber ? meta.Unit ?? Units.None : Units.None;
        var category = meta.Category ?? SensorCategory.Custom;
        var kind = meta.Kind ?? valueKind switch
        {
            SensorValueKind.Number => SensorHints.KindForUnit(unit),
            SensorValueKind.Color => SensorKinds.Color,
            _ => SensorKinds.Text,
        };

        return new SensorInfo
        {
            Id = id,
            Name = meta.Name ?? id,
            Device = meta.Device ?? "Push",
            Category = category,
            Kind = kind,
            Unit = unit,
            ValueKind = valueKind,
            Min = isNumber ? meta.Min : null,
            Max = isNumber ? meta.Max : null,
            Icon = meta.Icon ?? SensorHints.Icon(category, kind),
            StaleAfterSeconds = meta.StaleAfterSeconds ?? _defaultStaleAfterSeconds,
        };
    }

    private sealed class Entry
    {
        public PushSensorMeta Meta { get; set; } = new();

        public SensorInfo Info { get; set; } = new() { Id = "", Name = "" };

        public double? Number { get; set; }

        public string? Text { get; set; }

        public DateTimeOffset PushedAt { get; set; }
    }
}
