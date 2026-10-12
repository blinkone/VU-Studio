using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware;

/// <summary>No matching reply arrived within the command's timeout.</summary>
public sealed class VuTimeoutException : VuHubException
{
    public VuTimeoutException(VuCommand command, TimeSpan timeout)
        : base(command, $"{command} got no reply within {timeout.TotalMilliseconds:0} ms.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}
