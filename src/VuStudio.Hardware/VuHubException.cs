using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware;

/// <summary>
/// A hub command failed: an error status, no reply in time, or a reply that cannot be interpreted.
/// The serial link itself is still usable; a lost link surfaces as <see cref="IOException"/> instead.
/// </summary>
public class VuHubException : Exception
{
    public VuHubException(VuCommand command, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Command = command;
    }

    public VuCommand Command { get; }
}
