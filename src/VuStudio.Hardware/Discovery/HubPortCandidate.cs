namespace VuStudio.Hardware.Discovery;

/// <summary>
/// A serial port that may be the VU1 hub. Every FT231X board shares the hub's VID/PID, so a candidate is only
/// confirmed once the hub answers on it.
/// </summary>
/// <param name="PortName">"COM5", "/dev/ttyUSB0" or "/dev/cu.usbserial-DQ0164KA".</param>
/// <param name="Description">Where the candidate came from, for logs and the UI.</param>
public sealed record HubPortCandidate(string PortName, string Description);
