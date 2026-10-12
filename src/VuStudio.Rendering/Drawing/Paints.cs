using SkiaSharp;

namespace VuStudio.Rendering.Drawing;

/// <summary>
/// Paint factory for the one-bit look.
/// <list type="bullet">
/// <item>Shapes are rasterised aliased (a pixel is ink when its centre is inside the shape). A stroke of
/// integer width w then covers exactly w pixels across, wherever it falls on the grid; thresholding
/// anti-aliased coverage instead makes curves alternate between w and w + 1 pixels ("lumpy" arcs).</item>
/// <item>Strokes thinner than <see cref="HairlineLimit"/> become 1 px hairlines, which never break up.</item>
/// <item>Smooth text and icons are anti-aliased and cut at ~45 % coverage: curves stay smooth, thin features
/// survive, and no pixel lands on the ambiguous 50 % mark.</item>
/// </list>
/// </summary>
internal static class Paints
{
    public const float HairlineLimit = 1.15f;

    private static readonly SKMaskFilter Coverage = SKMaskFilter.CreateClip(114, 115);

    /// <summary>Aliased fill (pixel centres inside the shape).</summary>
    public static SKPaint Fill(SKColor color) => new()
    {
        Color = color,
        IsAntialias = false,
        Style = SKPaintStyle.Fill,
    };

    /// <summary>Anti-aliased fill cut at the coverage threshold; for detailed artwork such as icons.</summary>
    public static SKPaint SmoothFill(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        MaskFilter = Coverage,
    };

    public static SKPaint Stroke(SKColor color, float width, SKStrokeCap cap = SKStrokeCap.Butt)
    {
        if (!(width >= HairlineLimit))
        {
            return Hairline(color);
        }

        return new SKPaint
        {
            Color = color,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = cap,
            StrokeJoin = SKStrokeJoin.Miter,
        };
    }

    /// <summary>A crisp, unbroken 1 px line.</summary>
    public static SKPaint Hairline(SKColor color) => new()
    {
        Color = color,
        IsAntialias = false,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 0,
    };

    public static SKPaint Text(SKColor color, bool smooth) => smooth
        ? new SKPaint { Color = color, IsAntialias = true, MaskFilter = Coverage }
        : new SKPaint { Color = color, IsAntialias = false };
}
