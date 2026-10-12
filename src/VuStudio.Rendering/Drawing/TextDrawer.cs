using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Rendering.Text;

namespace VuStudio.Rendering.Drawing;

/// <summary>Draws <see cref="TextLayer"/>s: templates, alignment, baselines, overflow handling and rotation.</summary>
internal static class TextDrawer
{
    /// <summary>Smallest size <see cref="TextOverflow.Shrink"/> goes down to before it ellipsizes.</summary>
    public const float MinShrinkSize = 6;

    public static void Draw(FaceCanvas face, TextLayer layer)
    {
        var context = face.Context;
        var text = TextTemplate.Render(layer.Text, context);
        if (layer.Uppercase)
        {
            text = text.ToUpper(context.Culture);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var style = new TextStyle(
            layer.Font,
            Num.Clamp(layer.Size, 1, 400, 18),
            layer.Weight,
            layer.Italic,
            layer.Smooth,
            Num.Clamp(layer.LetterSpacing, -20, 50, 0));
        var maxWidth = layer.MaxWidth is { } mw && double.IsFinite(mw) && mw > 0 ? (float)mw : (float?)null;

        using var shaped = Shape(face.Assets.Fonts, text, style, maxWidth, layer.Overflow, out var clip);
        if (shaped.IsEmpty)
        {
            return;
        }

        var x = Num.F(layer.X);
        var y = Num.F(layer.Y);
        var left = layer.Align switch
        {
            TextAlign.Left => x,
            TextAlign.Right => x - shaped.Width,
            _ => x - (shaped.Width / 2),
        };
        var baseline = Baseline(shaped, layer.Baseline, y);

        var canvas = face.Canvas;
        canvas.Save();
        var rotation = Num.F(layer.Rotation);
        if (rotation != 0)
        {
            canvas.RotateDegrees(rotation, x, y);
        }

        if (clip && maxWidth is { } limit)
        {
            var clipLeft = layer.Align switch
            {
                TextAlign.Left => x,
                TextAlign.Right => x - limit,
                _ => x - (limit / 2),
            };
            canvas.ClipRect(new SKRect(clipLeft, -1000, clipLeft + limit, 1000), SKClipOperation.Intersect, false);
        }

        using var paint = Paints.Text(face.Color(layer.Ink), layer.Smooth);
        shaped.Draw(canvas, Num.Snap(left), Num.Snap(baseline), paint);
        canvas.Restore();
    }

    /// <summary>Baseline position for an anchor at <paramref name="y"/>: Top/Middle use the cap height.</summary>
    public static float Baseline(ShapedText text, TextBaseline baseline, float y) => baseline switch
    {
        TextBaseline.Top => y + text.CapHeight,
        TextBaseline.Baseline => y,
        TextBaseline.Bottom => y - text.Descent,
        _ => y + (text.CapHeight / 2),
    };

    private static ShapedText Shape(FontLibrary fonts, string text, in TextStyle style, float? maxWidth, TextOverflow overflow, out bool clip)
    {
        clip = false;
        var shaped = TextShaper.Shape(fonts, text, style);
        if (maxWidth is not { } limit || shaped.Width <= limit)
        {
            return shaped;
        }

        switch (overflow)
        {
            case TextOverflow.Clip:
                clip = true;
                return shaped;
            case TextOverflow.Ellipsis:
                shaped.Dispose();
                return TextShaper.ShapeEllipsized(fonts, text, style, limit);
            case TextOverflow.Shrink:
                shaped.Dispose();
                return TextShaper.ShapeToFit(fonts, text, style, limit, Math.Min(MinShrinkSize, style.Size));
            default:
                return shaped;
        }
    }
}
