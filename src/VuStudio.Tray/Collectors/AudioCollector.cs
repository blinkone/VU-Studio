using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using VuStudio.Core.Sensors;
using VuStudio.Session;
using VuStudio.Session.Signals;

namespace VuStudio.Tray.Collectors;

/// <summary>
/// Audio meters through WASAPI (NAudio.Wasapi 3.1.0, docs/integrations/windows-sources.md §6):
/// <list type="bullet">
/// <item>default render endpoint (multimedia role): sample peaks master/left/right as 0..100 linear amplitude,
///   sampled at ~30 Hz and published at ~20 Hz (each value is the max of the last 75 ms, so transients survive);</item>
/// <item>master volume % and mute (5 Hz);</item>
/// <item>default capture endpoint (communications role, else console): peak and mute.</item>
/// </list>
/// Every NAudio object lives on one dedicated MTA thread. Devices are re-acquired (debounced 250 ms) when the
/// default device changes (NAudio 3's event wrapper around <c>IMMNotificationClient</c>) or a call fails, and
/// re-tried every 5 s when there is no device. All COM objects are disposed on that thread.
/// </summary>
internal sealed class AudioCollector(ISessionPublisher publisher, ILogger logger) : Collector(publisher, logger)
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(1000.0 / 30);
    private static readonly TimeSpan PublishSlot = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan VolumeInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReacquireDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PeakWindow _master = new(TimeSpan.FromMilliseconds(75));
    private readonly PeakWindow _left = new(TimeSpan.FromMilliseconds(75));
    private readonly PeakWindow _right = new(TimeSpan.FromMilliseconds(75));
    private readonly PeakWindow _mic = new(TimeSpan.FromMilliseconds(75));
    private long _reacquireRequestedTicks = -1;
    private MMDevice? _render;
    private MMDevice? _capture;
    private TimeSpan _nextRenderAttempt;
    private TimeSpan _nextCaptureAttempt;
    private TimeSpan _nextVolume;
    private long _lastSlot = -1;

    public override string Name => "Audio";

    protected override IReadOnlyList<SensorInfo> Sensors => SessionSensors.Audio;

    /// <summary>Forces a device re-acquisition (e.g. after resume from sleep); any thread.</summary>
    public void RequestReacquire() => Interlocked.Exchange(ref _reacquireRequestedTicks, _clock.Elapsed.Ticks);

    protected override Task StartLoop(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(ct);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "VU Studio audio meters",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.SetApartmentState(ApartmentState.MTA); // CoInitializeEx(MTA) before NAudio's CoCreateInstance
        thread.Start();
        return done.Task;
    }

    protected override Task RunAsync(CancellationToken ct) => throw new NotSupportedException();

    private void Run(CancellationToken ct)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDeviceNotificationClient? notifications = null;
        try
        {
            while (!ct.IsCancellationRequested && enumerator is null)
            {
                try
                {
                    enumerator = new MMDeviceEnumerator();
                    notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);

                    // Raised on the Windows audio worker thread, which holds a lock: only flag, never touch devices here.
                    notifications.DefaultDeviceChanged += (_, _) => RequestReacquire();
                    notifications.DeviceStateChanged += (_, _) => RequestReacquire();
                }
                catch (Exception ex)
                {
                    ReportFailure(ex, "creating the device enumerator");
                    notifications?.Dispose();
                    enumerator?.Dispose();
                    notifications = null;
                    enumerator = null;
                    ct.WaitHandle.WaitOne(RetryInterval);
                }
            }

            var next = _clock.Elapsed;
            while (!ct.IsCancellationRequested && enumerator is not null)
            {
                var now = _clock.Elapsed;
                HandleReacquire(now);
                Sample(enumerator, now);

                // Absolute deadlines: oversleeping (15.6 ms timer granularity) is caught up on the next tick.
                next += SampleInterval;
                var wait = next - _clock.Elapsed;
                if (wait < TimeSpan.Zero)
                {
                    next = _clock.Elapsed;
                    wait = TimeSpan.Zero;
                }

                ct.WaitHandle.WaitOne(wait);
            }
        }
        finally
        {
            ReleaseRender();
            ReleaseCapture();
            notifications?.Dispose();
            enumerator?.Dispose();
            InvalidateAll();
        }
    }

    private void HandleReacquire(TimeSpan now)
    {
        var requested = Interlocked.Read(ref _reacquireRequestedTicks);
        if (requested < 0 || now - TimeSpan.FromTicks(requested) < ReacquireDebounce)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reacquireRequestedTicks, -1, requested) != requested)
        {
            return; // another notification arrived meanwhile: debounce again
        }

        Logger.LogInformation("Audio devices changed: re-acquiring the default endpoints");
        ReleaseRender();
        ReleaseCapture();
        _nextRenderAttempt = _nextCaptureAttempt = TimeSpan.Zero;
    }

    private void Sample(MMDeviceEnumerator enumerator, TimeSpan now)
    {
        if (_render is null && now >= _nextRenderAttempt)
        {
            _render = Acquire(enumerator, DataFlow.Render, [Role.Multimedia, Role.Console], "output");
            _nextRenderAttempt = now + RetryInterval;
            if (_render is null)
            {
                foreach (var id in new[] { SessionSensors.AudioPeak, SessionSensors.AudioPeakLeft, SessionSensors.AudioPeakRight, SessionSensors.AudioVolume, SessionSensors.AudioMute })
                {
                    Invalidate(id);
                }
            }
        }

        if (_capture is null && now >= _nextCaptureAttempt)
        {
            _capture = Acquire(enumerator, DataFlow.Capture, [Role.Communications, Role.Console], "microphone");
            _nextCaptureAttempt = now + RetryInterval;
            if (_capture is null)
            {
                Invalidate(SessionSensors.Mic);
                Invalidate(SessionSensors.MicMute);
            }
        }

        var readVolume = now >= _nextVolume;
        if (readVolume)
        {
            _nextVolume = now + VolumeInterval;
        }

        if (_render is { } render)
        {
            try
            {
                var meter = render.AudioMeterInformation;
                var master = meter.MasterPeakValue;
                var channels = meter.PeakValues.Count;
                var left = channels > 0 ? meter.PeakValues[0] : master;
                var right = channels > 1 ? meter.PeakValues[1] : left;
                _master.Add(now, master);
                _left.Add(now, left);
                _right.Add(now, right);
                if (readVolume)
                {
                    var volume = render.AudioEndpointVolume;
                    Publish(SessionSensors.AudioVolume, Math.Round(volume.MasterVolumeLevelScalar * 100, 1));
                    Publish(SessionSensors.AudioMute, volume.Mute);
                }

                ReportSuccess();
            }
            catch (Exception ex)
            {
                ReportFailure(ex, "reading the output meter");
                ReleaseRender();
                _nextRenderAttempt = now + TimeSpan.FromSeconds(1);
            }
        }

        if (_capture is { } capture)
        {
            try
            {
                _mic.Add(now, capture.AudioMeterInformation.MasterPeakValue);
                if (readVolume)
                {
                    Publish(SessionSensors.MicMute, capture.AudioEndpointVolume.Mute);
                }
            }
            catch (Exception ex)
            {
                ReportFailure(ex, "reading the microphone meter");
                ReleaseCapture();
                _nextCaptureAttempt = now + TimeSpan.FromSeconds(1);
            }
        }

        // Publish once per 50 ms slot (~20 Hz) from the ~30 Hz samples.
        var slot = now.Ticks / PublishSlot.Ticks;
        if (slot == _lastSlot)
        {
            return;
        }

        _lastSlot = slot;
        if (_render is not null)
        {
            PublishPeak(SessionSensors.AudioPeak, _master, now);
            PublishPeak(SessionSensors.AudioPeakLeft, _left, now);
            PublishPeak(SessionSensors.AudioPeakRight, _right, now);
        }

        if (_capture is not null)
        {
            PublishPeak(SessionSensors.Mic, _mic, now);
        }
    }

    private void PublishPeak(string id, PeakWindow window, TimeSpan now)
    {
        if (window.Max(now) is { } peak)
        {
            Publish(id, Math.Round(Math.Clamp(peak, 0, 1) * 100, 1));
        }
    }

    private MMDevice? Acquire(MMDeviceEnumerator enumerator, DataFlow flow, Role[] roles, string what)
    {
        foreach (var role in roles)
        {
            try
            {
                if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device))
                {
                    Logger.LogInformation("Audio {What}: {Device}", what, SafeName(device));
                    return device;
                }
            }
            catch (Exception ex)
            {
                ReportFailure(ex, $"opening the default {what} device");
            }
        }

        Logger.LogDebug("No default audio {What} device", what);
        return null;
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private void ReleaseRender()
    {
        Dispose(ref _render);
        _master.Clear();
        _left.Clear();
        _right.Clear();
    }

    private void ReleaseCapture()
    {
        Dispose(ref _capture);
        _mic.Clear();
    }

    private void Dispose(ref MMDevice? device)
    {
        try
        {
            device?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Disposing an audio device failed");
        }

        device = null;
    }
}
