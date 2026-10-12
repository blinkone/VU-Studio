using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Rendering;
using VuStudio.Rendering.Imaging;

namespace VuStudio.Tests.Rendering;

public class ImagingTests
{
    [Theory]
    [InlineData(DitherMode.FloydSteinberg)]
    [InlineData(DitherMode.Atkinson)]
    [InlineData(DitherMode.Bayer)]
    public void Mid_grey_dithers_to_a_mixed_pattern(DitherMode mode)
    {
        const int size = 64;
        var grey = Enumerable.Repeat((byte)128, size * size).ToArray();
        var bitmap = Dithering.Dither(grey, size, size, mode);
        var black = bitmap.CountBlack() / (double)(size * size);
        Assert.InRange(black, 0.4, 0.6);

        // Mixed locally, not split into large solid areas.
        for (var y = 0; y < size; y += 8)
        {
            for (var x = 0; x < size; x += 8)
            {
                var block = 0;
                for (var dy = 0; dy < 8; dy++)
                {
                    for (var dx = 0; dx < 8; dx++)
                    {
                        block += bitmap[x + dx, y + dy] ? 0 : 1;
                    }
                }

                Assert.InRange(block, 16, 48);
            }
        }
    }

    [Theory]
    [InlineData(DitherMode.Threshold)]
    [InlineData(DitherMode.FloydSteinberg)]
    [InlineData(DitherMode.Atkinson)]
    [InlineData(DitherMode.Bayer)]
    public void Black_and_white_stay_solid_and_tone_is_monotonic(DitherMode mode)
    {
        const int size = 32;
        double Black(byte level) => Dithering.Dither(Enumerable.Repeat(level, size * size).ToArray(), size, size, mode).CountBlack() / (double)(size * size);
        Assert.Equal(1, Black(0));
        Assert.Equal(0, Black(255));
        Assert.True(Black(64) >= Black(128) && Black(128) >= Black(192));
    }

    [Fact]
    public void Threshold_mode_uses_the_threshold()
    {
        var mask = Dithering.Apply([100, 150, 200], 3, 1, DitherMode.Threshold, 160);
        Assert.Equal([false, false, true], mask);
    }

    [Fact]
    public void Image_layers_load_from_the_asset_store_fit_and_dither()
    {
        var store = new MemoryAssetStore();
        var id = store.Add(GradientPng(64, 32), "gradient.png");
        var renderer = new FaceRenderer(store);
        var layer = new ImageLayer { AssetId = id, X = 0, Y = 0, Width = 200, Height = 144, Fit = ImageFit.Contain };
        var doc = new FaceDocument { Layers = [layer] };
        var bitmap = renderer.Render(doc, new FaceRenderContext());

        // Contain: 200 × 100 centred → rows 0–21 and 122–143 stay paper; the gradient goes dark → light.
        Assert.Equal(0, RenderingTestData.CountBlack(bitmap, 0, 0, 199, 20));
        Assert.Equal(0, RenderingTestData.CountBlack(bitmap, 0, 123, 199, 143));
        var left = RenderingTestData.CountBlack(bitmap, 0, 30, 39, 110);
        var middle = RenderingTestData.CountBlack(bitmap, 80, 30, 119, 110);
        var right = RenderingTestData.CountBlack(bitmap, 160, 30, 199, 110);
        Assert.True(left > middle && middle > right);
        Assert.InRange(middle, 600, 2600);

        // Inverting the image, or drawing it with background ink, flips the tones.
        layer.InvertImage = true;
        var inverted = renderer.Render(doc, new FaceRenderContext());
        Assert.True(RenderingTestData.CountBlack(inverted, 0, 30, 39, 110) < RenderingTestData.CountBlack(inverted, 160, 30, 199, 110));

        // Processed images are cached: the store is read once per parameter set.
        var reads = store.Reads;
        renderer.Render(doc, new FaceRenderContext());
        Assert.Equal(reads, store.Reads);

        Assert.Equal(0, new FaceRenderer().Render(doc, new FaceRenderContext()).CountBlack());
        Assert.Equal(0, renderer.Render(new FaceDocument { Layers = [new ImageLayer { AssetId = "missing" }] }, new FaceRenderContext()).CountBlack());
    }

    [Fact]
    public void Transparent_pixels_are_not_drawn()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var store = new MemoryAssetStore();
        var id = store.Add(data.ToArray(), null);
        var renderer = new FaceRenderer(store);
        var doc = new FaceDocument
        {
            Layers =
            [
                new ShapeLayer { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = 200, Height = 144, Fill = true, Stroke = false },
                new ImageLayer { AssetId = id, Width = 200, Height = 144, Fit = ImageFit.Stretch },
            ],
        };
        Assert.Equal(200 * 144, renderer.Render(doc, new FaceRenderContext()).CountBlack());
    }

    private static byte[] GradientPng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var x = 0; x < width; x++)
        {
            var level = (byte)(x * 255 / (width - 1));
            for (var y = 0; y < height; y++)
            {
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class MemoryAssetStore : IAssetStore
    {
        private readonly Dictionary<string, byte[]> _assets = [];

        public int Reads { get; private set; }

        public byte[]? Get(string assetId)
        {
            Reads++;
            return _assets.GetValueOrDefault(assetId);
        }

        public string Add(ReadOnlySpan<byte> content, string? originalName)
        {
            var id = $"asset{_assets.Count}";
            _assets[id] = content.ToArray();
            return id;
        }

        public IReadOnlyList<AssetInfo> List() => [.. _assets.Select(a => new AssetInfo(a.Key, null, a.Value.Length, DateTimeOffset.UnixEpoch))];

        public bool Delete(string assetId) => _assets.Remove(assetId);
    }
}
