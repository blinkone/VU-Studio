using VuStudio.Hardware.Discovery;

namespace VuStudio.Hardware;

/// <summary>Settings of <see cref="DialController"/>; defaults follow <c>docs/vu1-protocol.md</c>.</summary>
public sealed record DialControllerOptions
{
    /// <summary>
    /// "auto" to discover the hub by USB VID/PID, or a port name ("COM5", "/dev/ttyUSB0"). Discovered ports must
    /// answer a probe first; an explicit port is used as is.
    /// </summary>
    public string Port { get; init; } = HubPortFinder.AutoPort;

    /// <summary>0 = real hardware; N &gt; 0 = an in-process <see cref="Transport.SimulatedHub"/> with N dials.</summary>
    public int Simulate { get; init; }

    public VuTimeouts Timeouts { get; init; } = VuTimeouts.Default;

    /// <summary>How long a discovered port may take to answer the probe before the next candidate is tried.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Pause after each e-paper image frame; needle and backlight changes are sent meanwhile.</summary>
    public TimeSpan InterChunkDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Minimum time between two needle commands to the same dial.</summary>
    public TimeSpan NeedleInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Park the needles at 0 and switch the lights off when the controller stops.</summary>
    public bool ParkOnStop { get; init; } = true;

    /// <summary>First wait before reconnecting; doubles after each failed attempt up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>First wait before retrying a dial whose command failed; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// While a dial is unresponsive, or no dial was found, rescan the bus this often to recover.
    /// <see cref="TimeSpan.Zero"/> disables automatic rescans.
    /// </summary>
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Length of the needle wiggle and light flashes of <see cref="DialController.IdentifyAsync"/>.</summary>
    public TimeSpan IdentifyDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>True when switching to <paramref name="other"/> needs a new connection.</summary>
    internal bool RequiresReconnect(DialControllerOptions other) =>
        !string.Equals((Port ?? "").Trim(), (other.Port ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ||
        Math.Max(0, Simulate) != Math.Max(0, other.Simulate);
}
