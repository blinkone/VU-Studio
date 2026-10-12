using SkiaSharp;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Drawing;

/// <summary>
/// The 8-bit drawing surface of one render: a 200 × 144 grey canvas that only ever receives the two ink
/// colors, thresholded into a <see cref="MonoBitmap"/> at the end. One instance per render call.
/// </summary>
internal sealed class FaceCanvas : IDisposable
{
    public const int Width = MonoBitmap.DialWidth;
    public const int Height = MonoBitmap.DialHeight;

    public FaceCanvas(bool inverted, FaceRenderContext context, FaceAssets assets)
    {
        Bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Gray8, SKAlphaType.Opaque));
        Canvas = new SKCanvas(Bitmap);
        Inverted = inverted;
        Context = context;
        Assets = assets;
        Geometry = context.Geometry ?? NeedleGeometry.Default;
        Canvas.Clear(PaperColor);
    }

    public SKBitmap Bitmap { get; }

    public SKCanvas Canvas { get; }

    /// <summary>Black paper with white ink.</summary>
    public bool Inverted { get; }

    public FaceRenderContext Context { get; }

    public FaceAssets Assets { get; }

    public NeedleGeometry Geometry { get; }

    public SKColor PaperColor => Inverted ? SKColors.Black : SKColors.White;

    /// <summary>Foreground is black on a normal face and white on an inverted one; background is the paper.</summary>
    public SKColor Color(Ink ink) => (ink == Ink.Foreground) != Inverted ? SKColors.Black : SKColors.White;

    /// <summary>The grey level of an ink for direct pixel writes.</summary>
    public byte Level(Ink ink) => Color(ink) == SKColors.Black ? (byte)0 : (byte)255;

    /// <summary>Row-major grey pixels; valid between draw calls (raster canvases draw immediately).</summary>
    public Span<byte> Pixels => Bitmap.GetPixelSpan();

    public int RowBytes => Bitmap.RowBytes;

    public MonoBitmap ToMonoBitmap()
    {
        Canvas.Flush();
        var pixels = Bitmap.GetPixelSpan();
        if (Bitmap.RowBytes == Width)
        {
            return MonoBitmap.FromLuminance(Width, Height, pixels);
        }

        var packed = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            pixels.Slice(y * Bitmap.RowBytes, Width).CopyTo(packed.AsSpan(y * Width, Width));
        }

        return MonoBitmap.FromLuminance(Width, Height, packed);
    }

    public void Dispose()
    {
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}
