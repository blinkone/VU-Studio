using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Drawing;

/// <summary>
/// Draws <see cref="ShapeLayer"/>s. Axis-aligned rectangles and lines are snapped so their strokes cover
/// whole pixels (crisp at one bit); everything else is anti-aliased through the coverage threshold.
/// </summary>
internal static class ShapeDrawer
{
    public static void Draw(FaceCanvas face, ShapeLayer layer)
    {
        var canvas = face.Canvas;
        var color = face.Color(layer.Ink);
        var strokeWidth = Num.Clamp(layer.StrokeWidth, 0, 100, 1.5);
        var stroke = layer.Stroke && strokeWidth > 0;
        if (!stroke && !layer.Fill)
        {
            return;
        }

        switch (layer.Kind)
        {
            case ShapeKind.Rectangle:
                DrawRectangle(canvas, layer, color, stroke, strokeWidth);
                break;
            case ShapeKind.Ellipse:
            {
                var oval = Bounds(layer);
                if (layer.Fill)
                {
                    using var fill = Paints.Fill(color);
                    canvas.DrawOval(oval, fill);
                }

                if (stroke)
                {
                    using var paint = StrokePaint(color, strokeWidth, layer.Dashed, SKStrokeCap.Butt);
                    canvas.DrawOval(oval, paint);
                }

                break;
            }

            case ShapeKind.Line:
                if (stroke || layer.Fill)
                {
                    DrawLine(canvas, layer, color, stroke ? strokeWidth : 1);
                }

                break;
            case ShapeKind.Arc:
            {
                var oval = Bounds(layer);
                var start = Num.F(layer.StartAngle);
                var sweep = Math.Clamp(Num.F(layer.SweepAngle, 90), -360f, 360f);
                if (layer.Fill)
                {
                    using var fill = Paints.Fill(color);
                    canvas.DrawArc(oval, start, sweep, true, fill);
                }

                if (stroke)
                {
                    using var paint = StrokePaint(color, strokeWidth, layer.Dashed, SKStrokeCap.Butt);
                    canvas.DrawArc(oval, start, sweep, false, paint);
                }

                break;
            }
        }
    }

    private static void DrawRectangle(SKCanvas canvas, ShapeLayer layer, SKColor color, bool stroke, float strokeWidth)
    {
        var rect = Num.PixelRect(layer.X, layer.Y, layer.Width, layer.Height);
        var radius = Math.Clamp(Num.F(layer.CornerRadius), 0, Math.Min(rect.Width, rect.Height) / 2f);
        if (layer.Fill && rect.Width > 0 && rect.Height > 0)
        {
            if (radius > 0)
            {
                using var fill = Paints.Fill(color);
                canvas.DrawRoundRect(rect, radius, radius, fill);
            }
            else
            {
                using var fill = Paints.Fill(color);
                canvas.DrawRect(rect, fill);
            }
        }

        if (!stroke)
        {
            return;
        }

        // Centre the stroke on the edges, snapped so an integer-wide stroke covers whole pixels.
        var width = Num.PixelWidth(strokeWidth);
        var edges = new SKRect(
            Num.SnapStroke(rect.Left, width),
            Num.SnapStroke(rect.Top, width),
            Num.SnapStroke(rect.Right, width),
            Num.SnapStroke(rect.Bottom, width));
        using var paint = StrokePaint(color, width, layer.Dashed, SKStrokeCap.Square);
        if (width == 1 && radius == 0 && !layer.Dashed)
        {
            // An aliased hairline rectangle: exactly one pixel all round.
            canvas.DrawRect(edges.Left, edges.Top, edges.Width, edges.Height, paint);
        }
        else if (radius > 0)
        {
            canvas.DrawRoundRect(edges, radius, radius, paint);
        }
        else
        {
            canvas.DrawRect(edges, paint);
        }
    }

    private static void DrawLine(SKCanvas canvas, ShapeLayer layer, SKColor color, float strokeWidth)
    {
        var x1 = Num.F(layer.X);
        var y1 = Num.F(layer.Y);
        var x2 = Num.F(layer.X2);
        var y2 = Num.F(layer.Y2);
        var horizontal = MathF.Abs(y2 - y1) < 0.01f;
        var vertical = MathF.Abs(x2 - x1) < 0.01f;
        if (horizontal || vertical)
        {
            // Snap the line onto the pixel grid and extend it so it covers both end pixels.
            var width = Num.PixelWidth(strokeWidth);
            if (horizontal)
            {
                y1 = y2 = Num.SnapStroke(y1, width);
                (x1, x2) = (MathF.Min(x1, x2), MathF.Max(x1, x2));
                x1 = Num.Snap(x1);
                x2 = Num.Snap(x2);
            }
            else
            {
                x1 = x2 = Num.SnapStroke(x1, width);
                (y1, y2) = (MathF.Min(y1, y2), MathF.Max(y1, y2));
                y1 = Num.Snap(y1);
                y2 = Num.Snap(y2);
            }

            strokeWidth = width;
        }

        using var paint = StrokePaint(color, strokeWidth, layer.Dashed, SKStrokeCap.Butt);
        canvas.DrawLine(x1, y1, x2, y2, paint);
    }

    private static SKPaint StrokePaint(SKColor color, float width, bool dashed, SKStrokeCap cap)
    {
        var paint = Paints.Stroke(color, width, cap);
        if (dashed)
        {
            // Dashes in whole pixels: 2 on / 2 off for hairlines, 3w on / 2w off otherwise.
            var w = Math.Max(1f, MathF.Round(width));
            float on = w <= 1 ? 2 : 3 * w;
            float off = w <= 1 ? 2 : 2 * w;
            if (cap != SKStrokeCap.Butt && w > 1)
            {
                // Square caps grow each dash by w; keep the visible rhythm.
                on -= w;
                off += w;
            }

            paint.PathEffect = SKPathEffect.CreateDash([on, off], 0);
        }

        return paint;
    }

    private static SKRect Bounds(ShapeLayer layer)
    {
        var x0 = Num.F(layer.X);
        var y0 = Num.F(layer.Y);
        var x1 = x0 + Num.F(layer.Width);
        var y1 = y0 + Num.F(layer.Height);
        return new SKRect(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));
    }
}
