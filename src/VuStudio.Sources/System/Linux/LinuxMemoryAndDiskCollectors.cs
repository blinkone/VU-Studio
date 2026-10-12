using System.Globalization;
using System.Text.RegularExpressions;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics.Linux;

/// <summary>Parsing of /proc/meminfo and /proc/diskstats.</summary>
internal static partial class ProcParsers
{
    /// <summary>Every <c>Key: value [kB]</c> line as bytes (kB values are multiplied by 1024).</summary>
    public static Dictionary<string, long> ParseMemInfo(string? text)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result[line[..colon]] = parts.Length > 1 && parts[1].Equals("kB", StringComparison.OrdinalIgnoreCase) ? value * 1024 : value;
        }

        return result;
    }

    /// <summary>One block device of /proc/diskstats. Counters are cumulative; sectors are always 512 bytes.</summary>
    public readonly record struct DiskCounters(string Name, long SectorsRead, long SectorsWritten, long BusyMilliseconds);

    public static IReadOnlyList<DiskCounters> ParseDiskStats(string? text)
    {
        var result = new List<DiskCounters>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            // major minor name reads merged sectors-read ms writes merged sectors-written ms in-flight ms-busy …
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13)
            {
                continue;
            }

            if (long.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var read) &&
                long.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var written) &&
                long.TryParse(f[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var busy))
            {
                result.Add(new DiskCounters(f[2], read, written, busy));
            }
        }

        return result;
    }

    [GeneratedRegex(@"^(?:(?:sd|hd|vd|xvd)[a-z]+\d+|nvme\d+n\d+p\d+|mmcblk\d+p\d+)$")]
    private static partial Regex PartitionName();

    /// <summary>
    /// Whether <paramref name="name"/> is a whole disk worth reporting: not a partition (whose traffic is
    /// already counted in its disk), not a loop, RAM, compressed-RAM, floppy, optical or network block device.
    /// </summary>
    public static bool IsWholeDisk(string root, string name)
    {
        string[] ignoredPrefixes = ["loop", "ram", "zram", "fd", "sr", "nbd"];
        if (ignoredPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        // /sys/block lists whole devices only; partitions live one level deeper.
        var sysBlock = Path.Combine(root, "sys/block");
        return Directory.Exists(sysBlock) ? Directory.Exists(Path.Combine(sysBlock, name)) : !PartitionName().IsMatch(name);
    }
}

/// <summary>Memory (<c>mem/usage|used|available|total</c>) and swap (<c>swap/usage|used|total</c>) from /proc/meminfo.</summary>
internal sealed class LinuxMemoryCollector(string root) : ISystemCollector
{
    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var info = ProcParsers.ParseMemInfo(SysFs.ReadText(Path.Combine(root, "proc/meminfo")));
        if (!info.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            return;
        }

        // MemAvailable (kernel 3.14+) accounts for reclaimable cache; older kernels get the classic estimate.
        var available = info.TryGetValue("MemAvailable", out var a)
            ? a
            : info.GetValueOrDefault("MemFree") + info.GetValueOrDefault("Buffers") + info.GetValueOrDefault("Cached");
        Metrics.PublishMemory(sensors, total, Math.Min(available, total));

        if (info.GetValueOrDefault("SwapTotal") is > 0 and var swapTotal)
        {
            var swapUsed = Math.Max(0, swapTotal - info.GetValueOrDefault("SwapFree"));
            Metrics.EnsureNumber(sensors, "swap/usage", "Swap usage", "Swap", SensorCategory.Memory, SensorKinds.Load, Units.Percent, 0, 100, "memory");
            Metrics.EnsureNumber(sensors, "swap/used", "Swap used", "Swap", SensorCategory.Memory, SensorKinds.Data, Units.Bytes, 0, swapTotal, "memory");
            Metrics.EnsureNumber(sensors, "swap/total", "Swap total", "Swap", SensorCategory.Memory, SensorKinds.Data, Units.Bytes, 0, swapTotal, "memory");
            sensors.Publish("swap/usage", 100.0 * swapUsed / swapTotal);
            sensors.Publish("swap/used", swapUsed);
            sensors.Publish("swap/total", swapTotal);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Read and write throughput (<c>diskio/{device}/read|write</c>, B/s) and busy time (<c>diskio/{device}/busy</c>, %)
/// of whole block devices, from /proc/diskstats.
/// </summary>
internal sealed class LinuxDiskIoCollector(string root) : ISystemCollector
{
    private const long SectorBytes = 512;

    private readonly CounterRate _rates = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disk in ProcParsers.ParseDiskStats(SysFs.ReadText(Path.Combine(root, "proc/diskstats"))))
        {
            if (!ProcParsers.IsWholeDisk(root, disk.Name))
            {
                continue;
            }

            var slug = LocalIds.Slug(disk.Name, "disk");
            seen.Add(slug);
            var read = _rates.Update($"diskio/{slug}/read", disk.SectorsRead * (double)SectorBytes, context.Time);
            var write = _rates.Update($"diskio/{slug}/write", disk.SectorsWritten * (double)SectorBytes, context.Time);
            var busy = _rates.Update($"diskio/{slug}/busy", disk.BusyMilliseconds, context.Time); // ms of busy time per second

            Metrics.EnsureNumber(sensors, $"diskio/{slug}/read", "Read", disk.Name, SensorCategory.Storage, SensorKinds.Throughput, Units.BytesPerSecond, 0, null, "download");
            Metrics.EnsureNumber(sensors, $"diskio/{slug}/write", "Write", disk.Name, SensorCategory.Storage, SensorKinds.Throughput, Units.BytesPerSecond, 0, null, "upload");
            Metrics.EnsureNumber(sensors, $"diskio/{slug}/busy", "Busy time", disk.Name, SensorCategory.Storage, SensorKinds.Load, Units.Percent, 0, 100, "harddisk");

            if (read is { } r)
            {
                sensors.Publish($"diskio/{slug}/read", r);
            }

            if (write is { } w)
            {
                sensors.Publish($"diskio/{slug}/write", w);
            }

            if (busy is { } b)
            {
                sensors.Publish($"diskio/{slug}/busy", Math.Clamp(b / 10, 0, 100));
            }
        }

        foreach (var gone in _active.Except(seen).ToList())
        {
            sensors.RemovePrefix($"diskio/{gone}/");
            _rates.ForgetPrefix($"diskio/{gone}/");
        }

        _active.Clear();
        _active.UnionWith(seen);
    }

    public void Dispose()
    {
    }
}
