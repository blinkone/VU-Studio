using System.Text;
using SkiaSharp;

namespace VuStudio.Rendering.Text;

/// <summary>How a line of text is set.</summary>
/// <param name="Font">Font id from the bundled set; unknown ids fall back to Inter.</param>
/// <param name="Size">Font size in pixels.</param>
/// <param name="Smooth">Anti-alias and threshold (true) or draw aliased with full hinting (false).</param>
/// <param name="LetterSpacing">Extra pixels between glyphs (not after the last one).</param>
internal readonly record struct TextStyle(
    string? Font,
    float Size,
    int Weight = 400,
    bool Italic = false,
    bool Smooth = true,
    float LetterSpacing = 0);

/// <summary>A single line of text converted to positioned glyphs, ready to measure and draw.</summary>
internal sealed class ShapedText : IDisposable
{
    private readonly List<GlyphRun> _runs;
    private readonly List<SKFont> _fonts;
    private SKTextBlob? _blob;

    internal ShapedText(List<GlyphRun> runs, List<SKFont> fonts, float width, SKRect inkBounds, SKFontMetrics metrics, in TextStyle style)
    {
        _runs = runs;
        _fonts = fonts;
        Width = width;
        InkBounds = inkBounds;
        Size = style.Size;
        Smooth = style.Smooth;
        Ascent = metrics.Ascent;
        Descent = metrics.Descent;
        CapHeight = metrics.CapHeight > 0 ? metrics.CapHeight : -metrics.Ascent * 0.7f;
    }

    /// <summary>Advance width in pixels, letter spacing included.</summary>
    public float Width { get; }

    /// <summary>Union of the glyph outlines relative to the origin (left edge, baseline).</summary>
    public SKRect InkBounds { get; }

    /// <summary>Height of capitals and lining figures above the baseline.</summary>
    public float CapHeight { get; }

    /// <summary>Distance above the baseline reserved by the font (negative).</summary>
    public float Ascent { get; }

    /// <summary>Distance below the baseline reserved by the font (positive).</summary>
    public float Descent { get; }

    public float Size { get; }

    public bool Smooth { get; }

    public bool IsEmpty => _runs.Count == 0;

    public void Draw(SKCanvas canvas, float x, float baseline, SKPaint paint)
    {
        if (IsEmpty)
        {
            return;
        }

        _blob ??= BuildBlob();
        if (_blob is not null)
        {
            canvas.DrawText(_blob, x, baseline, paint);
        }
    }

    public void Dispose()
    {
        _blob?.Dispose();
        foreach (var font in _fonts)
        {
            font.Dispose();
        }
    }

    private SKTextBlob? BuildBlob()
    {
        using var builder = new SKTextBlobBuilder();
        foreach (var run in _runs)
        {
            builder.AddHorizontalRun(run.Glyphs, run.Font, run.Positions, 0);
        }

        return builder.Build();
    }

    internal sealed record GlyphRun(SKFont Font, ushort[] Glyphs, float[] Positions);
}

/// <summary>
/// Turns strings into <see cref="ShapedText"/>. There is no complex shaping (Skia's simple text path does
/// none either); characters a font lacks are first replaced by close equivalents in the same font
/// (− → -, … → ...), then taken from Inter, and dropped when no bundled font has them.
/// </summary>
internal static class TextShaper
{
    public const string Ellipsis = "…";

    public static ShapedText Shape(FontLibrary fonts, string text, in TextStyle style)
    {
        var size = Math.Clamp(float.IsFinite(style.Size) ? style.Size : 12, 1, 1000);
        var resolved = fonts.Resolve(style.Font, style.Weight, style.Italic, size);
        var primary = CreateFont(resolved, size, style.Smooth);
        var created = new List<SKFont> { primary };
        SKFont? fallback = null;

        var items = new List<(SKFont Font, ushort Glyph)>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp is '\n' or '\r' or '\t')
            {
                cp = ' ';
            }
            else if (cp < 0x20 || cp == 0x7F || (cp >= 0x80 && cp < 0xA0))
            {
                continue;
            }

            if (TryAdd(primary, cp, items))
            {
                continue;
            }

            if (Substitute(cp) is { } substitute && substitute.All(c => primary.GetGlyph(c) != 0))
            {
                foreach (var c in substitute)
                {
                    TryAdd(primary, c, items);
                }

                continue;
            }

            if (fallback is null && !resolved.Family.Id.Equals(FontLibrary.DefaultFontId, StringComparison.OrdinalIgnoreCase))
            {
                fallback = CreateFont(fonts.Resolve(FontLibrary.DefaultFontId, style.Weight, style.Italic, size), size, style.Smooth);
                created.Add(fallback);
            }

