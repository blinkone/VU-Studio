using System.Globalization;
using System.Text.RegularExpressions;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics.Linux;

/// <summary>
/// Every sensor the kernel exposes through hwmon (/sys/class/hwmon/hwmon*): temperatures (<c>temp*_input</c>, m°C),
/// fans (<c>fan*_input</c>, RPM), power (<c>power*_average</c> or <c>_input</c>, µW), voltages (<c>in*_input</c>, mV),
/// currents (<c>curr*_input</c>, mA) and clocks (<c>freq*_input</c>, Hz). Ids are
/// <c>hwmon/{chip}/{temp|fan|power|in|curr|freq}{n}</c>. The set of chips is rescanned every 30 s for hot-plugged drives.
/// </summary>
internal sealed partial class LinuxHwmonCollector(string root) : ISystemCollector
{
    private static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(30);

    private readonly Throttle _rescan = new(RescanEvery);
    private readonly List<Chip> _chips = [];

    [GeneratedRegex(@"^(temp|fan|power|in|curr|freq)(\d+)_(input|average)$")]
    private static partial Regex ChannelFile();

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        if (_rescan.Due(context.Time))
        {
            Scan(sensors);
        }

        foreach (var chip in _chips)
        {
            // Reading a sleeping GPU would wake it; its values simply age until it is awake again.
            if (SysFs.IsSuspended(Path.Combine(chip.Directory, "device")))
            {
                continue;
            }

            foreach (var channel in chip.Channels)
            {
                var raw = SysFs.ReadDouble(channel.Path);
                var value = raw * channel.Scale;
                if (value is { } v && channel.IsPlausible(v))
                {
                    sensors.Publish(channel.Id, v);
                }
                else if (sensors.IsDefined(channel.Id))
                {
                    sensors.Invalidate(channel.Id);
                }
            }
        }
    }

    public void Dispose()
    {
    }

    private void Scan(SensorRegistry sensors)
    {
        var found = new List<Chip>();
        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);
        var deviceNames = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var directory in SysFs.Directories(Path.Combine(root, "sys/class/hwmon"), "hwmon*").OrderBy(HwmonIndex))
        {
            var name = SysFs.ReadText(Path.Combine(directory, "name")) is { Length: > 0 } text ? text : Path.GetFileName(directory);
            var slug = LocalIds.Unique(LocalIds.Slug(name, "chip"), usedSlugs);
            var duplicate = deviceNames[name] = deviceNames.GetValueOrDefault(name) + 1;
            var device = FriendlyName(name) + (duplicate > 1 ? $" {duplicate}" : "");
            var category = CategoryOf(name);

            var channels = DiscoverChannels(directory, slug, device, category);
            if (channels.Count > 0)
            {
                found.Add(new Chip(directory, channels));
            }
        }

        // Sensors of chips that are gone are removed; new ones are defined.
        var previous = _chips.SelectMany(c => c.Channels).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var current = found.SelectMany(c => c.Channels).ToList();
        foreach (var gone in previous.Except(current.Select(c => c.Id)))
        {
            sensors.Remove(gone);
        }

        foreach (var channel in current)
        {
            sensors.Define(channel.Info);
        }

        _chips.Clear();
        _chips.AddRange(found);
    }

    private static List<Channel> DiscoverChannels(string directory, string chip, string device, SensorCategory? chipCategory)
    {
        var present = new Dictionary<(string Kind, int Index), string>();
        foreach (var file in SysFs.Files(directory))
        {
            var match = ChannelFile().Match(Path.GetFileName(file));
            if (!match.Success)
            {
                continue;
            }

            var key = (match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            var suffix = match.Groups[3].Value;

            // power*_average is preferred to power*_input when a chip has both (the first is smoothed); other kinds only have _input.
            if (key.Item1 == "power")
            {
                if (suffix == "average" || !present.ContainsKey(key))
                {
                    present[key] = file;
                }
            }
            else if (suffix == "input")
            {
                present[key] = file;
            }
        }

        var channels = new List<Channel>();
        foreach (var ((kind, index), file) in present.OrderBy(p => p.Key.Kind, StringComparer.Ordinal).ThenBy(p => p.Key.Index))
        {
            var label = SysFs.ReadText(Path.Combine(directory, $"{kind}{index}_label"));
            channels.Add(MakeChannel(chip, device, chipCategory, kind, index, file, string.IsNullOrWhiteSpace(label) ? null : label));
        }

        return channels;
    }

    private static Channel MakeChannel(string chip, string device, SensorCategory? chipCategory, string kind, int index, string file, string? label)
    {
        var id = $"hwmon/{chip}/{kind}{index}";
        var category = chipCategory ?? (kind == "temp" ? SensorCategory.System : SensorCategory.Power);
        switch (kind)
        {
            case "temp":
                var maxTemperature = category switch { SensorCategory.Cpu => 105, SensorCategory.Storage => 85, _ => 100 };
                return new Channel(id, file, 0.001, v => v is > -50 and < 250 and not 0, Metrics.Number(id, label ?? $"Temperature {index}", device, category, SensorKinds.Temperature, Units.Celsius, 0, maxTemperature, "thermometer"));
            case "fan":
                // Whatever chip a fan hangs off, it is cooling.
                return new Channel(id, file, 1, v => v >= 0, Metrics.Number(id, label ?? $"Fan {index}", device, SensorCategory.Cooling, SensorKinds.Fan, Units.Rpm, 0, 5000, "fan"));
            case "power":
                return new Channel(id, file, 1e-6, v => v >= 0, Metrics.Number(id, label ?? $"Power {index}", device, category, SensorKinds.Power, Units.Watt, 0, null, "flash"));
            case "in":
                return new Channel(id, file, 0.001, _ => true, Metrics.Number(id, label ?? $"Voltage {index}", device, category, SensorKinds.Voltage, Units.Volt, null, null, "lightning-bolt"));
            case "curr":
                return new Channel(id, file, 0.001, _ => true, Metrics.Number(id, label ?? $"Current {index}", device, category, SensorKinds.Current, Units.Ampere, null, null, "flash"));
            default:
                return new Channel(id, file, 1e-6, v => v >= 0, Metrics.Number(id, label ?? $"Frequency {index}", device, category, SensorKinds.Clock, Units.MHz, 0, null, "speedometer"));
        }
    }

    private static int HwmonIndex(string directory)
    {
        var name = Path.GetFileName(directory);
        return int.TryParse(name.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue;
    }

    /// <summary>The kind of hardware a chip is, judged by the driver name; <c>null</c> when unknown.</summary>
    internal static SensorCategory? CategoryOf(string chip)
    {
        var name = chip.ToLowerInvariant();
        return name switch
        {
            "k10temp" or "coretemp" or "zenpower" or "zenpower3" or "cpu_thermal" or "cpu-thermal" or "x86_pkg_temp" or "fam15h_power" => SensorCategory.Cpu,
            "amdgpu" or "radeon" or "nouveau" or "nvidia" or "i915" or "xe" => SensorCategory.Gpu,
            "nvme" or "drivetemp" => SensorCategory.Storage,
            "jc42" or "spd5118" => SensorCategory.Memory,
            "acpitz" or "thinkpad" or "dell_smm" or "applesmc" => SensorCategory.Motherboard,
            _ when name.StartsWith("nct", StringComparison.Ordinal) || name.StartsWith("it8", StringComparison.Ordinal) ||
                   name.StartsWith("w83", StringComparison.Ordinal) || name.StartsWith("f71", StringComparison.Ordinal) ||
                   name.StartsWith("asus", StringComparison.Ordinal) || name.StartsWith("pch_", StringComparison.Ordinal) => SensorCategory.Motherboard,
            _ when name.StartsWith("bat", StringComparison.Ordinal) => SensorCategory.Battery,
            _ when name.StartsWith("iwlwifi", StringComparison.Ordinal) || name.StartsWith("mt79", StringComparison.Ordinal) ||
                   name.StartsWith("ath", StringComparison.Ordinal) || name.StartsWith("rtw", StringComparison.Ordinal) ||
                   name.StartsWith("r8169", StringComparison.Ordinal) || name.StartsWith("igc", StringComparison.Ordinal) ||
                   name.StartsWith("ixgbe", StringComparison.Ordinal) || name.StartsWith("mlx", StringComparison.Ordinal) => SensorCategory.Network,
            _ => null,
        };
    }

    private static string FriendlyName(string chip) => chip switch
    {
        "k10temp" or "zenpower" or "zenpower3" => "AMD CPU",
        "coretemp" => "Intel CPU",
        "cpu_thermal" or "cpu-thermal" => "CPU",
        "amdgpu" => "AMD GPU",
        "nvme" => "NVMe drive",
        "drivetemp" => "Drive",
        "acpitz" => "ACPI thermal zone",
        _ => chip,
    };

    private sealed record Chip(string Directory, List<Channel> Channels);

    private sealed record Channel(string Id, string Path, double Scale, Func<double, bool> IsPlausible, SensorInfo Info);
}
