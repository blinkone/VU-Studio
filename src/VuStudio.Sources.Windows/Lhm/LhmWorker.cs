using System.Runtime.ExceptionServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.Common;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>The part of <c>Computer</c> the worker needs; faked in tests.</summary>
internal interface ILhmSession
{
    /// <summary>Slow (seconds). Called once, on the worker thread.</summary>
    void Open();

    /// <summary>Runs <paramref name="visitor"/> over the computer (one update pass).</summary>
    void Accept(IVisitor visitor);

    void Reset();

    void Close();
}

/// <summary>Consistent view of the worker state for the watchdog.</summary>
internal readonly record struct LhmProgress(
    LhmPhase Phase,
    long PhaseStartedAt,
    long CompletedPasses,
    int CpuTemperatureMisses,
    int FailedHardware,
    string? LastError,
    Exception? Fault);

/// <summary>
/// Owns the dedicated thread that opens LibreHardwareMonitor, updates it on a fixed interval and publishes the
/// readings. <c>Update()</c> is not thread-safe, so Open, Update, Reset and Close all run on this one thread.
/// The thread is a background thread: if LHM hangs it can be abandoned and never keeps the process alive.
/// </summary>
internal sealed class LhmWorker
{
    private readonly ILhmSession _session;
    private readonly ISensorSink _sink;
    private readonly LhmSettings _settings;
    private readonly LhmTimings _timings;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedHardwareIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedHardware = new(StringComparer.Ordinal);
    private readonly PassVisitor _visitor;

    private volatile bool _abandoned;
    private LhmPhase _phase = LhmPhase.Opening;
    private long _phaseStartedAt;
    private long _completedPasses;
    private int _cpuTemperatureMisses;
    private int _failedHardware;
    private string? _lastError;
    private Exception? _fault;

    // pass state (worker thread only)
    private long _pass;
    private bool _publishCurrent = true;
    private int _cpuTemperatureSensors;
    private int _cpuTemperatureValid;
    private readonly double _staleAfterSeconds;

    public LhmWorker(ILhmSession session, ISensorSink sink, LhmSettings settings, LhmTimings timings, ILogger log, TimeProvider? time = null)
    {
        _session = session;
        _sink = sink;
        _settings = settings;
        _timings = timings;
        _log = log;
        _time = time ?? TimeProvider.System;
        _visitor = new PassVisitor(this);
        _staleAfterSeconds = Math.Max(10, settings.Interval.TotalSeconds * 5);
        _thread = new Thread(Main) { IsBackground = true, Name = "VuStudio LHM" };
    }

    public void Start()
    {
        SetPhase(LhmPhase.Opening);
        _thread.Start();
    }

    public LhmProgress GetProgress()
    {
        lock (_gate)
        {
            return new LhmProgress(_phase, _phaseStartedAt, _completedPasses, _cpuTemperatureMisses, _failedHardware, _lastError, _fault);
        }
    }

    public TimeSpan Elapsed(long since) => _time.GetElapsedTime(since);

    /// <summary>Asks the thread to stop and waits a bounded time. If LHM is stuck the thread is abandoned.</summary>
    public async Task StopAsync()
    {
        _stop.Set();
        var finished = await Task.WhenAny(_exited.Task, Task.Delay(_timings.DisposeWait)).ConfigureAwait(false);
        if (finished != _exited.Task)
        {
            _abandoned = true;
            _log.LogWarning("LibreHardwareMonitor thread did not stop within {Seconds} s; abandoning it", _timings.DisposeWait.TotalSeconds);
        }
    }

    /// <summary>Marks the worker as abandoned so a late wake-up never publishes.</summary>
    public void Abandon() => _abandoned = true;

