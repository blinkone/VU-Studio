namespace VuStudio.Hardware.Discovery;

/// <summary>
/// Lists serial ports that may be the VU1 hub, best candidate first. <see cref="HubPortFinder"/> is the portable
/// implementation; a platform-specific one (for example SetupAPI on Windows) can replace it.
/// </summary>
public interface IHubPortLocator
{
    /// <summary>Ports that are present now. Must not throw for ordinary enumeration failures.</summary>
    IReadOnlyList<HubPortCandidate> FindCandidates();
}
