using System.Globalization;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>The moment a collection pass happens.</summary>
/// <param name="Time">Monotonic time since the provider started; used for rates and throttling.</param>
/// <param name="UtcNow">Wall-clock time.</param>
internal readonly record struct SampleContext(TimeSpan Time, DateTimeOffset UtcNow);

/// <summary>
/// One source of operating-system metrics. <see cref="Collect"/> runs on every provider tick, on the
/// provider's task, so it must be quick and must not block on slow I/O: collectors that need a slow call
/// start it in the background and publish its result on a later pass.
/// </summary>
internal interface ISystemCollector : IDisposable
{
    void Collect(SensorRegistry sensors, SampleContext context);
}

/// <summary>Lets a collector run less often than the provider ticks.</summary>
internal sealed class Throttle(TimeSpan every)
{
    private TimeSpan? _last;

    /// <summary>True the first time and then whenever <paramref name="now"/> is at least the period after the last true.</summary>
    public bool Due(TimeSpan now)
    {
        if (_last is { } last && now - last < every)
        {
            return false;
        }

        _last = now;
        return true;
    }
}

/// <summary>Defining metrics and reading kernel text files.</summary>
internal static class Metrics
{
    /// <summary>Defines a numeric sensor unless it already exists.</summary>
    public static void EnsureNumber(
        SensorRegistry sensors,
        string id,
        string name,
        string device,
        SensorCategory category,
        string kind,
        string unit,
        double? min,
        double? max,
        string? icon = null)
    {
        if (!sensors.IsDefined(id))
        {
            sensors.Define(Number(id, name, device, category, kind, unit, min, max, icon));
        }
    }

    public static SensorInfo Number(
        string id,
        string name,
        string device,
        SensorCategory category,
        string kind,
        string unit,
        double? min,
        double? max,
        string? icon = null) => new()
        {
            Id = id,
            Name = name,
            Device = device,
            Category = category,
            Kind = kind,
            Unit = unit,
            Min = min,
            Max = max,
            Icon = icon ?? SensorHints.Icon(category, kind),
        };

    /// <summary>The memory sensors shared by every operating system: <c>mem/usage|used|available|total</c>.</summary>
    public static void PublishMemory(SensorRegistry sensors, double totalBytes, double availableBytes)
    {
        const string device = "Memory";
        var used = Math.Max(0, totalBytes - availableBytes);
        EnsureNumber(sensors, "mem/usage", "Memory usage", device, SensorCategory.Memory, SensorKinds.Load, Units.Percent, 0, 100, "memory");
        EnsureNumber(sensors, "mem/used", "Memory used", device, SensorCategory.Memory, SensorKinds.Data, Units.Bytes, 0, totalBytes, "memory");
        EnsureNumber(sensors, "mem/available", "Memory available", device, SensorCategory.Memory, SensorKinds.Data, Units.Bytes, 0, totalBytes, "memory");
        EnsureNumber(sensors, "mem/total", "Memory total", device, SensorCategory.Memory, SensorKinds.Data, Units.Bytes, 0, totalBytes, "memory");
        sensors.Publish("mem/usage", totalBytes > 0 ? Math.Clamp(100 * used / totalBytes, 0, 100) : 0);
        sensors.Publish("mem/used", used);
        sensors.Publish("mem/available", availableBytes);
        sensors.Publish("mem/total", totalBytes);
    }
}

/// <summary>Tolerant readers for /proc and /sys files, which can vanish or refuse to be read at any moment.</summary>
internal static class SysFs
{
    public static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static double? ReadDouble(string path) =>
        double.TryParse(ReadText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    public static IReadOnlyList<string> Directories(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> Files(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// True when a PCI device is runtime-suspended. Reading its sensors would wake it up (and keep a laptop's
    /// discrete GPU from ever sleeping), so collectors leave such devices alone.
    /// </summary>
    public static bool IsSuspended(string deviceDirectory) =>
        SysFs.ReadText(Path.Combine(deviceDirectory, "power", "runtime_status")) == "suspended";
}
