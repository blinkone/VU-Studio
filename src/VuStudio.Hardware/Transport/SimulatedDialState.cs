using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware.Transport;

/// <summary>A snapshot of one simulated dial.</summary>
public sealed class SimulatedDialState
{
    public required string Uid { get; init; }

    public required int BusIndex { get; init; }

    /// <summary>False until the hub receives <see cref="VuCommand.ProvisionDevice"/>.</summary>
    public required bool Provisioned { get; init; }

    /// <summary>Last percent set (0–100).</summary>
    public required int Percent { get; init; }

    /// <summary>Current raw DAC value: set directly, or derived from the percent through the calibration.</summary>
    public required int RawValue { get; init; }

    public required uint CalibrationMax { get; init; }

    public required uint CalibrationHalf { get; init; }

    public required LedLevels Backlight { get; init; }

    public required Easing NeedleEasing { get; init; }

    public required Easing BacklightEasing { get; init; }

    /// <summary>Number of <see cref="VuCommand.DisplayShowImage"/> commands executed (e-paper refreshes).</summary>
    public required int RefreshCount { get; init; }

    /// <summary>Status every command for this dial currently fails with; null when healthy.</summary>
    public required VuStatus? Fault { get; init; }

    /// <summary>The 3600-byte image buffer written by clear, cursor and image-data commands.</summary>
    public required ReadOnlyMemory<byte> ImageBuffer { get; init; }

    /// <summary>The 3600-byte image shown on the e-paper (copied from the buffer on show).</summary>
    public required ReadOnlyMemory<byte> DisplayedImage { get; init; }

    public MonoBitmap GetDisplayedBitmap() => EInkEncoder.Decode(DisplayedImage.Span);
}
