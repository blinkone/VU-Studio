using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware.Transport;

/// <summary>A command received by <see cref="SimulatedHub"/>, for tests and diagnostics.</summary>
/// <param name="Sequence">1-based order of arrival.</param>
/// <param name="BusIndex">The addressed dial for single-dial commands; null otherwise.</param>
/// <param name="Status">The status the hub produced (<see cref="VuStatus.Ok"/> for data replies).</param>
/// <param name="Replied">False when the reply was dropped by fault injection.</param>
public sealed record SimulatedCommand(
    long Sequence,
    VuCommand Command,
    int? BusIndex,
    ReadOnlyMemory<byte> Payload,
    VuStatus Status,
    bool Replied);
