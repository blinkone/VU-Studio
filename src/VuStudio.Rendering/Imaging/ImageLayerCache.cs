using System.Collections.Concurrent;
using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Rendering.Drawing;

namespace VuStudio.Rendering.Imaging;

/// <summary>
/// A dithered image placed on the canvas: per pixel 0 = transparent, 1 = dark (drawn with the layer's ink),
/// 2 = light (drawn with the other color).
/// </summary>
internal sealed record ImageMask(int Left, int Top, int Width, int Height, byte[] Pixels)
{
    public const byte Transparent = 0;
    public const byte Dark = 1;
    public const byte Light = 2;
}

/// <summary>
/// Loads uploaded images from the <see cref="IAssetStore"/>, fits, adjusts and dithers them, and caches the
/// result per asset and parameters (bounded, so editing sessions cannot grow it without limit).
/// </summary>
internal sealed class ImageLayerCache
{
    private const int MaxEntries = 48;

    private readonly IAssetStore? _store;
    private readonly ConcurrentDictionary<string, ImageMask?> _cache = new(StringComparer.Ordinal);

    public ImageLayerCache(IAssetStore? store) => _store = store;

    public void Clear() => _cache.Clear();

    public ImageMask? Get(ImageLayer layer)
    {
        if (_store is null || string.IsNullOrWhiteSpace(layer.AssetId))
        {
            return null;
        }

        var box = Num.PixelRect(layer.X, layer.Y, layer.Width, layer.Height);
        if (box.Width < 1 || box.Height < 1)
        {
            return null;
        }

        var key = string.Join('|', layer.AssetId, box.Left, box.Top, box.Width, box.Height, layer.Fit, layer.Dither,
            layer.Threshold, layer.Brightness, layer.Contrast, layer.InvertImage);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bytes = _store.Get(layer.AssetId);
        if (bytes is null || bytes.Length == 0)
        {
            // Not cached: the asset may appear later (e.g. while an upload completes).
            return null;
        }

        var mask = Build(bytes, layer, box);
        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[key] = mask;
        return mask;
    }

    internal static ImageMask? Build(byte[] bytes, ImageLayer layer, SKRectI box)
    {
        using var image = SKImage.FromEncodedData(bytes);
        if (image is null || image.Width < 1 || image.Height < 1)
        {
            return null;
        }

        // Only the part of the box that is on the canvas is rasterised.
        var visible = SKRectI.Intersect(box, new SKRectI(0, 0, FaceCanvas.Width, FaceCanvas.Height));
        if (visible.Width < 1 || visible.Height < 1)
        {
            return null;
        }

        var destination = Fit(layer.Fit, image.Width, image.Height, box);
        var info = new SKImageInfo(visible.Width, visible.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.ClipRect(new SKRect(box.Left - visible.Left, box.Top - visible.Top, box.Right - visible.Left, box.Bottom - visible.Top));
            destination.Offset(-visible.Left, -visible.Top);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image, destination, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }

        var count = visible.Width * visible.Height;
        var luminance = new byte[count];
        var opaque = new bool[count];
        var pixels = bitmap.GetPixelSpan();
        var brightness = Math.Clamp(layer.Brightness, -100, 100) * 2.55;
        var c = Math.Clamp(layer.Contrast, -100, 100) * 2.55;
        var contrast = 259 * (c + 255) / (255 * (259 - c));
        for (var y = 0; y < visible.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, visible.Width * 4);
            for (var x = 0; x < visible.Width; x++)
            {
                var r = row[x * 4];
                var g = row[(x * 4) + 1];
                var b = row[(x * 4) + 2];
                var a = row[(x * 4) + 3];
                var i = (y * visible.Width) + x;
                opaque[i] = a >= 128;
                var l = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                l = ((l - 128) * contrast) + 128 + brightness;
                if (layer.InvertImage)
                {
                    l = 255 - l;
                }

                luminance[i] = (byte)Math.Clamp(Math.Round(l), 0, 255);
            }
        }

        var white = Dithering.Apply(luminance, visible.Width, visible.Height, layer.Dither, layer.Threshold);
        var mask = new byte[count];
        for (var i = 0; i < count; i++)
        {
            mask[i] = !opaque[i] ? ImageMask.Transparent : white[i] ? ImageMask.Light : ImageMask.Dark;
        }

        return new ImageMask(visible.Left, visible.Top, visible.Width, visible.Height, mask);
    }

    /// <summary>Where the whole image lands for a fit mode (may extend past the box for Cover/None).</summary>
    internal static SKRect Fit(ImageFit fit, int imageWidth, int imageHeight, SKRectI box)
    {
        float w = imageWidth;
        float h = imageHeight;
        var scale = fit switch
        {
            ImageFit.Contain => Math.Min(box.Width / w, box.Height / h),
            ImageFit.Cover => Math.Max(box.Width / w, box.Height / h),
            ImageFit.None => 1f,
            _ => float.NaN,
        };
        if (float.IsNaN(scale))
        {
            return new SKRect(box.Left, box.Top, box.Right, box.Bottom);
        }

        var dw = w * scale;
        var dh = h * scale;
        var left = box.Left + ((box.Width - dw) / 2);
        var top = box.Top + ((box.Height - dh) / 2);
        return new SKRect(left, top, left + dw, top + dh);
    }
}

/// <summary>Composites a cached <see cref="ImageMask"/> straight into the canvas pixels.</summary>
internal static class ImageDrawer
{
    public static void Draw(FaceCanvas face, ImageLayer layer, ImageLayerCache cache)
    {
        if (cache.Get(layer) is not { } mask)
        {
            return;
        }

        face.Canvas.Flush();
        var pixels = face.Pixels;
        var dark = face.Level(layer.Ink);
        var light = (byte)(255 - dark);
        for (var y = 0; y < mask.Height; y++)
        {
            var row = (mask.Top + y) * face.RowBytes;
            for (var x = 0; x < mask.Width; x++)
            {
                var value = mask.Pixels[(y * mask.Width) + x];
                if (value != ImageMask.Transparent)
                {
                    pixels[row + mask.Left + x] = value == ImageMask.Dark ? dark : light;
                }
            }
        }

        face.Bitmap.NotifyPixelsChanged();
    }
}
