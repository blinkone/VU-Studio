using VuStudio.Core.Sensors;

namespace VuStudio.Server.Live;

/// <summary>
/// Owns the <c>session/locked</c> sensor. The value combines two sources: Windows service session
/// events (authoritative while running as a service) and the tray app's reports, which expire when
/// the tray goes away so a crashed tray never leaves the dials dark.
/// </summary>
public sealed class SessionState
{
    private static readonly TimeSpan TrayValidity = TimeSpan.FromSeconds(30);

    private readonly ISensorSink _sink;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private bool? _serviceLocked;
    private bool? _trayLocked;
    private DateTimeOffset _trayReported;

    public SessionState(SensorHub hub, TimeProvider time)
    {
        _time = time;
        _sink = hub.CreateSink(WellKnownSensors.SessionProvider);
        _sink.Define(new SensorInfo
        {
            Id = "locked",
            Name = "Workstation locked",
            Category = SensorCategory.System,
            Kind = SensorKinds.State,
            Min = 0,
            Max = 1,
            Icon = "lock",
            StaleAfterSeconds = 60 * 60 * 24 * 365,
        });
        Publish();
    }

    public bool IsLocked
    {
        get
        {
            lock (_lock)
            {
                return Compute();
            }
        }
    }

    /// <summary>Called from Windows service session-change events.</summary>
    public void SetLocked(bool locked)
    {
        lock (_lock)
        {
            _serviceLocked = locked;
        }

        Publish();
    }

    /// <summary>Called when the tray app reports the lock state.</summary>
    public void ReportFromTray(bool locked)
    {
        lock (_lock)
        {
            _trayLocked = locked;
            _trayReported = _time.GetUtcNow();
        }

        Publish();
    }

    /// <summary>Re-evaluates expiry of the tray report; call periodically.</summary>
    public void Refresh() => Publish();

    private bool Compute()
    {
        if (_serviceLocked is { } service)
        {
            return service;
        }

        return _trayLocked == true && _time.GetUtcNow() - _trayReported < TrayValidity;
    }

    private void Publish()
    {
        bool locked;
        lock (_lock)
        {
            locked = Compute();
        }

        _sink.Publish("locked", locked ? 1 : 0);
    }
}
