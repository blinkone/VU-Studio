using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Processes;

/// <summary>
/// Watches named programs: whether they run, how many instances there are, their CPU use and their memory.
/// </summary>
/// <remarks>
/// Sensor ids, per program: <c>{name}/running</c> (0/1), <c>{name}/count</c>, <c>{name}/cpu</c> (% of the whole
/// machine, summed over instances) and <c>{name}/memory</c> (bytes of working set, summed). The CPU figure
/// needs two samples, so it appears from the second poll on.
/// </remarks>
internal sealed class ProcessProvider : PollingSensorProvider
{
    private readonly IReadOnlyList<Watched> _watched;
    private readonly IReadOnlySet<string> _names;
    private readonly IProcessSource _source;
    private readonly TimeProvider _time;
    private readonly int _cores;
    private readonly double _staleAfter;
    private readonly Dictionary<int, TimeSpan> _previousCpu = [];
    private long? _previousTimestamp;
    private SensorRegistry? _sensors;

    public ProcessProvider(
        string id,
        IEnumerable<string> names,
        TimeSpan interval,
        IProcessSource source,
        TimeProvider time,
        int cores,
        ILogger logger)
        : base(id, interval, logger)
    {
        _source = source;
        _time = time;
        _cores = Math.Max(1, cores);
        _staleAfter = SensorHints.StaleAfterSeconds(interval);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var watched = new List<Watched>();
        foreach (var name in names.Select(ProcessNames.Normalize).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.Ordinal))
        {
            watched.Add(new Watched(name, LocalIds.Unique(LocalIds.Slug(name, "process"), used)));
        }

        _watched = watched;
        _names = watched.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sensors = new SensorRegistry(sink, _staleAfter);
        foreach (var program in _watched)
        {
            Define(program, "running", "Running", SensorKinds.State, Units.None, 1, "cog");
            Define(program, "count", "Instances", SensorKinds.Count, Units.None, null, "counter");
            Define(program, "cpu", "CPU usage", SensorKinds.Load, Units.Percent, 100, "cpu-64-bit");
            Define(program, "memory", "Memory (working set)", SensorKinds.Data, Units.Bytes, null, "memory");
        }

        return Task.CompletedTask;
    }

    protected override Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var sensors = _sensors ?? throw new InvalidOperationException("The provider was not initialised.");
        if (_watched.Count == 0)
        {
            throw new InvalidOperationException("No process names are configured.");
        }

        var now = _time.GetTimestamp();
        var elapsed = _previousTimestamp is { } previous ? _time.GetElapsedTime(previous, now).TotalSeconds : 0;
        _previousTimestamp = now;

        var samples = _source.Snapshot(_names);

        // CPU use is the change in processor time since the previous poll, over the time that passed and
        // all the cores, so that 100 % means every core is busy. A process we did not see last time has no
        // baseline yet, and a process id that was reused shows a smaller total than before: both count as zero.
        var cpuNow = new Dictionary<int, TimeSpan>();
        foreach (var program in _watched)
        {
            var instances = samples.Where(s => s.Name == program.Name).ToList();
            var cpuSeconds = 0.0;
            foreach (var instance in instances)
            {
                if (instance.CpuTime is not { } cpu)
                {
                    continue;
                }

                cpuNow[instance.Pid] = cpu;
                if (_previousCpu.TryGetValue(instance.Pid, out var before) && cpu >= before)
                {
                    cpuSeconds += (cpu - before).TotalSeconds;
                }
            }

            sensors.Publish($"{program.Id}/running", instances.Count > 0 ? 1 : 0);
            sensors.Publish($"{program.Id}/count", instances.Count);
            sensors.Publish($"{program.Id}/memory", instances.Sum(i => i.WorkingSetBytes));
            if (elapsed > 0)
            {
                sensors.Publish($"{program.Id}/cpu", Math.Clamp(100 * cpuSeconds / (elapsed * _cores), 0, 100));
            }
            else
            {
                sensors.Invalidate($"{program.Id}/cpu");
            }
        }

        _previousCpu.Clear();
        foreach (var (pid, cpu) in cpuNow)
        {
            _previousCpu[pid] = cpu;
        }

        return Task.CompletedTask;
    }

    private void Define(Watched program, string leaf, string name, string kind, string unit, double? max, string icon) =>
        _sensors!.Define(new SensorInfo
        {
            Id = $"{program.Id}/{leaf}",
            Name = name,
            Device = program.Name,
            Category = SensorCategory.Process,
            Kind = kind,
            Unit = unit,
            Min = 0,
            Max = max,
            Icon = icon,
        });

    private sealed record Watched(string Name, string Id);
}
