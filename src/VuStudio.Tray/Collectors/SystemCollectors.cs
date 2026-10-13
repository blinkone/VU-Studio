using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using VuStudio.Core.Sensors;
using VuStudio.Session;
using VuStudio.Session.Signals;
using VuStudio.Tray.Native;
using Windows.UI.ViewManagement;

namespace VuStudio.Tray.Collectors;

/// <summary>
/// <c>session/locked</c>: <see cref="SystemEvents.SessionSwitch"/> for immediate changes, plus a 5 s poll of
/// <c>WTSSessionInfoEx</c> that re-sends the state (the service expires tray reports after 30 s) and corrects
/// it if an event was missed. The service owns the sensor's definition and merges this with its own events.
/// </summary>
internal sealed class LockStateCollector(ISessionPublisher publisher, ILogger logger) : PollingCollector(publisher, logger)
{
    private readonly LockStateArbiter _arbiter = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _lock = new();

    public override string Name => "Lock state";

    protected override IReadOnlyList<SensorInfo> Sensors => [];

    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    protected override void OnStarting() => SystemEvents.SessionSwitch += OnSessionSwitch;

    protected override void OnStopped() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    protected override ValueTask PollAsync(CancellationToken ct)
    {
        var polled = NativeMethods.IsSessionLocked();
        bool locked;
        lock (_lock)
        {
            locked = _arbiter.OnPoll(polled, _clock.Elapsed);
        }

        Publish(SessionSensors.Locked, locked);
        return ValueTask.CompletedTask;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon => false,
            _ => null,
        };
        if (locked is not { } value)
        {
            return;
        }

        lock (_lock)
        {
            _arbiter.OnEvent(value, _clock.Elapsed);
        }

        Logger.LogInformation("Session {State}", value ? "locked" : "unlocked");
        Publish(SessionSensors.Locked, value);
    }
}

/// <summary><c>session/idle</c>: seconds since the last keyboard/mouse input of this session, every second.</summary>
internal sealed class IdleCollector(ISessionPublisher publisher, ILogger logger) : PollingCollector(publisher, logger)
{
    public override string Name => "Idle time";

    protected override IReadOnlyList<SensorInfo> Sensors => SessionSensors.System.Where(s => s.Id == SessionSensors.Idle).ToList();

    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected override ValueTask PollAsync(CancellationToken ct)
    {
        if (NativeMethods.GetIdleTime() is { } idle)
        {
            Publish(SessionSensors.Idle, Math.Floor(idle.TotalSeconds));
        }
        else
        {
            Invalidate(SessionSensors.Idle);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>session/foreground/process</c> (lower-case exe name) and <c>session/foreground/fullscreen</c>, every second.
/// </summary>
internal sealed class ForegroundCollector(ISessionPublisher publisher, ILogger logger) : PollingCollector(publisher, logger)
{
    private uint _cachedPid;
    private string? _cachedName;

    public override string Name => "Foreground app";

    /// <summary>PID of the foreground window's process (0 = unknown); read by the game collector.</summary>
    public uint ForegroundPid { get; private set; }

    protected override IReadOnlyList<SensorInfo> Sensors =>
        SessionSensors.System.Where(s => s.Id is SessionSensors.ForegroundProcess or SessionSensors.ForegroundFullscreen).ToList();

    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected override ValueTask PollAsync(CancellationToken ct)
    {
        var hwnd = NativeMethods.ForegroundWindow();
        var pid = NativeMethods.WindowProcessId(hwnd);
        ForegroundPid = pid;

        // hwnd is 0 during switches, on the lock screen and on the UAC secure desktop: keep the last name.
        if (pid != 0)
        {
            if (pid != _cachedPid || _cachedName is null)
            {
                _cachedPid = pid;
                _cachedName = ProcessNames.ToExeName(NativeMethods.ProcessImagePath(pid) ?? ProcessNameFallback(pid));
            }

            PublishText(SessionSensors.ForegroundProcess, _cachedName);
        }
        else if (_cachedName is not null)
        {
            PublishText(SessionSensors.ForegroundProcess, _cachedName);
        }

        var fullscreen = FullscreenDetector.IsFullscreen(
            NativeMethods.NotificationState(),
            NativeMethods.WindowRect(hwnd),
            hwnd == 0 ? null : NativeMethods.MonitorRect(hwnd),
            NativeMethods.IsShellWindow(hwnd));
        Publish(SessionSensors.ForegroundFullscreen, fullscreen);
        return ValueTask.CompletedTask;
    }

    /// <summary>Protected or other-user processes refuse <c>OpenProcess</c>: fall back to the name without path.</summary>
    private static string? ProcessNameFallback(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>session/accent</c>: <c>UISettings</c> accent, falling back to the DWM colorization color and the registry
/// (§8.4). Polled every 10 s and on <c>WM_DWMCOLORIZATIONCOLORCHANGED</c> / <c>WM_SETTINGCHANGE("ImmersiveColorSet")</c>
/// (see <see cref="Trigger"/>), debounced 200 ms.
/// </summary>
internal sealed class AccentCollector(ISessionPublisher publisher, ILogger logger) : Collector(publisher, logger)
{
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private UISettings? _settings;
    private bool _uiSettingsFailed;

    public override string Name => "Accent color";

    protected override IReadOnlyList<SensorInfo> Sensors => SessionSensors.System.Where(s => s.Id == SessionSensors.Accent).ToList();

    /// <summary>Requests an early refresh (any thread).</summary>
    public void Trigger()
    {
        try
        {
            if (_trigger.CurrentCount == 0)
            {
                _trigger.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Read() is { } color)
                {
                    PublishText(SessionSensors.Accent, color);
                }
                else
                {
                    Invalidate(SessionSensors.Accent);
                }

                ReportSuccess();
            }
            catch (Exception ex)
            {
                ReportFailure(ex, "reading the accent color");
            }

            if (await _trigger.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            {
                await Task.Delay(200, ct).ConfigureAwait(false); // let the burst of messages settle
                while (_trigger.Wait(0))
                {
                }
            }
        }
    }

    protected override void OnStopped() => _settings = null;

    private string? Read()
    {
        if (!_uiSettingsFailed)
        {
            try
            {
                _settings ??= new UISettings();
                var c = _settings.GetColorValue(UIColorType.Accent);
                return AccentColors.FromRgb(c.R, c.G, c.B);
            }
            catch (Exception ex)
            {
                _uiSettingsFailed = true;
                Logger.LogInformation(ex, "UISettings accent unavailable, using the DWM colorization color");
            }
        }

        if (NativeMethods.DwmColorization() is { } argb)
        {
            return AccentColors.FromArgb(argb);
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent");
        return key?.GetValue("AccentColorMenu") is int abgr ? AccentColors.FromAbgr(unchecked((uint)abgr)) : null;
    }
}