    private void SetPhase(LhmPhase phase)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseStartedAt = _time.GetTimestamp();
        }
    }

    private void Main()
    {
        try
        {
            _session.Open();
            var lastEnd = _time.GetUtcNow();
            var first = true;
            while (!_stop.IsSet)
            {
                if (!first && LhmHealth.ShouldReset(_time.GetUtcNow() - lastEnd, _settings.Interval))
                {
                    _log.LogInformation("Long pause detected (system sleep?); resetting LibreHardwareMonitor");
                    SetPhase(LhmPhase.Resetting);
                    _session.Reset();
                }

                var started = _time.GetTimestamp();
                RunPass(first);
                first = false;
                lastEnd = _time.GetUtcNow();

                var remaining = _settings.Interval - _time.GetElapsedTime(started);
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                if (_stop.Wait(remaining))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault = ex;
            }
        }
        finally
        {
            try
            {
                _session.Close();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Closing LibreHardwareMonitor failed");
            }

            SetPhase(LhmPhase.Stopped);
            _exited.TrySetResult();
        }
    }

    private int _consecutivePassFailures;

    private void RunPass(bool first)
    {
        if (!first)
        {
            SetPhase(LhmPhase.Updating);
        }

        _pass++;
        _cpuTemperatureSensors = 0;
        _cpuTemperatureValid = 0;
        _failedHardwareIds.Clear();
        _lastError = null;
        var completed = false;
        try
        {
            _session.Accept(_visitor);
            completed = true;
            _consecutivePassFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutivePassFailures++;
            _lastError = ex.Message;
            _log.LogWarning(ex, "LibreHardwareMonitor update pass failed ({Count} in a row)", _consecutivePassFailures);
            if (_consecutivePassFailures >= _timings.MaxConsecutivePassFailures)
            {
                throw;
            }
        }

        if (completed)
        {
            RemoveVanished();
        }

        lock (_gate)
        {
            if (completed)
            {
                _cpuTemperatureMisses = _cpuTemperatureSensors > 0 && _cpuTemperatureValid == 0 ? _cpuTemperatureMisses + 1 : 0;
                _completedPasses++;
            }

            _failedHardware = _failedHardwareIds.Count;
        }

        SetPhase(LhmPhase.Idle);
    }

    private void RemoveVanished()
    {
        List<string>? gone = null;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastSeenPass == _pass)
            {
                continue;
            }

            entry.MissedPasses++;
            if (entry.MissedPasses >= _timings.MissedPassesBeforeRemoval)
            {
                (gone ??= []).Add(key);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (var key in gone)
        {
            if (!_abandoned)
            {
                _sink.Remove(_entries[key].LocalId);
            }

            _entries.Remove(key);
        }
    }

    private void VisitHardware(IHardware hardware)
    {
        var updated = true;
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            updated = false;
            var id = hardware.Identifier.ToString();
            _failedHardwareIds.Add(id);
            _lastError = $"{hardware.Name}: {ex.Message}";
            if (_warnedHardware.Add(id))
            {
                _log.LogWarning(ex, "Updating {Hardware} failed", hardware.Name);
            }
        }

        foreach (var sensor in hardware.Sensors)
        {
            _publishCurrent = updated;
            Process(sensor);
        }

        _publishCurrent = true;
        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(_visitor);
        }
    }

    private void Process(ISensor sensor)
    {
        // Keep LHM's own 24 h history off for every sensor, including those we do not publish.
        if (sensor.ValuesTimeWindow != TimeSpan.Zero)
        {
            sensor.ValuesTimeWindow = TimeSpan.Zero;
        }

        var key = sensor.Identifier.ToString();
        if (_skipped.Contains(key))
        {
            return;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = CreateEntry(sensor, key);
            if (entry is null)
            {
                _skipped.Add(key);
                return;
            }

            _entries[key] = entry;
        }

        if (entry.LastSeenPass == _pass)
        {
            return; // duplicate identifier within one pass
        }

        entry.LastSeenPass = _pass;
        entry.MissedPasses = 0;

        var raw = sensor.Value;
        var valid = false;
        double value = 0;
        if (raw is { } f && float.IsFinite(f))
        {
            value = entry.Spec.Convert(f);
            valid = double.IsFinite(value);
        }

        if (entry.IsCpuTemperature)
        {
            _cpuTemperatureSensors++;
            if (valid)
            {
                _cpuTemperatureValid++;
            }
        }

        if (!_publishCurrent || _abandoned)
        {
            return;
        }

        if (valid)
        {
            _sink.Publish(entry.LocalId, value);
            entry.HasValue = true;
        }
        else if (entry.HasValue)
        {
            _sink.Invalidate(entry.LocalId);
            entry.HasValue = false;
        }
    }

    private Entry? CreateEntry(ISensor sensor, string key)
    {
        var spec = LhmMapping.Describe(sensor.SensorType);
        if (spec is null || !_settings.Accepts(sensor.SensorType, spec.Kind))
        {
            return null;
        }

        var hardware = sensor.Hardware;
        var category = LhmMapping.Category(hardware.HardwareType);
        var (min, max) = SensorPresentation.Range(spec.Kind, category);
        var localId = LhmMapping.LocalId(key);
        if (!_abandoned)
        {
            _sink.Define(new SensorInfo
            {
                Id = localId,
                Name = sensor.Name,
                Device = hardware.Name,
                Category = category,
                Kind = spec.Kind,
                Unit = spec.Unit,
                Min = min,
                Max = max,
                Icon = SensorPresentation.Icon(spec.Kind, category),
                StaleAfterSeconds = _staleAfterSeconds,
            });
        }

        return new Entry(localId, spec)
        {
            IsCpuTemperature = hardware.HardwareType == HardwareType.Cpu && sensor.SensorType == SensorType.Temperature,
        };
    }

    /// <summary>Rethrows a worker fault on the caller's thread, keeping the original stack.</summary>
    public static void ThrowIfFaulted(LhmProgress progress)
    {
        if (progress.Fault is { } fault)
        {
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }

    private sealed class Entry(string localId, LhmSensorSpec spec)
    {
        public string LocalId { get; } = localId;

        public LhmSensorSpec Spec { get; } = spec;

        public bool IsCpuTemperature { get; init; }

        public bool HasValue { get; set; }

        public long LastSeenPass { get; set; }

        public int MissedPasses { get; set; }
    }

    private sealed class PassVisitor(LhmWorker owner) : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware) => owner.VisitHardware(hardware);

        public void VisitSensor(ISensor sensor) => owner.Process(sensor);

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
