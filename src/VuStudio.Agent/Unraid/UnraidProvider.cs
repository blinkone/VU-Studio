using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Agent.Unraid;

/// <summary>
/// Array, parity-check and disk sensors of an Unraid server, read from emhttpd's state files. Never reads
/// SMART, so it does not spin disks up. Ids (under the provider id <c>unraid</c>):
/// <c>array/state|started|usage|used|free|total|problem-disks</c>, <c>parity/state|progress|speed|eta|errors</c>,
/// <c>disk/&lt;name&gt;/temp|usage|spun-down|errors</c>.
/// </summary>
internal sealed class UnraidProvider : PollingSensorProvider
{
    public const string ProviderId = "unraid";
    private const double Kib = 1024;
    private const double StaleAfterSeconds = 120;

    private readonly string _directory;
    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastTemperature = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diskSlugs = new(StringComparer.Ordinal);

    public UnraidProvider(string directory, TimeSpan interval, ILogger logger)
        : base(ProviderId, interval, logger)
    {
        _directory = directory;
    }

    /// <summary>True when the directory holds an Unraid <c>var.ini</c>.</summary>
    public static bool IsPresent(string directory) => File.Exists(Path.Combine(directory, "var.ini"));

    protected override Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var variables = EmhttpIni.Parse(ReadFile("var.ini")).GetValueOrDefault("") ?? new Dictionary<string, string>();
        var disks = EmhttpIni.Parse(ReadFile("disks.ini"));
        if (variables.Count == 0)
        {
            throw new InvalidDataException("var.ini is empty or unreadable.");
        }

