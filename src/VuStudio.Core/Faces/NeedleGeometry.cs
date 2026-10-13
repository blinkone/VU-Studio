namespace VuStudio.Core.Faces;

/// <summary>
/// Where the needle pivots and how far it swings, in display pixels. Angles are degrees from
/// vertical, clockwise positive. Defaults are measured from the stock VU1 faces.
/// </summary>
public sealed record NeedleGeometry
{
    public static NeedleGeometry Default { get; } = new();

    public double PivotX { get; init; } = 99.5;

    public double PivotY { get; init; } = 225.5;

    /// <summary>Angle at 0 %.</summary>
    public double StartAngle { get; init; } = -40;

    /// <summary>Angle at 100 %.</summary>
    public double EndAngle { get; init; } = 40;

    public double AngleAt(double fraction) => StartAngle + ((EndAngle - StartAngle) * fraction);

    /// <summary>Point at <paramref name="radius"/> from the pivot along the needle at <paramref name="fraction"/>.</summary>
    public (double X, double Y) PointAt(double fraction, double radius)
    {
        var radians = AngleAt(fraction) * Math.PI / 180;
        return (PivotX + (radius * Math.Sin(radians)), PivotY - (radius * Math.Cos(radians)));
    }
}
