using System.Globalization;
using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Core.Mapping;
using VuStudio.Rendering.Text;

namespace VuStudio.Rendering.Drawing;

/// <summary>A major position on the scale: dial fraction and label text.</summary>
internal readonly record struct ScaleMark(double Fraction, string Label);

/// <summary>
/// Draws <see cref="ScaleLayer"/>s around the needle pivot: zones, arc, minor and major ticks, labels.
/// Everything follows the page's <see cref="ValueMapping"/> and <see cref="NeedleGeometry"/>, so the printed
/// scale always lines up with the needle.
/// </summary>
internal static class ScaleDrawer
{
    /// <summary>Labels are kept at least this far inside the canvas.</summary>
    private const float CanvasMargin = 2;

    /// <summary>Clearance around each label for collision tests (so labels are ≥ 2 px apart).</summary>
    private const float LabelClearance = 1;

    /// <summary>Pitch of the hatching of <see cref="ZoneStyle.Hatched"/> zones.</summary>
    private const int HatchPitch = 3;

    private const int MaxMinorTicks = 600;

    /// <summary>Dotted zones at least this deep (px) use a sparse grid instead of the dense screen.</summary>
    private const double LargeZone = 16;

    public static void Draw(FaceCanvas face, ScaleLayer layer)
    {
        var geometry = face.Geometry;
        var canvas = face.Canvas;
        var color = face.Color(layer.Ink);
        var majors = Majors(layer, face.Context);
        var minors = Minors(majors, layer.MinorPerMajor);

        foreach (var zone in layer.Zones ?? [])
        {
            if (zone is not null)
            {
                DrawZone(canvas, geometry, zone, color);
            }
        }

        var radius = Num.Clamp(layer.Radius, 0, 1000, 152);
        if (layer.ShowArc)
        {
            var arcRadius = layer.ArcRadius is { } r && double.IsFinite(r) ? Math.Max(0, r) : radius;
            DrawArc(canvas, geometry, arcRadius, Num.Clamp(layer.ArcWidth, 0, 50, 1.5), color);
        }

        var majorWidth = Num.Clamp(layer.MajorWidth, 0, 50, 2.5);
        var minorWidth = Num.Clamp(layer.MinorWidth, 0, 50, 1.2);
        var dotRadius = radius - (Math.Max(majorWidth, minorWidth) / 2);
        foreach (var f in minors)
        {
            DrawTick(canvas, geometry, f, radius, Num.Clamp(layer.MinorLength, 0, 500, 6), minorWidth, layer.TickStyle, dotRadius, color);
        }

        foreach (var mark in majors)
        {
            DrawTick(canvas, geometry, mark.Fraction, radius, Num.Clamp(layer.MajorLength, 0, 500, 14), majorWidth, layer.TickStyle, dotRadius, color);
        }

        if (layer.ShowLabels && majors.Count > 0)
        {
            DrawLabels(face, layer, majors, color);
        }
    }

    /// <summary>
    /// Major ticks: nice values from the mapping, evenly spaced values, or explicit labels on evenly spaced
    /// positions. Sorted by fraction (an inverted mapping runs right to left).
    /// </summary>
    internal static List<ScaleMark> Majors(ScaleLayer layer, FaceRenderContext context)
    {
        var mapping = context.Mapping ?? ValueMapping.Percent;
        var marks = new List<ScaleMark>();
        if (layer.CustomLabels is { Count: > 0 } custom)
        {
            var n = custom.Count;
            for (var i = 0; i < n; i++)
            {
                marks.Add(new ScaleMark(n == 1 ? 0.5 : (double)i / (n - 1), custom[i] ?? ""));
            }

            return marks;
        }

        if (layer.EvenSpacing)
        {
            var n = Math.Clamp(layer.MajorCount, 2, 61);
            for (var i = 0; i < n; i++)
            {
                var f = (double)i / (n - 1);
                marks.Add(new ScaleMark(f, FormatLabel(layer, context, mapping.FromFraction(f))));
            }

            return marks;
        }

        foreach (var value in mapping.SuggestLabelValues(layer.MajorCount))
        {
            if (double.IsFinite(value))
            {
                marks.Add(new ScaleMark(mapping.ToFraction(value), FormatLabel(layer, context, value)));
            }
        }

        marks.Sort((a, b) => a.Fraction.CompareTo(b.Fraction));
        for (var i = marks.Count - 1; i > 0; i--)
        {
            if (marks[i].Fraction - marks[i - 1].Fraction < 1e-4)
            {
                marks.RemoveAt(i);
            }
        }

        return marks;
    }

