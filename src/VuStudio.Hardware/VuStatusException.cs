using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware;

/// <summary>The hub answered a command with a status other than <see cref="VuStatus.Ok"/>.</summary>
public sealed class VuStatusException : VuHubException
{
    public VuStatusException(VuCommand command, VuStatus status)
        : base(command, $"{command} failed with status {status} (0x{(uint)status:X4}).")
    {
        Status = status;
    }

    public VuStatus Status { get; }
}
