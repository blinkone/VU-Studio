using SkiaSharp;

namespace VuStudio.Rendering.Drawing;

/// <summary>Guards against NaN/∞ and absurd values coming from user-edited documents, and pixel snapping.</summary>
internal static class Num
{
    public static float F(double value, double fallback = 0) => (float)(double.IsFinite(value) ? value : fallback);

    public static float Clamp(double value, double min, double max, double fallback) =>
        (float)Math.Clamp(double.IsFinite(value) ? value : fallback, min, max);

    public static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>Rounds half up to a whole pixel.</summary>
    public static float Snap(float value) => MathF.Floor(value + 0.5f);

    /// <summary>
    /// Position for the centre line of an axis-aligned stroke of integer width so it covers whole pixels:
    /// half-pixel for odd widths, whole pixel for even ones.
    /// </summary>
    public static float SnapStroke(float value, int width) =>
        width % 2 == 1 ? MathF.Floor(value) + 0.5f : Snap(value);

    /// <summary>Integer stroke width (≥ 1) used for axis-aligned strokes.</summary>
    public static int PixelWidth(float width) => Math.Max(1, (int)MathF.Round(width));

    /// <summary>A rectangle from position and size, normalised (negative sizes flip) and rounded to pixels.</summary>
    public static SKRectI PixelRect(double x, double y, double width, double height)
    {
        var x0 = F(x);
        var y0 = F(y);
        var x1 = x0 + F(width);
        var y1 = y0 + F(height);
        return new SKRectI(
            (int)Snap(Math.Min(x0, x1)),
            (int)Snap(Math.Min(y0, y1)),
            (int)Snap(Math.Max(x0, x1)),
            (int)Snap(Math.Max(y0, y1)));
    }

    public static SKRect Oval(double cx, double cy, double radius) =>
        new((float)(cx - radius), (float)(cy - radius), (float)(cx + radius), (float)(cy + radius));
}