    /// <summary>
    /// <paramref name="perMajor"/> minor ticks evenly spaced in fraction space between consecutive majors,
    /// continued with the same spacing towards the dial ends when the first/last major is not at an end.
    /// </summary>
    internal static List<double> Minors(IReadOnlyList<ScaleMark> majors, int perMajor)
    {
        var result = new List<double>();
        if (perMajor <= 0 || majors.Count < 2)
        {
            return result;
        }

        perMajor = Math.Min(perMajor, 99);
        for (var i = 0; i + 1 < majors.Count && result.Count < MaxMinorTicks; i++)
        {
            var a = majors[i].Fraction;
            var step = (majors[i + 1].Fraction - a) / (perMajor + 1);
            for (var k = 1; k <= perMajor; k++)
            {
                result.Add(a + (step * k));
            }
        }

        var first = (majors[1].Fraction - majors[0].Fraction) / (perMajor + 1);
        if (first > 0.002)
        {
            for (var f = majors[0].Fraction - first; f >= -1e-6 && result.Count < MaxMinorTicks; f -= first)
            {
                result.Add(Math.Max(0, f));
            }
        }

        var last = (majors[^1].Fraction - majors[^2].Fraction) / (perMajor + 1);
        if (last > 0.002)
        {
            for (var f = majors[^1].Fraction + last; f <= 1 + 1e-6 && result.Count < MaxMinorTicks; f += last)
            {
                result.Add(Math.Min(1, f));
            }
        }

        return result;
    }

    /// <summary>
    /// Label text for a scale value: <see cref="ScaleLayer.LabelDivisor"/> first, then
    /// <see cref="ScaleLayer.LabelFormat"/>, else <see cref="FaceRenderContext.FormatLabel"/> with trailing
    /// zeros trimmed ("2.50" → "2.5").
    /// </summary>
    internal static string FormatLabel(ScaleLayer layer, FaceRenderContext context, double value)
    {
        var divisor = double.IsFinite(layer.LabelDivisor) && layer.LabelDivisor != 0 ? layer.LabelDivisor : 1;
        var v = value / divisor;
        if (Math.Abs(v) < 1e-9 * Math.Max(1, Math.Abs(value)))
        {
            v = 0;
        }

        if (!string.IsNullOrEmpty(layer.LabelFormat))
        {
            return context.FormatLabel(v, layer.LabelFormat);
        }

        var text = context.FormatLabel(v);
        var separator = (context.Culture ?? CultureInfo.InvariantCulture).NumberFormat.NumberDecimalSeparator;
        if (text.Contains(separator, StringComparison.Ordinal) && text.IndexOfAny(['e', 'E']) < 0)
        {
            text = text.TrimEnd('0');
            if (text.EndsWith(separator, StringComparison.Ordinal))
            {
                text = text[..^separator.Length];
            }
        }

        return text is "-0" ? "0" : text;
    }

    /// <summary>Annular sector between two dial fractions (a full pie when <paramref name="inner"/> is ~0).</summary>
    internal static SKPath SectorPath(NeedleGeometry geometry, double from, double to, double inner, double outer)
    {
        var a0 = (float)(geometry.AngleAt(from) - 90);
        var a1 = (float)(geometry.AngleAt(to) - 90);
        var sweep = a1 - a0;
        using var builder = new SKPathBuilder();
        builder.ArcTo(Num.Oval(geometry.PivotX, geometry.PivotY, outer), a0, sweep, true);
        if (inner > 0.25)
        {
            builder.ArcTo(Num.Oval(geometry.PivotX, geometry.PivotY, inner), a1, -sweep, false);
        }
        else
        {
            builder.LineTo((float)geometry.PivotX, (float)geometry.PivotY);
        }

        builder.Close();
        return builder.Detach();
    }

