using System.Globalization;
using System.Text.RegularExpressions;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics.Linux;

/// <summary>
/// AMD GPUs from the amdgpu driver's files in /sys/class/drm/card*/device: <c>gpu/amd{n}/load</c> (%) and
/// <c>gpu/amd{n}/vram/used|total|usage</c>. Temperature, fan and power of the same card come from hwmon.
/// </summary>
internal sealed partial class LinuxAmdGpuCollector(string root) : ISystemCollector
{
    [GeneratedRegex(@"^card\d+$")]
    private static partial Regex CardName();

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var cards = SysFs.Directories(Path.Combine(root, "sys/class/drm"), "card*")
            .Where(d => CardName().IsMatch(Path.GetFileName(d)))
            .OrderBy(d => int.Parse(Path.GetFileName(d).AsSpan(4), CultureInfo.InvariantCulture))
            .Select(d => Path.Combine(d, "device"))
            .Where(d => File.Exists(Path.Combine(d, "gpu_busy_percent")))
            .ToList();

        for (var i = 0; i < cards.Count; i++)
        {
            var device = cards[i];
            if (SysFs.IsSuspended(device))
            {
                continue; // reading would wake the GPU
            }

            var prefix = $"gpu/amd{i}";
            var name = cards.Count > 1 ? $"AMD GPU {i}" : "AMD GPU";
            if (SysFs.ReadDouble(Path.Combine(device, "gpu_busy_percent")) is { } busy)
            {
                Metrics.EnsureNumber(sensors, $"{prefix}/load", "GPU load", name, SensorCategory.Gpu, SensorKinds.Load, Units.Percent, 0, 100, "expansion-card");
                sensors.Publish($"{prefix}/load", Math.Clamp(busy, 0, 100));
            }

            if (SysFs.ReadDouble(Path.Combine(device, "mem_info_vram_total")) is { } total and > 0 &&
                SysFs.ReadDouble(Path.Combine(device, "mem_info_vram_used")) is { } used)
            {
                Metrics.EnsureNumber(sensors, $"{prefix}/vram/used", "VRAM used", name, SensorCategory.Gpu, SensorKinds.Data, Units.Bytes, 0, total, "memory");
                Metrics.EnsureNumber(sensors, $"{prefix}/vram/total", "VRAM total", name, SensorCategory.Gpu, SensorKinds.Data, Units.Bytes, 0, total, "memory");
                Metrics.EnsureNumber(sensors, $"{prefix}/vram/usage", "VRAM usage", name, SensorCategory.Gpu, SensorKinds.Load, Units.Percent, 0, 100, "memory");
                sensors.Publish($"{prefix}/vram/used", used);
                sensors.Publish($"{prefix}/vram/total", total);
                sensors.Publish($"{prefix}/vram/usage", Math.Clamp(100 * used / total, 0, 100));
            }
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Batteries from /sys/class/power_supply (only entries of type <c>Battery</c>, wireless mice included):
/// <c>battery/{name}/capacity</c> (%), <c>/status</c> (text such as Charging or Discharging) and <c>/power</c>
/// (W flowing in or out; from <c>power_now</c>, or <c>current_now</c> times <c>voltage_now</c>).
/// </summary>
internal sealed class LinuxBatteryCollector(string root) : ISystemCollector
{
    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in SysFs.Directories(Path.Combine(root, "sys/class/power_supply"), "*"))
        {
            if (SysFs.ReadText(Path.Combine(directory, "type")) != "Battery")
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var slug = LocalIds.Unique(LocalIds.Slug(name, "battery"), used);
            var device = SysFs.ReadText(Path.Combine(directory, "model_name")) is { Length: > 0 } model ? $"{name} ({model})" : name;

            if (Capacity(directory) is { } capacity)
            {
                Metrics.EnsureNumber(sensors, $"battery/{slug}/capacity", "Charge", device, SensorCategory.Battery, SensorKinds.Level, Units.Percent, 0, 100, "battery");
                sensors.Publish($"battery/{slug}/capacity", Math.Clamp(capacity, 0, 100));
            }

            if (SysFs.ReadText(Path.Combine(directory, "status")) is { Length: > 0 } status)
            {
                var id = $"battery/{slug}/status";
                if (!sensors.IsDefined(id))
                {
                    sensors.Define(new SensorInfo
                    {
                        Id = id,
                        Name = "Status",
                        Device = device,
                        Category = SensorCategory.Battery,
                        Kind = SensorKinds.State,
                        ValueKind = SensorValueKind.Text,
                        Icon = "battery-charging",
                    });
                }

                sensors.PublishText(id, status);
            }

            if (Power(directory) is { } watts)
            {
                Metrics.EnsureNumber(sensors, $"battery/{slug}/power", "Power", device, SensorCategory.Battery, SensorKinds.Power, Units.Watt, 0, null, "flash");
                sensors.Publish($"battery/{slug}/power", watts);
            }
        }
    }

