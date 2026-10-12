using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Drawing;

/// <summary>Draws <see cref="SparklineLayer"/>s from <see cref="FaceRenderContext.History"/>.</summary>
internal static class SparklineDrawer
{
    /// <summary>Auto-ranged data gets this fraction of its span as headroom above and below.</summary>
    private const double AutoMargin = 0.08;

    public static void Draw(FaceCanvas face, SparklineLayer layer)
    {
        var context = face.Context;
        var sensorId = string.IsNullOrWhiteSpace(layer.SensorId) ? context.SensorId : layer.SensorId.Trim();
        if (string.IsNullOrWhiteSpace(sensorId))
        {
            return;
        }

        var window = TimeSpan.FromSeconds(Math.Clamp(layer.WindowSeconds, 1, 31 * 24 * 3600));
        var points = (context.History(sensorId, window) ?? [])
            .Where(p => double.IsFinite(p.Value))
            .OrderBy(p => p.Time)
            .ToList();
        if (points.Count < 2)
        {
            return;
        }

        var rect = Num.PixelRect(layer.X, layer.Y, layer.Width, layer.Height);
        if (rect.Width < 2 || rect.Height < 2)
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
            if (inner.Width < 2 || inner.Height < 2)
            {
                return;
            }
        }

        var (low, high) = Range(points, layer);
        var stroke = Num.Clamp(layer.StrokeWidth, 1, 8, 1.5);
        var t0 = points[0].Time;
        var span = (points[^1].Time - t0).TotalSeconds;

        canvas.Save();
        canvas.ClipRect(new SKRect(inner.Left, inner.Top, inner.Right, inner.Bottom), SKClipOperation.Intersect, false);
        if (layer.Style == SparklineStyle.Bars)
        {
            DrawBars(canvas, inner, points, low, high, color);
            canvas.Restore();
            return;
        }

        // Keep the stroke inside the plot area.
        var inset = stroke >= Paints.HairlineLimit ? stroke / 2 : 0.5f;
        var left = inner.Left + inset;
        var right = inner.Right - inset;
        var top = inner.Top + inset;
        var bottom = inner.Bottom - inset;
        using var builder = new SKPathBuilder();
        for (var i = 0; i < points.Count; i++)
        {
            var tx = span > 0 ? (points[i].Time - t0).TotalSeconds / span : (double)i / (points.Count - 1);
            var x = left + (float)(tx * (right - left));
            var y = bottom - (float)((points[i].Value - low) / (high - low) * (bottom - top));
            if (i == 0)
            {
                builder.MoveTo(x, y);
            }
            else
            {
                builder.LineTo(x, y);
            }
        }

        using var line = builder.Snapshot();
        if (layer.Style == SparklineStyle.Area)
        {
            builder.LineTo(right, inner.Bottom);
            builder.LineTo(left, inner.Bottom);
            builder.Close();
            using var area = builder.Detach();
            canvas.Save();
            canvas.ClipPath(area, SKClipOperation.Intersect, false);
            Patterns.Checker(canvas, area.Bounds, color);
            canvas.Restore();
        }

        using var paint = Paints.Stroke(color, stroke, SKStrokeCap.Round);
        paint.StrokeJoin = SKStrokeJoin.Round;
        canvas.DrawPath(line, paint);
        canvas.Restore();
    }

    private static (double Low, double High) Range(List<HistoryPoint> points, SparklineLayer layer)
    {
        var low = points.Min(p => p.Value);
        var high = points.Max(p => p.Value);
        var fixedLow = layer.Min is { } mn && double.IsFinite(mn);
        var fixedHigh = layer.Max is { } mx && double.IsFinite(mx);
        if (!fixedLow || !fixedHigh)
        {
            var spread = high - low;
            var margin = spread > 0 ? spread * AutoMargin : Math.Max(Math.Abs(high) * 0.1, 1);
            low -= margin;
            high += margin;
        }

        if (fixedLow)
        {
            low = layer.Min!.Value;
        }

        if (fixedHigh)
        {
            high = layer.Max!.Value;
        }

        if (high <= low)
        {
            high = low + 1;
        }

        return (low, high);
    }

    /// <summary>2 px bars with 1 px gaps; each bar is the mean of the samples in its time slice.</summary>
    private static void DrawBars(SKCanvas canvas, SKRectI inner, List<HistoryPoint> points, double low, double high, SKColor color)
    {
        const int barWidth = 2;
        const int pitch = 3;
        var count = Math.Max(1, (inner.Width + 1) / pitch);
        var offset = (inner.Width - ((count * pitch) - 1)) / 2;
        var t0 = points[0].Time;
        var span = Math.Max(1e-9, (points[^1].Time - t0).TotalSeconds);
        var sums = new double[count];
        var counts = new int[count];
        for (var i = 0; i < points.Count; i++)
        {
            var slot = Math.Min(count - 1, (int)((points[i].Time - t0).TotalSeconds / span * count));
            sums[slot] += points[i].Value;
            counts[slot]++;
        }

        using var paint = Paints.Fill(color);
        var last = points[0].Value;
        for (var i = 0; i < count; i++)
        {
            var value = counts[i] > 0 ? sums[i] / counts[i] : last;
            last = value;
            var height = Math.Max(1, (int)Math.Round((value - low) / (high - low) * inner.Height));
            var x = inner.Left + offset + (i * pitch);
            canvas.DrawRect(x, inner.Bottom - height, barWidth, height, paint);
        }
    }
}
