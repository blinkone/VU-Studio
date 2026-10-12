using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Push;

/// <summary>
/// Optional metadata an external program can attach to a pushed sensor. Every member is optional; on a
/// later push only the members that are set replace what the sensor already has.
/// </summary>
/// <param name="Name">Human readable name; defaults to the sensor id.</param>
/// <param name="Unit">Canonical unit (<see cref="Units"/>): <c>%</c>, <c>°C</c>, <c>W</c>, <c>B/s</c>…</param>
/// <param name="Category">Grouping in the sensor catalog; defaults to <see cref="SensorCategory.Custom"/>.</param>
/// <param name="Min">Suggested lower bound for gauges.</param>
/// <param name="Max">Suggested upper bound for gauges.</param>
/// <param name="Icon">Icon id from the bundled icon set.</param>
public sealed record PushSensorMeta(
    string? Name = null,
    string? Unit = null,
    SensorCategory? Category = null,
    double? Min = null,
    double? Max = null,
    string? Icon = null)
{
    /// <summary>Device label shown in the catalog; defaults to <c>Push</c>.</summary>
    public string? Device { get; init; }

    /// <summary>One of <see cref="SensorKinds"/> (or any other string); inferred from the unit when omitted.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// After how many seconds without a new push the value is considered stale. Defaults to the push
    /// provider's <c>staleAfter</c> setting.
    /// </summary>
    public double? StaleAfterSeconds { get; init; }
}
