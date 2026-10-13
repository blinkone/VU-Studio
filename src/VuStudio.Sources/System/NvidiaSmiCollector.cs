using System.Globalization;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>One GPU's row of <c>nvidia-smi --query-gpu</c> output; fields the card does not report are <c>null</c>.</summary>
internal sealed record NvidiaGpu(int Index, double? Load, double? Temperature, double? Power, double? MemoryUsedMiB, double? MemoryTotalMiB, double? Fan);

/// <summary>Parsing of the <c>nvidia-smi</c> CSV.</summary>
internal static class NvidiaSmi
{
    public static readonly string[] Arguments =
    [
        "--query-gpu=utilization.gpu,temperature.gpu,power.draw,memory.used,memory.total,fan.speed",
        "--format=csv,noheader,nounits",
    ];

    /// <summary>One line per GPU, comma separated; <c>[N/A]</c> and <c>[Not Supported]</c> mean "no value".</summary>
    public static IReadOnlyList<NvidiaGpu> Parse(string? output)
    {
        var gpus = new List<NvidiaGpu>();
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length < 6)
            {
                continue;
            }

            gpus.Add(new NvidiaGpu(gpus.Count, Cell(cells[0]), Cell(cells[1]), Cell(cells[2]), Cell(cells[3]), Cell(cells[4]), Cell(cells[5])));
        }

        return gpus;
    }

    private static double? Cell(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}

/// <summary>
/// NVIDIA GPUs through the <c>nvidia-smi</c> tool, when it is installed: <c>gpu/nvidia{n}/load</c>, <c>/temp</c>,
/// <c>/power</c>, <c>/fan</c>, <c>/vram/used</c>, <c>/vram/total</c>, <c>/vram/usage</c>. The tool is slow and
/// wakes sleeping GPUs, so it runs at most every two seconds, never on the provider's tick: a query is started
/// in the background and its answer is published on the next pass.
/// </summary>
internal sealed class NvidiaSmiCollector : ISystemCollector
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecheckBinary = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _locate;
    private readonly Func<string, CancellationToken, Task<ProcessRunResult>> _run;
    private readonly CancellationTokenSource _cancel = new();
    private string? _path;
    private TimeSpan _nextAttempt;
    private Task<IReadOnlyList<NvidiaGpu>?>? _pending;

    public NvidiaSmiCollector(Func<string?>? locate = null, Func<string, CancellationToken, Task<ProcessRunResult>>? run = null)
    {
        _locate = locate ?? LocateBinary;
        _run = run ?? RunBinary;
    }

    /// <summary>The query in flight, if any. Exposed so tests can wait for it.</summary>
    internal Task? Pending => _pending;

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        if (_pending is { IsCompleted: true } done)
        {
            _pending = null;
            if (done.IsCompletedSuccessfully && done.Result is { } gpus)
            {
                Publish(sensors, gpus);
            }
            else
            {
                _nextAttempt = context.Time + RetryAfterFailure;
            }
        }

        if (_pending is not null || context.Time < _nextAttempt)
        {
            return;
        }

        _path ??= _locate();
        if (_path is null)
        {
            _nextAttempt = context.Time + RecheckBinary;
            return;
        }

        _nextAttempt = context.Time + MinInterval;
        _pending = QueryAsync(_path);
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private async Task<IReadOnlyList<NvidiaGpu>?> QueryAsync(string path)
    {
        try
        {
            var result = await _run(path, _cancel.Token).ConfigureAwait(false);
            return result.Succeeded ? NvidiaSmi.Parse(result.StdOut) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _path = null; // maybe uninstalled; look again later
            return null;
        }
    }

    private static void Publish(SensorRegistry sensors, IReadOnlyList<NvidiaGpu> gpus)
    {
        foreach (var gpu in gpus)
        {
            var prefix = $"gpu/nvidia{gpu.Index}";
            var device = gpus.Count > 1 ? $"NVIDIA GPU {gpu.Index}" : "NVIDIA GPU";
            Set(sensors, $"{prefix}/load", "GPU load", device, SensorCategory.Gpu, SensorKinds.Load, Units.Percent, 100, gpu.Load, 1, "expansion-card");
            Set(sensors, $"{prefix}/temp", "GPU temperature", device, SensorCategory.Gpu, SensorKinds.Temperature, Units.Celsius, 100, gpu.Temperature, 1, "thermometer");
            Set(sensors, $"{prefix}/power", "GPU power", device, SensorCategory.Gpu, SensorKinds.Power, Units.Watt, null, gpu.Power, 1, "flash");
            Set(sensors, $"{prefix}/fan", "GPU fan", device, SensorCategory.Cooling, SensorKinds.Fan, Units.Percent, 100, gpu.Fan, 1, "fan");
            Set(sensors, $"{prefix}/vram/used", "VRAM used", device, SensorCategory.Gpu, SensorKinds.Data, Units.Bytes, gpu.MemoryTotalMiB * 1024 * 1024, gpu.MemoryUsedMiB, 1024 * 1024, "memory");
            Set(sensors, $"{prefix}/vram/total", "VRAM total", device, SensorCategory.Gpu, SensorKinds.Data, Units.Bytes, gpu.MemoryTotalMiB * 1024 * 1024, gpu.MemoryTotalMiB, 1024 * 1024, "memory");
            var usage = gpu.MemoryUsedMiB is { } used && gpu.MemoryTotalMiB is > 0 and var total ? 100 * used / total : (double?)null;
            Set(sensors, $"{prefix}/vram/usage", "VRAM usage", device, SensorCategory.Gpu, SensorKinds.Load, Units.Percent, 100, usage, 1, "memory");
        }
    }

    private static void Set(SensorRegistry sensors, string id, string name, string device, SensorCategory category, string kind, string unit, double? max, double? value, double scale, string icon)
    {
        if (value is not { } v)
        {
            if (sensors.IsDefined(id))
            {
                sensors.Invalidate(id);
            }

            return;
        }

        sensors.Define(Metrics.Number(id, name, device, category, kind, unit, 0, max, icon));
        sensors.Publish(id, v * scale);
    }

    /// <summary>
    /// Windows: only the absolute paths the NVIDIA driver installs to, never the PATH (the service runs as SYSTEM and a
    /// writable PATH entry would let anyone plant an <c>nvidia-smi.exe</c>). Elsewhere: the exact name on the PATH,
    /// then the usual directories.
    /// </summary>
    private static string? LocateBinary() => OperatingSystem.IsWindows()
        ? ExecutableLocator.FirstExisting(WindowsCandidates(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
        : ExecutableLocator.Find("nvidia-smi", extraDirectories: ["/usr/bin", "/usr/local/bin", "/usr/local/nvidia/bin"], usePathExt: false);

    /// <summary><c>%SystemRoot%\System32\nvidia-smi.exe</c> and <c>%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe</c>.</summary>
    internal static IReadOnlyList<string> WindowsCandidates(string systemDirectory, string programFiles) =>
        new[]
        {
            string.IsNullOrEmpty(systemDirectory) ? "" : Path.Combine(systemDirectory, "nvidia-smi.exe"),
            string.IsNullOrEmpty(programFiles) ? "" : Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        }.Where(p => p.Length > 0).ToList();

    private static Task<ProcessRunResult> RunBinary(string path, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(new ProcessRunRequest(path) { Arguments = NvidiaSmi.Arguments, Timeout = TimeSpan.FromSeconds(5) }, cancellationToken);
}