        PublishArray(sink, variables, disks);
        PublishParity(sink, variables);
        PublishDisks(sink, disks);
        return Task.CompletedTask;
    }

    private string? ReadFile(string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(_directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PublishArray(ISensorSink sink, IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> disks)
    {
        Text(sink, "array/state", "Array state", "Array", SensorKinds.State, v.Get("mdState") ?? v.Get("fsState") ?? "UNKNOWN", "nas");
        Num(sink, "array/started", "Array started", "Array", SensorKinds.State, Units.None, 0, 1, string.Equals(v.Get("fsState"), "Started", StringComparison.OrdinalIgnoreCase) ? 1 : 0, "nas");
        Num(sink, "array/problem-disks", "Disks with problems", "Array", SensorKinds.Count, Units.None, 0, null,
            (v.Number("mdNumDisabled") ?? 0) + (v.Number("mdNumInvalid") ?? 0) + (v.Number("mdNumMissing") ?? 0), "alert");

        // Capacity: data disks that are present, as the official API computes it. Sizes are KiB.
        double size = 0, used = 0, free = 0;
        foreach (var disk in disks.Values.Where(d => d.Get("type") == "Data" && !string.IsNullOrEmpty(d.Get("device"))))
        {
            size += disk.Number("fsSize") ?? 0;
            used += disk.Number("fsUsed") ?? 0;
            free += disk.Number("fsFree") ?? 0;
        }

        if (size > 0)
        {
            Num(sink, "array/usage", "Array usage", "Array", SensorKinds.Load, Units.Percent, 0, 100, Math.Clamp(100 * used / size, 0, 100), "harddisk");
            Num(sink, "array/used", "Array used", "Array", SensorKinds.Data, Units.Bytes, 0, size * Kib, used * Kib, "harddisk");
            Num(sink, "array/free", "Array free", "Array", SensorKinds.Data, Units.Bytes, 0, size * Kib, free * Kib, "harddisk-plus");
            Num(sink, "array/total", "Array size", "Array", SensorKinds.Data, Units.Bytes, 0, size * Kib, size * Kib, "harddisk");
        }
    }

    private void PublishParity(ISensorSink sink, IReadOnlyDictionary<string, string> v)
    {
        var pos = v.Number("mdResyncPos") ?? 0;
        var total = v.Number("mdResyncSize") ?? 0;
        var dt = v.Number("mdResyncDt") ?? 0;
        var db = v.Number("mdResyncDb") ?? 0;
        var running = pos > 0 && dt > 0;
        var paused = pos > 0 && !running;
        var speed = dt > 0 ? db * Kib / dt : 0;

        Text(sink, "parity/state", "Parity operation", "Parity", SensorKinds.State, running ? "running" : paused ? "paused" : "idle", "shield-check");
        Num(sink, "parity/progress", "Parity progress", "Parity", SensorKinds.Percent, Units.Percent, 0, 100, pos > 0 && total > 0 ? Math.Clamp(100 * pos / total, 0, 100) : 0, "shield-check");
        Num(sink, "parity/speed", "Parity speed", "Parity", SensorKinds.Throughput, Units.BytesPerSecond, 0, null, running ? speed : 0, "speedometer");
        Num(sink, "parity/errors", "Parity errors", "Parity", SensorKinds.Count, Units.None, 0, null, v.Number("sbSyncErrs") ?? 0, "alert");

        const string etaId = "parity/eta";
        Define(sink, etaId, "Parity time left", "Parity", SensorKinds.Duration, Units.Seconds, 0, null, "update");
        if (running && speed > 0)
        {
            sink.Publish(etaId, (total - pos) * Kib / speed);
        }
        else
        {
            sink.Invalidate(etaId);
        }
    }

    private void PublishDisks(ISensorSink sink, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> disks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, disk) in disks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(disk.Get("device")) || (disk.Get("status") ?? "").StartsWith("DISK_NP", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = LocalIds.Unique(LocalIds.Slug(disk.Get("name") ?? name, "disk"), used);
            seen.Add(slug);
            var device = $"Unraid {disk.Get("name") ?? name}";
            var prefix = $"disk/{slug}/";

            // "*" means the disk is in standby; emhttpd does not wake it to read the temperature. Keep the last value.
            var spunDown = disk.Get("spundown") == "1" || disk.Get("temp") == "*";
            Num(sink, prefix + "spun-down", "Spun down", device, SensorKinds.State, Units.None, 0, 1, spunDown ? 1 : 0, "harddisk");
            Define(sink, prefix + "temp", "Temperature", device, SensorKinds.Temperature, Units.Celsius, 0, 80, "thermometer", SensorCategory.Storage);
            if (disk.Number("temp") is { } temp)
            {
                _lastTemperature[slug] = temp;
                sink.Publish(prefix + "temp", temp);
            }
            else if (_lastTemperature.TryGetValue(slug, out var last))
            {
                sink.Publish(prefix + "temp", last);
            }
            else
            {
                sink.Invalidate(prefix + "temp");
            }

            Num(sink, prefix + "errors", "Errors", device, SensorKinds.Count, Units.None, 0, null, disk.Number("numErrors") ?? 0, "alert");
            if (disk.Number("fsSize") is > 0 and var size)
            {
                Num(sink, prefix + "usage", "Usage", device, SensorKinds.Load, Units.Percent, 0, 100, Math.Clamp(100 * (disk.Number("fsUsed") ?? 0) / size, 0, 100), "harddisk");
            }
        }

        foreach (var gone in _diskSlugs.Except(seen).ToList())
        {
            foreach (var id in _defined.Where(i => i.StartsWith($"disk/{gone}/", StringComparison.Ordinal)).ToList())
            {
                sink.Remove(id);
                _defined.Remove(id);
            }

            _lastTemperature.Remove(gone);
        }

        _diskSlugs.Clear();
        _diskSlugs.UnionWith(seen);
    }

    private void Define(ISensorSink sink, string id, string name, string device, string kind, string unit, double? min, double? max, string icon, SensorCategory category = SensorCategory.Storage)
    {
        if (_defined.Add(id))
        {
            sink.Define(new SensorInfo
            {
                Id = id,
                Name = name,
                Device = device,
                Category = category,
                Kind = kind,
                Unit = unit,
                Min = min,
                Max = max,
                Icon = icon,
                StaleAfterSeconds = StaleAfterSeconds,
            });
        }
    }

    private void Num(ISensorSink sink, string id, string name, string device, string kind, string unit, double? min, double? max, double value, string icon)
    {
        Define(sink, id, name, device, kind, unit, min, max, icon);
        sink.Publish(id, value);
    }

    private void Text(ISensorSink sink, string id, string name, string device, string kind, string value, string icon)
    {
        if (_defined.Add(id))
        {
            sink.Define(new SensorInfo
            {
                Id = id,
                Name = name,
                Device = device,
                Category = SensorCategory.Storage,
                Kind = kind,
                ValueKind = SensorValueKind.Text,
                Icon = icon,
                StaleAfterSeconds = StaleAfterSeconds,
            });
        }

        sink.PublishText(id, value);
    }
}