    public void Dispose()
    {
    }

    private static double? Capacity(string directory)
    {
        if (SysFs.ReadDouble(Path.Combine(directory, "capacity")) is { } percent)
        {
            return percent;
        }

        // Some drivers only give raw energy or charge.
        foreach (var unit in new[] { "energy", "charge" })
        {
            if (SysFs.ReadDouble(Path.Combine(directory, unit + "_now")) is { } now &&
                SysFs.ReadDouble(Path.Combine(directory, unit + "_full")) is { } full and > 0)
            {
                return 100 * now / full;
            }
        }

        return null;
    }

    private static double? Power(string directory)
    {
        if (SysFs.ReadDouble(Path.Combine(directory, "power_now")) is { } microWatts)
        {
            return Math.Abs(microWatts) / 1e6;
        }

        return SysFs.ReadDouble(Path.Combine(directory, "current_now")) is { } microAmps &&
               SysFs.ReadDouble(Path.Combine(directory, "voltage_now")) is { } microVolts
            ? Math.Abs(microAmps) * Math.Abs(microVolts) / 1e12
            : null;
    }
}

/// <summary>Uptime (<c>uptime</c>, seconds, from /proc/uptime) and the number of processes (<c>process/count</c>, from the numeric entries of /proc).</summary>
internal sealed class LinuxHostCollector(string root) : ISystemCollector
{
    private readonly Throttle _processes = new(TimeSpan.FromSeconds(5));

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var uptime = SysFs.ReadText(Path.Combine(root, "proc/uptime"))?.Split(' ', 2)[0];
        if (double.TryParse(uptime, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            Metrics.EnsureNumber(sensors, "uptime", "Uptime", "System", SensorCategory.System, SensorKinds.Duration, Units.Seconds, 0, null, "update");
            sensors.Publish("uptime", seconds);
        }

        if (_processes.Due(context.Time))
        {
            var count = SysFs.Directories(Path.Combine(root, "proc"), "*").Count(d => Path.GetFileName(d).All(char.IsAsciiDigit));
            if (count > 0)
            {
                Metrics.EnsureNumber(sensors, "process/count", "Processes", "System", SensorCategory.System, SensorKinds.Count, Units.None, 0, null, "counter");
                sensors.Publish("process/count", count);
            }
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Everything the Linux kernel tells us about the machine through /proc and /sys. <paramref name="root"/> is
/// <c>/</c> in production and a fake tree in tests.
/// </summary>
internal sealed class LinuxCollector : ISystemCollector
{
    private readonly ISystemCollector[] _parts;

    public LinuxCollector(string root = "/", bool perCore = true)
    {
        _parts =
        [
            new LinuxCpuCollector(root, perCore),
            new LinuxMemoryCollector(root),
            new LinuxDiskIoCollector(root),
            new LinuxHwmonCollector(root),
            new LinuxAmdGpuCollector(root),
            new LinuxBatteryCollector(root),
            new LinuxHostCollector(root),
        ];
    }

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        // One part failing (a file that vanished mid-read) must not silence the others.
        List<Exception>? errors = null;
        foreach (var part in _parts)
        {
            try
            {
                part.Collect(sensors, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException(errors);
        }
    }

    public void Dispose()
    {
        foreach (var part in _parts)
        {
            part.Dispose();
        }
    }
}
