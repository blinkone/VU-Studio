namespace VuStudio.Hardware;

/// <summary>How long <see cref="VuHubClient"/> waits for each kind of reply (<c>docs/vu1-protocol.md</c> §2).</summary>
public sealed record VuTimeouts
{
    public static VuTimeouts Default { get; } = new();

    /// <summary>Needle and backlight sets.</summary>
    public TimeSpan Value { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Everything without a more specific timeout: UID, device map, info, easing, calibration, power.</summary>
    public TimeSpan Command { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>E-paper commands: clear, cursor, image frames and show.</summary>
    public TimeSpan Display { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Rescan, provisioning and reset.</summary>
    public TimeSpan Bus { get; init; } = TimeSpan.FromSeconds(5);
}
