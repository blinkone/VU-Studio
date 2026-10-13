using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;

namespace VuStudio.Core.Engine;

public enum HubConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

public sealed record HubStatus(HubConnectionState State, string? Port, string? Message, bool Simulated)
{
    public static HubStatus Disconnected { get; } = new(HubConnectionState.Disconnected, null, null, false);
}

/// <summary>A physical dial as seen by the hardware layer.</summary>
public sealed record DialInfo(
    string Uid,
    int BusIndex,
    bool Online,
    string? FirmwareVersion = null,
    string? HardwareVersion = null,
    string? ProtocolVersion = null,
    string? BuildHash = null,
    Easing? NeedleEasing = null,
    Easing? BacklightEasing = null);

public enum CalibrationPoint
{
    /// <summary>Raw value that corresponds to 100 %.</summary>
    Full,

    /// <summary>Raw value that corresponds to 50 %.</summary>
    Half,
}

/// <summary>
/// Desired-state sink for the dials. Setters are cheap and thread-safe: they only record the
/// latest target; the implementation sends differences to the hardware at its own pace.
/// </summary>
public interface IDialOutput
{
    HubStatus Status { get; }

    IReadOnlyList<DialInfo> Dials { get; }

    /// <summary>Raised when <see cref="Status"/> or <see cref="Dials"/> change.</summary>
    event Action? Changed;

    /// <summary>Needle target 0..100 (fractions allowed; rounded by the implementation).</summary>
    void SetNeedle(string uid, double percent);

    void SetBacklight(string uid, LedLevels levels);

    /// <summary>Desired full-scale backlight transition time; mapped to on-device easing.</summary>
    void SetBacklightTransition(string uid, int milliseconds);

    void SetNeedleEasing(string uid, Easing easing);

    /// <summary>Queues a face upload (skipped when identical to the last uploaded bitmap).</summary>
    void SetFace(string uid, MonoBitmap face);

    /// <summary>Re-sends every desired value (after a reconnect or when the UI asks for a refresh).</summary>
    void Invalidate();

    /// <summary>Wiggles the needle and flashes the light so the user can find a dial.</summary>
    Task IdentifyAsync(string uid, CancellationToken cancellationToken = default);

    Task RescanAsync(bool provision, CancellationToken cancellationToken = default);

    /// <summary>Moves the needle by raw DAC value (calibration helper).</summary>
    Task<bool> SetRawAsync(string uid, int raw, CancellationToken cancellationToken = default);

    Task<bool> CalibrateAsync(string uid, CalibrationPoint point, int raw, CancellationToken cancellationToken = default);
}
