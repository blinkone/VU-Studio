namespace VuStudio.Hardware.Transport;

/// <summary>
/// A line-oriented link to the VU1 hub. An instance is one connection: open it once, close or dispose it, and
/// create a new instance to reconnect. A closed or lost link surfaces as <see cref="IOException"/>.
/// </summary>
public interface IVuTransport : IAsyncDisposable
{
    /// <summary>Port name ("COM5", "/dev/ttyUSB0") or "simulator"; used in status messages.</summary>
    string Name { get; }

    /// <summary>True from a successful <see cref="OpenAsync"/> until the link is closed or lost.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Raised once when the link is lost unexpectedly (device unplugged, I/O error); never raised by
    /// <see cref="CloseAsync"/>. Handlers may run on an internal thread or on the thread whose write failed;
    /// they must return quickly.
    /// </summary>
    event Action<Exception>? Disconnected;

    /// <exception cref="IOException">The port does not exist or cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The port is in use or access is denied.</exception>
    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync();

    /// <summary>Writes one line; the transport appends <c>\r\n</c>.</summary>
    /// <exception cref="IOException">The link is closed or lost.</exception>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next received line without its terminator, or null when none arrives within
    /// <paramref name="timeout"/>. Lines received before the link was lost are still returned.
    /// </summary>
    /// <exception cref="IOException">The link is closed or lost and no buffered line is left.</exception>
    Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Drops every line received so far, including a partially received one.</summary>
    void DiscardInput();
}
