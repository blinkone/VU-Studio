using SkiaSharp;

namespace VuStudio.Rendering.Drawing;

/// <summary>
/// One-bit fill patterns. They are aligned to the canvas grid (not to the shape), so neighbouring areas
/// with the same pattern join seamlessly.
/// </summary>
internal static class Patterns
{
    /// <summary>45° hairlines (bottom-left to top-right) every <paramref name="pitch"/> pixels inside the current clip.</summary>
    public static void Hatch(SKCanvas canvas, SKRect bounds, int pitch, SKColor color)
    {
        pitch = Math.Max(2, pitch);
        using var paint = Paints.Hairline(color);
        var top = MathF.Floor(bounds.Top) - 1;
        var bottom = MathF.Ceiling(bounds.Bottom) + 1;
        var start = (int)MathF.Floor(bounds.Left + top);
        start -= Mod(start, pitch);
        var end = (int)MathF.Ceiling(bounds.Right + bottom);

        // Pixel centres (i + .5, j + .5) lie on x + y = c for integer c, so the lines hit whole pixels.
        for (var c = start; c <= end; c += pitch)
        {
            canvas.DrawLine(c - top, top, c - bottom, bottom, paint);
        }
    }

    /// <summary>A staggered screen of single-pixel dots (25 % ink) inside the current clip.</summary>
    public static void Dots(SKCanvas canvas, SKRect bounds, SKColor color) => DotScreen(canvas, bounds, color, 2, staggered: true);

    /// <summary>A square grid of single-pixel dots every <paramref name="pitch"/> pixels inside the current clip.</summary>
    public static void Grid(SKCanvas canvas, SKRect bounds, SKColor color, int pitch) => DotScreen(canvas, bounds, color, Math.Max(2, pitch), staggered: false);

    private static void DotScreen(SKCanvas canvas, SKRect bounds, SKColor color, int pitch, bool staggered)
    {
        var points = new List<SKPoint>();
        var x0 = (int)MathF.Floor(bounds.Left);
        var x1 = (int)MathF.Ceiling(bounds.Right);
        var y0 = (int)MathF.Floor(bounds.Top);
        var y1 = (int)MathF.Ceiling(bounds.Bottom);
        for (var y = y0; y <= y1; y++)
        {
            if (Mod(y, pitch) != 0)
            {
                continue;
            }

            // Staggered rows shift every other row by half a pitch.
            var phase = staggered && Mod(y, 2 * pitch) != 0 ? pitch / 2 : 0;
            for (var x = x0; x <= x1; x++)
            {
                if (Mod(x, pitch) == phase)
                {
                    points.Add(new SKPoint(x + 0.5f, y + 0.5f));
                }
            }
        }

        DrawPixels(canvas, points, color);
    }

    /// <summary>A 50 % checkerboard inside the current clip (used to tint areas).</summary>
    public static void Checker(SKCanvas canvas, SKRect bounds, SKColor color)
    {
        var points = new List<SKPoint>();
        for (var y = (int)MathF.Floor(bounds.Top); y <= (int)MathF.Ceiling(bounds.Bottom); y++)
        {
            for (var x = (int)MathF.Floor(bounds.Left); x <= (int)MathF.Ceiling(bounds.Right); x++)
            {
                if (Mod(x + y, 2) == 0)
                {
                    points.Add(new SKPoint(x + 0.5f, y + 0.5f));
                }
            }
        }

        DrawPixels(canvas, points, color);
    }

    /// <summary>
    /// A round dot of diameter <paramref name="diameter"/> that stays a dot at one bit: one pixel below
    /// 1.75 px, a 2 × 2 block below 2.75 px, a disc above.
    /// </summary>
    public static void Dot(SKCanvas canvas, float cx, float cy, float diameter, SKColor color)
    {
        if (diameter < 1.75f)
        {
            using var paint = Paints.Fill(color);
            canvas.DrawRect(MathF.Floor(cx), MathF.Floor(cy), 1, 1, paint);
        }
        else if (diameter < 2.75f)
        {
            using var paint = Paints.Fill(color);
            canvas.DrawRect(Num.Snap(cx) - 1, Num.Snap(cy) - 1, 2, 2, paint);
        }
        else
        {
            using var paint = Paints.Fill(color);
            canvas.DrawCircle(cx, cy, diameter / 2, paint);
        }
    }

    public static void DrawPixels(SKCanvas canvas, List<SKPoint> points, SKColor color)
    {
        if (points.Count == 0)
        {
            return;
        }

        using var paint = Paints.Hairline(color);
        canvas.DrawPoints(SKPointMode.Points, [.. points], paint);
    }

    public static int Mod(int value, int m) => ((value % m) + m) % m;
}