    private static void DrawZone(SKCanvas canvas, NeedleGeometry geometry, ScaleZone zone, SKColor color)
    {
        var from = Num.Clamp01(zone.From);
        var to = Num.Clamp01(zone.To);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var inner = double.IsFinite(zone.InnerRadius) ? Math.Max(0, zone.InnerRadius) : 138;
        var outer = double.IsFinite(zone.OuterRadius) ? Math.Max(0, zone.OuterRadius) : 152;
        if (outer < inner)
        {
            (inner, outer) = (outer, inner);
        }

        if (to - from < 1e-4 || outer - inner < 0.5)
        {
            return;
        }

        using var path = SectorPath(geometry, from, to, inner, outer);
        switch (zone.Style)
        {
            case ZoneStyle.Band:
            {
                using var paint = Paints.Fill(color);
                canvas.DrawPath(path, paint);
                break;
            }

            case ZoneStyle.Outline:
            {
                using var paint = Paints.Hairline(color);
                canvas.DrawPath(path, paint);
                break;
            }

            case ZoneStyle.Hatched:
            {
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, false);
                Patterns.Hatch(canvas, path.Bounds, HatchPitch, color);
                canvas.Restore();
                using var paint = Paints.Hairline(color);
                canvas.DrawPath(path, paint);
                break;
            }

            case ZoneStyle.Dotted:
            {
                // A band reads as a 25 % dot screen; a large area (≥ 16 px deep) becomes a quiet 4 px grid texture.
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, false);
                if (outer - inner >= LargeZone)
                {
                    Patterns.Grid(canvas, path.Bounds, color, 4);
                }
                else
                {
                    Patterns.Dots(canvas, path.Bounds, color);
                }

                canvas.Restore();
                break;
            }
        }
    }

    private static void DrawArc(SKCanvas canvas, NeedleGeometry geometry, double radius, float width, SKColor color)
    {
        if (radius <= 0 || width <= 0)
        {
            return;
        }

        var a0 = (float)(geometry.AngleAt(0) - 90);
        var sweep = (float)(geometry.AngleAt(1) - geometry.AngleAt(0));
        using var builder = new SKPathBuilder();
        builder.ArcTo(Num.Oval(geometry.PivotX, geometry.PivotY, radius), a0, sweep, true);
        using var path = builder.Detach();
        using var paint = Paints.Stroke(color, width);
        canvas.DrawPath(path, paint);
    }

    private static void DrawTick(SKCanvas canvas, NeedleGeometry geometry, double fraction, float outer, float length, float width, TickStyle style, float dotRadius, SKColor color)
    {
        if (width <= 0)
        {
            return;
        }

        var theta = geometry.AngleAt(fraction) * Math.PI / 180;
        switch (style)
        {
            case TickStyle.Dot:
            {
                var (cx, cy) = geometry.PointAt(fraction, dotRadius);
                Patterns.Dot(canvas, (float)cx, (float)cy, width, color);
                break;
            }

            case TickStyle.Wedge:
            {
                if (length <= 0)
                {
                    return;
                }

                var (ox, oy) = geometry.PointAt(fraction, outer);
                var (ix, iy) = geometry.PointAt(fraction, outer - length);
                var tx = (float)Math.Cos(theta) * width / 2;
                var ty = (float)Math.Sin(theta) * width / 2;
                using var builder = new SKPathBuilder();
                builder.MoveTo((float)ox - tx, (float)oy - ty);
                builder.LineTo((float)ox + tx, (float)oy + ty);
                builder.LineTo((float)ix, (float)iy);
                builder.Close();
                using var path = builder.Detach();
                using var paint = Paints.Fill(color);
                canvas.DrawPath(path, paint);
                break;
            }

            default:
            {
                if (length <= 0)
                {
                    return;
                }

                var (x1, y1) = geometry.PointAt(fraction, outer - length);
                var (x2, y2) = geometry.PointAt(fraction, outer);
                using var paint = Paints.Stroke(color, width);
                canvas.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, paint);
                break;
            }
        }
    }

    private static void DrawLabels(FaceCanvas face, ScaleLayer layer, List<ScaleMark> majors, SKColor color)
    {
        var geometry = face.Geometry;
        var fonts = face.Assets.Fonts;
        var size = Num.Clamp(layer.LabelSize, 1, 200, 14);
        var radius = Num.Clamp(layer.LabelRadius, 0, 1000, 124);
        var smooth = layer.RotateLabels || size >= 18;
        var style = new TextStyle(layer.LabelFont, size, layer.LabelWeight, false, smooth);
        var useMinus = layer.CustomLabels is not { Count: > 0 };

        var labels = new List<Label>();
        try
        {
            foreach (var mark in majors)
            {
                if (string.IsNullOrWhiteSpace(mark.Label))
                {
                    continue;
                }

                // A true minus sign reads better than a hyphen; fonts without one fall back to '-'.
                var text = useMinus && mark.Label.StartsWith('-') ? "−" + mark.Label[1..] : mark.Label;
                var shaped = TextShaper.Shape(fonts, text, style);
                if (shaped.IsEmpty)
                {
                    shaped.Dispose();
                    continue;
                }

                var (cx, cy) = geometry.PointAt(mark.Fraction, radius);
                var angle = layer.RotateLabels ? (float)geometry.AngleAt(mark.Fraction) : 0f;
                var box = new LabelBox((float)cx, (float)cy, (shaped.Width / 2) + LabelClearance, (shaped.CapHeight / 2) + LabelClearance, angle);
                labels.Add(new Label(shaped, KeepInside(box)));
            }

            foreach (var label in Select(labels))
            {
                DrawLabel(face.Canvas, label, color);
            }
        }
        finally
        {
            foreach (var label in labels)
            {
                label.Text.Dispose();
            }
        }
    }

    /// <summary>
    /// Thins colliding labels evenly: every k-th label for the smallest k without collisions, always keeping
    /// both end labels (a pick next to the last label is dropped if they collide).
    /// </summary>
    internal static List<int> SelectLabels(int count, Func<int, int, bool> overlaps)
    {
        if (count <= 1)
        {
            return count == 1 ? [0] : [];
        }

        for (var stride = 1; stride < count; stride++)
        {
            var picks = new List<int>();
            for (var i = 0; i < count; i += stride)
            {
                picks.Add(i);
            }

            if (picks[^1] != count - 1)
            {
                while (picks.Count > 1 && overlaps(picks[^1], count - 1))
                {
                    picks.RemoveAt(picks.Count - 1);
                }

                picks.Add(count - 1);
            }

            var clear = true;
            for (var a = 0; a < picks.Count && clear; a++)
            {
                for (var b = a + 1; b < picks.Count; b++)
                {
                    if (overlaps(picks[a], picks[b]))
                    {
                        clear = false;
                        break;
                    }
                }
            }

            if (clear)
            {
                return picks;
            }
        }

        return [0];
    }

    private static List<Label> Select(List<Label> labels) =>
        SelectLabels(labels.Count, (a, b) => labels[a].Box.Overlaps(labels[b].Box)).ConvertAll(i => labels[i]);

    private static void DrawLabel(SKCanvas canvas, Label label, SKColor color)
    {
        var text = label.Text;
        var box = label.Box;
        using var paint = Paints.Text(color, text.Smooth);
        if (box.Angle == 0)
        {
            text.Draw(canvas, Num.Snap(box.Cx - (text.Width / 2)), Num.Snap(box.Cy + (text.CapHeight / 2)), paint);
            return;
        }

        canvas.Save();
        canvas.RotateDegrees(box.Angle, box.Cx, box.Cy);
        text.Draw(canvas, box.Cx - (text.Width / 2), box.Cy + (text.CapHeight / 2), paint);
        canvas.Restore();
    }

    /// <summary>Shifts a label box so its (rotated) extent stays inside the canvas margins.</summary>
    private static LabelBox KeepInside(LabelBox box)
    {
        var (hx, hy) = box.Extent();
        var dx = 0f;
        var dy = 0f;
        var minX = CanvasMargin - LabelClearance;
        var minY = CanvasMargin - LabelClearance;
        var maxX = FaceCanvas.Width - CanvasMargin + LabelClearance;
        var maxY = FaceCanvas.Height - CanvasMargin + LabelClearance;
        if (box.Cx - hx < minX)
        {
            dx = minX - (box.Cx - hx);
        }
        else if (box.Cx + hx > maxX)
        {
            dx = maxX - (box.Cx + hx);
        }

        if (box.Cy - hy < minY)
        {
            dy = minY - (box.Cy - hy);
        }
        else if (box.Cy + hy > maxY)
        {
            dy = maxY - (box.Cy + hy);
        }

        return box with { Cx = box.Cx + dx, Cy = box.Cy + dy };
    }

    private sealed record Label(ShapedText Text, LabelBox Box);

    /// <summary>An oriented rectangle: centre, half extents and rotation in degrees.</summary>
    private readonly record struct LabelBox(float Cx, float Cy, float HalfWidth, float HalfHeight, float Angle)
    {
        /// <summary>Half size of the axis-aligned bounds.</summary>
        public (float X, float Y) Extent()
        {
            var r = Angle * MathF.PI / 180;
            var c = MathF.Abs(MathF.Cos(r));
            var s = MathF.Abs(MathF.Sin(r));
            return ((HalfWidth * c) + (HalfHeight * s), (HalfWidth * s) + (HalfHeight * c));
        }

        /// <summary>Separating-axis test between two oriented rectangles.</summary>
        public bool Overlaps(in LabelBox other)
        {
            Span<(float X, float Y)> axes = stackalloc (float, float)[4];
            var a = Angle * MathF.PI / 180;
            var b = other.Angle * MathF.PI / 180;
            axes[0] = (MathF.Cos(a), MathF.Sin(a));
            axes[1] = (-MathF.Sin(a), MathF.Cos(a));
            axes[2] = (MathF.Cos(b), MathF.Sin(b));
            axes[3] = (-MathF.Sin(b), MathF.Cos(b));
            var dx = other.Cx - Cx;
            var dy = other.Cy - Cy;
            foreach (var (x, y) in axes)
            {
                var distance = MathF.Abs((dx * x) + (dy * y));
                if (distance >= Radius(this, x, y) + Radius(other, x, y))
                {
                    return false;
                }
            }

            return true;

            static float Radius(in LabelBox box, float x, float y)
            {
                var r = box.Angle * MathF.PI / 180;
                var (ux, uy) = (MathF.Cos(r), MathF.Sin(r));
                var (vx, vy) = (-uy, ux);
                return (box.HalfWidth * MathF.Abs((ux * x) + (uy * y))) + (box.HalfHeight * MathF.Abs((vx * x) + (vy * y)));
            }
        }
    }
}