            if (fallback is not null)
            {
                TryAdd(fallback, cp, items);
            }
        }

        primary.GetFontMetrics(out var metrics);
        var spacing = float.IsFinite(style.LetterSpacing) ? style.LetterSpacing : 0;
        var runs = new List<ShapedText.GlyphRun>();
        var x = 0f;
        var ink = SKRect.Empty;
        var hasInk = false;
        for (var i = 0; i < items.Count;)
        {
            var font = items[i].Font;
            var j = i;
            while (j < items.Count && ReferenceEquals(items[j].Font, font))
            {
                j++;
            }

            var glyphs = new ushort[j - i];
            for (var k = 0; k < glyphs.Length; k++)
            {
                glyphs[k] = items[i + k].Glyph;
            }

            var widths = new float[glyphs.Length];
            var bounds = new SKRect[glyphs.Length];
            font.GetGlyphWidths(glyphs, widths, bounds, null);
            var positions = new float[glyphs.Length];
            for (var k = 0; k < glyphs.Length; k++)
            {
                positions[k] = x;
                var b = bounds[k];
                if (b.Width > 0 && b.Height > 0)
                {
                    b.Offset(x, 0);
                    ink = hasInk ? SKRect.Union(ink, b) : b;
                    hasInk = true;
                }

                x += widths[k] + spacing;
            }

            runs.Add(new ShapedText.GlyphRun(font, glyphs, positions));
            i = j;
        }

        var width = items.Count > 0 ? Math.Max(0, x - spacing) : 0;
        return new ShapedText(runs, created, width, ink, metrics, style with { Size = size });
    }

    /// <summary>Shapes <paramref name="text"/>, truncated with an ellipsis so it fits <paramref name="maxWidth"/>.</summary>
    public static ShapedText ShapeEllipsized(FontLibrary fonts, string text, in TextStyle style, float maxWidth)
    {
        var full = Shape(fonts, text, style);
        if (full.Width <= maxWidth)
        {
            return full;
        }

        full.Dispose();
        var runes = text.EnumerateRunes().ToArray();
        int lo = 0, hi = runes.Length - 1;
        ShapedText? best = null;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var candidate = Shape(fonts, Prefix(runes, mid) + Ellipsis, style);
            if (candidate.Width <= maxWidth)
            {
                best?.Dispose();
                best = candidate;
                lo = mid + 1;
            }
            else
            {
                candidate.Dispose();
                hi = mid - 1;
            }
        }

        return best ?? Shape(fonts, Ellipsis, style);

        static string Prefix(Rune[] runes, int count)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                sb.Append(runes[i].ToString());
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Shapes <paramref name="text"/> at the largest size (not below <paramref name="minSize"/>) that fits
    /// <paramref name="maxWidth"/>; pixel fonts only use whole multiples of their grid. Ellipsizes when even
    /// the smallest size is too wide.
    /// </summary>
    public static ShapedText ShapeToFit(FontLibrary fonts, string text, in TextStyle style, float maxWidth, float minSize)
    {
        var shaped = Shape(fonts, text, style);
        if (shaped.Width <= maxWidth)
        {
            return shaped;
        }

        var family = fonts.FindFamily(style.Font);
        if (family is { IsPixel: true, PixelGrid: > 0 })
        {
            shaped.Dispose();
            var grid = family.PixelGrid;
            for (var k = MathF.Floor(style.Size / grid) - 1; k >= 1; k--)
            {
                var candidate = Shape(fonts, text, Scaled(style, k * grid));
                if (candidate.Width <= maxWidth)
                {
                    return candidate;
                }

                candidate.Dispose();
            }

            return ShapeEllipsized(fonts, text, Scaled(style, Math.Min(style.Size, grid)), maxWidth);
        }

        var size = shaped.Size;
        while (shaped.Width > maxWidth && size > minSize)
        {
            // Width is close to proportional to size; aim there, then step down in half pixels.
            var guess = MathF.Floor(size * maxWidth / Math.Max(1, shaped.Width) * 2) / 2;
            var next = Math.Max(minSize, Math.Min(size - 0.5f, guess));
            shaped.Dispose();
            size = next;
            shaped = Shape(fonts, text, Scaled(style, size));
        }

        if (shaped.Width <= maxWidth)
        {
            return shaped;
        }

        shaped.Dispose();
        return ShapeEllipsized(fonts, text, Scaled(style, size), maxWidth);
    }

    /// <summary>The style at another size, with letter spacing scaled along.</summary>
    private static TextStyle Scaled(in TextStyle style, float size) =>
        style with { Size = size, LetterSpacing = style.Size > 0 ? style.LetterSpacing * size / style.Size : style.LetterSpacing };

    private static SKFont CreateFont(in ResolvedTypeface typeface, float size, bool smooth)
    {
        var font = new SKFont(typeface.Typeface, size) { BaselineSnap = true };
        if (typeface.Family.IsPixel)
        {
            // Pixel fonts sit on their own grid: hinting or anti-aliasing would only blur it.
            font.Edging = SKFontEdging.Alias;
            font.Hinting = SKFontHinting.None;
            font.Subpixel = false;
        }
        else if (smooth)
        {
            font.Edging = SKFontEdging.Antialias;
            font.Hinting = SKFontHinting.Normal;
            font.Subpixel = true;
            font.LinearMetrics = true;
        }
        else
        {
            font.Edging = SKFontEdging.Alias;
            font.Hinting = SKFontHinting.Full;
            font.Subpixel = false;
        }

        if (typeface.SyntheticItalic)
        {
            font.SkewX = -0.2f;
        }

        return font;
    }

    private static bool TryAdd(SKFont font, int codepoint, List<(SKFont Font, ushort Glyph)> items)
    {
        var glyph = font.GetGlyph(codepoint);
        if (glyph == 0)
        {
            return false;
        }

        items.Add((font, glyph));
        return true;
    }

    /// <summary>Plain equivalents tried in the same font before falling back to another one.</summary>
    private static string? Substitute(int codepoint) => codepoint switch
    {
        0x2212 or 0x2010 or 0x2011 or 0x2012 or 0x2013 or 0x2014 => "-",
        0x2026 => "...",
        0x00A0 or 0x2002 or 0x2003 or 0x2007 or 0x2008 or 0x2009 or 0x200A or 0x202F => " ",
        0x2018 or 0x2019 or 0x2032 => "'",
        0x201C or 0x201D or 0x2033 => "\"",
        _ => null,
    };
}
