using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Drawing;

/// <summary>Draws <see cref="BarLayer"/>s: a solid or segmented linear gauge on whole pixels.</summary>
internal static class BarDrawer
{
    public static void Draw(FaceCanvas face, BarLayer layer)
    {
        var rect = Num.PixelRect(layer.X, layer.Y, layer.Width, layer.Height);
        if (rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        var canvas = face.Canvas;
        var color = face.Color(layer.Ink);
        var inner = rect;
        if (layer.Border)
        {
            using var border = Paints.Hairline(color);
            canvas.DrawRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Width - 1, rect.Height - 1, border);
            inner = new SKRectI(rect.Left + 2, rect.Top + 2, rect.Right - 2, rect.Bottom - 2);
            if (inner.Width < 1 || inner.Height < 1)
            {
                return;
            }
        }

        if (Fraction(face.Context, layer) is not { } fraction)
        {
            return;
        }

        var horizontal = layer.Orientation == Orientation.Horizontal;
        var length = horizontal ? inner.Width : inner.Height;
        using var fill = Paints.Fill(color);
        if (layer.Segments <= 0)
        {
            var filled = (int)Math.Round(fraction * length);
            if (filled > 0)
            {
                canvas.DrawRect(Segment(inner, horizontal, 0, filled), fill);
            }

            return;
        }

        var count = Math.Min(layer.Segments, length);
        var gap = Math.Clamp(Num.F(layer.Gap, 1), 0, Math.Max(0, ((float)length / count) - 1));
        var pitch = (length + gap) / count;
        using var outline = Paints.Hairline(color);
        for (var i = 0; i < count; i++)
        {
            var start = (int)MathF.Round(i * pitch);
            var end = (int)MathF.Round((i * pitch) + pitch - gap);
            if (end <= start)
            {
                end = start + 1;
            }

            var segment = Segment(inner, horizontal, start, end - start);
            if (fraction >= (i + 0.5) / count)
            {
                canvas.DrawRect(segment, fill);
            }
            else if (!layer.Border && segment.Width >= 3 && segment.Height >= 3)
            {
                // Unlit segments keep their outline so the bar's extent stays visible.
                canvas.DrawRect(segment.Left + 0.5f, segment.Top + 0.5f, segment.Width - 1, segment.Height - 1, outline);
            }
        }
    }

    /// <summary>
    /// Fill fraction: the sensor's value (canonical units) or the page value. The range is Min/Max, else the
    /// sensor's suggested range, else the page mapping (which also honours log/sqrt/custom scales).
    /// </summary>
    internal static double? Fraction(FaceRenderContext context, BarLayer layer)
    {
        var min = Finite(layer.Min);
        var max = Finite(layer.Max);
        if (!string.IsNullOrWhiteSpace(layer.SensorId))
        {
            var snapshot = context.Sensors(layer.SensorId.Trim());
            if (snapshot is not { HasFreshNumber: true, Value: { } value })
            {
                return null;
            }

            min ??= snapshot.Info.Min ?? context.Mapping.Min;
            max ??= snapshot.Info.Max ?? context.Mapping.Max;
            return Linear(value, min.Value, max.Value);
        }

        if (context.Value is not { } pageValue || !double.IsFinite(pageValue))
        {
            return null;
        }

        if (min is null && max is null)
        {
            return context.Mapping.ToFraction(pageValue);
        }

        return Linear(pageValue, min ?? context.Mapping.Min, max ?? context.Mapping.Max);
    }

    private static double Linear(double value, double min, double max) =>
        max == min ? (value >= max ? 1 : 0) : Math.Clamp((value - min) / (max - min), 0, 1);

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static SKRect Segment(SKRectI inner, bool horizontal, int start, int length) => horizontal
        ? new SKRect(inner.Left + start, inner.Top, inner.Left + start + length, inner.Bottom)
        : new SKRect(inner.Left, inner.Bottom - start - length, inner.Right, inner.Bottom - start);
}
