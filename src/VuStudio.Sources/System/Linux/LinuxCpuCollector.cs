using System.Globalization;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics.Linux;

/// <summary>Cumulative CPU time from one <c>cpu</c> line of /proc/stat, in jiffies.</summary>
internal readonly record struct CpuTimes(string Name, long Busy, long Total);

/// <summary>Parsing of /proc/stat, /proc/loadavg and /proc/cpuinfo.</summary>
internal static partial class ProcParsers
{
    /// <summary>
    /// The <c>cpu</c> (all cores) and <c>cpuN</c> lines. Busy time is everything except idle and iowait; guest
    /// time is not added because the kernel already counts it inside user time.
    /// </summary>
    public static IReadOnlyList<CpuTimes> ParseStat(string? text)
    {
        var result = new List<CpuTimes>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            long Field(int i) => i < parts.Length && long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

            // user nice system idle iowait irq softirq steal
            var idle = Field(4) + Field(5);
            var busy = Field(1) + Field(2) + Field(3) + Field(6) + Field(7) + Field(8);
            result.Add(new CpuTimes(parts[0], busy, busy + idle));
        }

        return result;
    }

    /// <summary>The 1, 5 and 15 minute load averages, or <c>null</c> when the text is not a loadavg line.</summary>
    public static double[]? ParseLoadAverage(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var values = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }

    /// <summary>The processor's marketing name from /proc/cpuinfo, trying the keys x86, ARM and others use.</summary>
    public static string? ParseCpuModel(string? text)
    {
        string[] keys = ["model name", "Model Name", "Hardware", "cpu model", "Processor"];
        var lines = (text ?? "").Split('\n');
        foreach (var key in keys)
        {
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim() == key && line[(colon + 1)..].Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// CPU load from /proc/stat deltas (<c>cpu/load</c>, <c>cpu/{n}/load</c>), load average (<c>loadavg/1</c>,
/// <c>/5</c>, <c>/15</c>) and clock speed from cpufreq (<c>cpu/{n}/freq</c>, average <c>cpu/freq</c>, in MHz).
/// </summary>
internal sealed class LinuxCpuCollector(string root, bool perCore) : ISystemCollector
{
    private readonly Dictionary<string, CpuTimes> _previous = new(StringComparer.Ordinal);
    private string? _device;

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        _device ??= ProcParsers.ParseCpuModel(SysFs.ReadText(Path.Combine(root, "proc/cpuinfo"))) ?? "CPU";

        var cores = 0;
        foreach (var times in ProcParsers.ParseStat(SysFs.ReadText(Path.Combine(root, "proc/stat"))))
        {
            var isTotal = times.Name == "cpu";
            if (!isTotal)
            {
                cores++;
                if (!perCore)
                {
                    continue;
                }
            }

            if (_previous.TryGetValue(times.Name, out var before) && times.Total > before.Total && times.Busy >= before.Busy)
            {
                var id = isTotal ? "cpu/load" : $"cpu/{times.Name[3..]}/load";
                Metrics.EnsureNumber(
                    sensors,
                    id,
                    isTotal ? "CPU total" : $"CPU core {times.Name[3..]}",
                    _device,
                    SensorCategory.Cpu,
                    SensorKinds.Load,
                    Units.Percent,
                    0,
                    100,
                    "cpu-64-bit");
                sensors.Publish(id, Math.Clamp(100.0 * (times.Busy - before.Busy) / (times.Total - before.Total), 0, 100));
            }

            _previous[times.Name] = times;
        }

        PublishLoadAverage(sensors, Math.Max(1, cores));
        PublishFrequencies(sensors, cores);
    }

    public void Dispose()
    {
    }

    private void PublishLoadAverage(SensorRegistry sensors, int cores)
    {
        if (ProcParsers.ParseLoadAverage(SysFs.ReadText(Path.Combine(root, "proc/loadavg"))) is not { } load)
        {
            return;
        }

        string[] windows = ["1", "5", "15"];
        for (var i = 0; i < 3; i++)
        {
            var id = $"loadavg/{windows[i]}";
            Metrics.EnsureNumber(sensors, id, $"Load average ({windows[i]} min)", "System", SensorCategory.System, "loadavg", Units.None, 0, cores, "gauge");
            sensors.Publish(id, load[i]);
        }
    }

    private void PublishFrequencies(SensorRegistry sensors, int cores)
    {
        var sum = 0.0;
        var count = 0;
        double? highest = null;
        for (var core = 0; core < cores; core++)
        {
            var directory = Path.Combine(root, "sys/devices/system/cpu", "cpu" + core.ToString(CultureInfo.InvariantCulture), "cpufreq");
            if (SysFs.ReadDouble(Path.Combine(directory, "scaling_cur_freq")) is not { } kiloHertz)
            {
                continue;
            }

            var mhz = kiloHertz / 1000;
            var max = SysFs.ReadDouble(Path.Combine(directory, "cpuinfo_max_freq")) / 1000;
            highest = Math.Max(highest ?? 0, max ?? mhz);
            sum += mhz;
            count++;
            if (perCore)
            {
                var id = $"cpu/{core}/freq";
                Metrics.EnsureNumber(sensors, id, $"CPU core {core} clock", _device!, SensorCategory.Cpu, SensorKinds.Clock, Units.MHz, 0, max, "speedometer");
                sensors.Publish(id, mhz);
            }
        }

        if (count > 0)
        {
            Metrics.EnsureNumber(sensors, "cpu/freq", "CPU average clock", _device!, SensorCategory.Cpu, SensorKinds.Clock, Units.MHz, 0, highest, "speedometer");
            sensors.Publish("cpu/freq", sum / count);
        }
    }
}
